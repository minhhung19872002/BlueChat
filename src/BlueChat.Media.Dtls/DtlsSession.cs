using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace BlueChat.Media.Dtls;

public enum DtlsRole
{
    /// <summary>We answered <c>a=setup:passive</c>: the browser sends the ClientHello.</summary>
    Server,

    /// <summary>We answered <c>a=setup:active</c>.</summary>
    Client,
}

public enum DtlsState
{
    New,
    Handshaking,
    Connected,
    Failed,
    Closed,
}

public enum SrtpProfile
{
    /// <summary>RFC 7714 AEAD_AES_128_GCM: 16-byte key, 12-byte salt.</summary>
    AeadAes128Gcm,

    /// <summary>RFC 5764 SRTP_AES128_CM_HMAC_SHA1_80: 16-byte key, 14-byte salt.</summary>
    Aes128CmHmacSha1x80,
}

/// <summary>SRTP master keys exported per RFC 5764 §4.2, already split into our side and the peer's side.</summary>
public sealed record SrtpKeyMaterial(
    SrtpProfile Profile, byte[] LocalMasterKey, byte[] LocalMasterSalt, byte[] RemoteMasterKey, byte[] RemoteMasterSalt);

/// <summary>
/// One DTLS association over an ICE-selected path. Datagram in → <see cref="Receive"/>; datagrams out →
/// <see cref="TakeOutgoing"/>; call <see cref="HandleTimeout"/> on a timer for retransmission (RFC 6347 §4.2.4).
/// No socket I/O here, so the session can be tested and fuzzed without a network.
/// </summary>
public sealed unsafe class DtlsSession : IDisposable
{
    private const string ExporterLabel = "EXTRACTOR-dtls_srtp"; // RFC 5764 §4.2
    private const int RecordHeaderLength = 13; // RFC 6347 §4.1

    private readonly object _sync = new();
    private readonly DtlsRole _role;
    private readonly string _expectedPeerFingerprint;
    private readonly int _mtu;
    private IntPtr _ssl;
    private IntPtr _wbio;
    private IntPtr _rbio;

    public DtlsSession(DtlsContext context, DtlsRole role, string expectedPeerFingerprint, int mtu = 1200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mtu, 256);
        _role = role;
        _expectedPeerFingerprint = expectedPeerFingerprint;
        _mtu = mtu;
        _ssl = Native.SSL_new(context.Handle);
        if (_ssl == IntPtr.Zero)
        {
            throw new DtlsException("SSL_new: " + Native.TakeErrors());
        }

        _rbio = Native.BIO_new(Native.BIO_s_mem());
        _wbio = Native.BIO_new(Native.BIO_s_mem());
        // An empty read BIO must mean "retry later", not end of stream.
        Native.BIO_ctrl(_rbio, Native.BioCSetBufMemEofReturn, new CLong(-1), IntPtr.Zero);
        Native.SSL_set_bio(_ssl, _rbio, _wbio); // the SSL object now owns both BIOs
        Native.SSL_ctrl(_ssl, Native.SslCtrlSetMtu, new CLong(mtu - 28), IntPtr.Zero); // minus IPv4 + UDP headers
        if (role == DtlsRole.Server)
        {
            Native.SSL_set_accept_state(_ssl);
        }
        else
        {
            Native.SSL_set_connect_state(_ssl);
        }
    }

    public DtlsState State { get; private set; } = DtlsState.New;

    public string? FailureReason { get; private set; }

    public string? PeerFingerprint { get; private set; }

    public string? ProtocolVersion { get; private set; }

    public string? CipherSuite { get; private set; }

    public SrtpKeyMaterial? Keys { get; private set; }

    /// <summary>Client role: produce the ClientHello. Server role: only marks the session as waiting.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (State != DtlsState.New)
            {
                return;
            }

            State = DtlsState.Handshaking;
            if (_role == DtlsRole.Client)
            {
                Advance();
            }
        }
    }

    /// <summary>Feeds one received datagram (first byte 20–63, RFC 7983).</summary>
    public void Receive(ReadOnlySpan<byte> datagram)
    {
        lock (_sync)
        {
            if (State is DtlsState.Failed or DtlsState.Closed || datagram.IsEmpty)
            {
                return;
            }

            if (State == DtlsState.New)
            {
                State = DtlsState.Handshaking;
            }

            fixed (byte* p = datagram)
            {
                if (Native.BIO_write(_rbio, p, datagram.Length) != datagram.Length)
                {
                    Fail("bio_write");
                    return;
                }
            }

            Advance();
        }
    }

    /// <summary>Retransmits the last flight when OpenSSL's timer has expired.</summary>
    public void HandleTimeout()
    {
        lock (_sync)
        {
            if (State == DtlsState.Handshaking)
            {
                if (Native.SSL_ctrl(_ssl, Native.DtlsCtrlHandleTimeout, new CLong(0), IntPtr.Zero).Value < 0)
                {
                    Fail("handshake_timeout: " + Native.TakeErrors());
                }
            }
        }
    }

    /// <summary>Pending datagrams, records packed up to the MTU without splitting a record.</summary>
    public IReadOnlyList<byte[]> TakeOutgoing()
    {
        lock (_sync)
        {
            var pending = (int)Native.BIO_ctrl(_wbio, Native.BioCtrlPending, new CLong(0), IntPtr.Zero).Value;
            if (pending <= 0)
            {
                return [];
            }

            var buffer = new byte[pending];
            int read;
            fixed (byte* p = buffer)
            {
                read = Native.BIO_read(_wbio, p, pending);
            }

            return read <= 0 ? [] : PackRecords(buffer.AsSpan(0, read), _mtu);
        }
    }

    internal static List<byte[]> PackRecords(ReadOnlySpan<byte> data, int mtu)
    {
        var result = new List<byte[]>();
        var start = 0;
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < RecordHeaderLength)
            {
                break; // malformed tail: send what we have as is
            }

            var recordLength = RecordHeaderLength + BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 11, 2));
            if (offset + recordLength > data.Length)
            {
                break;
            }

            if (offset > start && offset + recordLength - start > mtu)
            {
                result.Add(data[start..offset].ToArray());
                start = offset;
            }

            offset += recordLength;
        }

        if (start < data.Length)
        {
            result.Add(data[start..].ToArray());
        }

        return result;
    }

    private void Advance()
    {
        if (State == DtlsState.Handshaking)
        {
            var r = Native.SSL_do_handshake(_ssl);
            if (r == 1)
            {
                Complete();
                return;
            }

            var error = Native.SSL_get_error(_ssl, r);
            if (error is Native.SslErrorWantRead or Native.SslErrorWantWrite)
            {
                return;
            }

            Fail($"handshake ({error}): {Native.TakeErrors()}");
            return;
        }

        if (State == DtlsState.Connected)
        {
            // WebRTC sends no DTLS application data on an SRTP-only path; reading processes alerts (close_notify).
            var buffer = stackalloc byte[2048];
            while (true)
            {
                var n = Native.SSL_read(_ssl, buffer, 2048);
                if (n > 0)
                {
                    continue;
                }

                var error = Native.SSL_get_error(_ssl, n);
                if (error == Native.SslErrorZeroReturn)
                {
                    State = DtlsState.Closed;
                }
                else if (error is not (Native.SslErrorWantRead or Native.SslErrorWantWrite))
                {
                    Fail($"read ({error}): {Native.TakeErrors()}");
                }

                return;
            }
        }
    }

    private void Complete()
    {
        var peer = Native.SSL_get1_peer_certificate(_ssl);
        if (peer == IntPtr.Zero)
        {
            Fail("no_peer_certificate");
            return;
        }

        try
        {
            byte* der = null;
            var length = Native.i2d_X509(peer, &der);
            if (length <= 0)
            {
                Fail("i2d_X509: " + Native.TakeErrors());
                return;
            }

            // i2d_X509 allocated `der` with OPENSSL_malloc; copy then let OpenSSL keep ownership of nothing extra.
            var copy = new ReadOnlySpan<byte>(der, length).ToArray();
            CryptoFree(der);
            PeerFingerprint = DtlsCertificate.FingerprintOf(copy);
        }
        finally
        {
            Native.X509_free(peer);
        }

        if (!string.Equals(PeerFingerprint, _expectedPeerFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Fail("fingerprint_mismatch");
            return;
        }

        var profilePtr = Native.SSL_get_selected_srtp_profile(_ssl);
        if (profilePtr == IntPtr.Zero)
        {
            Fail("no_srtp_profile");
            return;
        }

        // struct srtp_protection_profile_st { const char *name; unsigned long id; }
        var profileName = NativeStrings.Read(*(IntPtr*)profilePtr);
        SrtpProfile profile;
        int keyLength = 16, saltLength;
        switch (profileName)
        {
            case "SRTP_AEAD_AES_128_GCM":
                profile = SrtpProfile.AeadAes128Gcm;
                saltLength = 12;
                break;
            case "SRTP_AES128_CM_SHA1_80":
                profile = SrtpProfile.Aes128CmHmacSha1x80;
                saltLength = 14;
                break;
            default:
                Fail("unsupported_srtp_profile: " + profileName);
                return;
        }

        var material = new byte[2 * (keyLength + saltLength)];
        var label = Encoding.ASCII.GetBytes(ExporterLabel);
        fixed (byte* m = material)
        fixed (byte* l = label)
        {
            if (Native.SSL_export_keying_material(_ssl, m, (nuint)material.Length, l, (nuint)label.Length, null, 0, 0) != 1)
            {
                Fail("export_keying_material: " + Native.TakeErrors());
                return;
            }
        }

        // RFC 5764 §4.2: client_key | server_key | client_salt | server_salt
        var clientKey = material.AsSpan(0, keyLength).ToArray();
        var serverKey = material.AsSpan(keyLength, keyLength).ToArray();
        var clientSalt = material.AsSpan(2 * keyLength, saltLength).ToArray();
        var serverSalt = material.AsSpan(2 * keyLength + saltLength, saltLength).ToArray();
        Keys = _role == DtlsRole.Client
            ? new SrtpKeyMaterial(profile, clientKey, clientSalt, serverKey, serverSalt)
            : new SrtpKeyMaterial(profile, serverKey, serverSalt, clientKey, clientSalt);
        ProtocolVersion = NativeStrings.Read(Native.SSL_get_version(_ssl));
        CipherSuite = NativeStrings.Read(Native.SSL_CIPHER_get_name(Native.SSL_get_current_cipher(_ssl)));
        State = DtlsState.Connected;
    }

    private static void CryptoFree(byte* p) => NativeFree.CRYPTO_free(p, null, 0);

    private void Fail(string reason)
    {
        State = DtlsState.Failed;
        FailureReason = reason;
        Native.ERR_clear_error();
    }

    /// <summary>Sends close_notify (RFC 5246 §7.2.1); pick it up with <see cref="TakeOutgoing"/>.</summary>
    public void Close()
    {
        lock (_sync)
        {
            if (State == DtlsState.Connected)
            {
                _ = Native.SSL_shutdown(_ssl);
                State = DtlsState.Closed;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_ssl != IntPtr.Zero)
            {
                Native.SSL_free(_ssl); // frees both BIOs
                _ssl = IntPtr.Zero;
                _rbio = IntPtr.Zero;
                _wbio = IntPtr.Zero;
            }
        }
    }
}

internal static unsafe partial class NativeFree
{
    [System.Runtime.InteropServices.LibraryImport("bc_crypto", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    public static partial void CRYPTO_free(void* p, string? file, int line);
}
