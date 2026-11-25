using System.Buffers.Binary;
using System.Security.Cryptography;
using BlueChat.Media.Dtls;

namespace BlueChat.Media.Srtp;

/// <summary>
/// One direction of SRTP + SRTCP (RFC 3711 §3.2 cryptographic context): session keys for RTP and RTCP and the
/// per-SSRC state (ROC, highest sequence, replay windows, SRTCP index). Not thread-safe: the owner serializes calls.
/// </summary>
internal sealed class SrtpContext : IDisposable
{
    public const int GcmTagLength = 16; // RFC 7714 §14.2 AEAD_AES_128_GCM
    public const int HmacTagLength = 10; // RFC 3711 §5.2 HMAC-SHA1-80
    private const int EsrtcpLength = 4; // RFC 3711 §3.4 E-flag + 31-bit SRTCP index
    private const uint MaxSrtcpIndex = 0x7FFFFFFF;

    private readonly bool _gcm;
    private readonly int _maxStreams;
    private readonly int _maxPacketLength;
    private readonly byte[] _rtpSalt;
    private readonly byte[] _rtcpSalt;
    private readonly AesGcm? _rtpGcm;
    private readonly AesGcm? _rtcpGcm;
    private readonly AesCounterMode? _rtpCtr;
    private readonly AesCounterMode? _rtcpCtr;
    private readonly IncrementalHash? _rtpHmac;
    private readonly IncrementalHash? _rtcpHmac;
    private readonly Dictionary<uint, RtpStream> _rtp = [];
    private readonly Dictionary<uint, RtcpStream> _rtcp = [];
    private byte[] _aadScratch = new byte[256];

    private SrtpContext(SrtpProfile profile, byte[] rtpKey, byte[] rtpSalt, byte[]? rtpAuth, byte[] rtcpKey,
        byte[] rtcpSalt, byte[]? rtcpAuth, int maxStreams, int maxPacketLength)
    {
        _gcm = profile == SrtpProfile.AeadAes128Gcm;
        _maxStreams = maxStreams;
        _maxPacketLength = maxPacketLength;
        _rtpSalt = rtpSalt;
        _rtcpSalt = rtcpSalt;
        if (_gcm)
        {
            _rtpGcm = new AesGcm(rtpKey, GcmTagLength);
            _rtcpGcm = new AesGcm(rtcpKey, GcmTagLength);
        }
        else
        {
            _rtpCtr = new AesCounterMode(rtpKey);
            _rtcpCtr = new AesCounterMode(rtcpKey);
            _rtpHmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, rtpAuth!);
            _rtcpHmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, rtcpAuth!);
        }
    }

    public int RtpOverhead => _gcm ? GcmTagLength : HmacTagLength;

    public int RtcpOverhead => (_gcm ? GcmTagLength : HmacTagLength) + EsrtcpLength;

    public int StreamCount => _rtp.Count;

    /// <summary>Derives all session keys from the master key and salt (RFC 3711 §4.3, RFC 7714 §11).</summary>
    public static SrtpContext FromMaster(SrtpProfile profile, byte[] masterKey, byte[] masterSalt, int maxStreams, int maxPacketLength)
    {
        var gcm = profile == SrtpProfile.AeadAes128Gcm;
        ValidateMaster(profile, masterKey, masterSalt);
        var saltLength = gcm ? 12 : 14;
        return new SrtpContext(profile,
            SrtpKdf.Derive(masterKey, masterSalt, SrtpKdf.RtpEncryption, 16),
            SrtpKdf.Derive(masterKey, masterSalt, SrtpKdf.RtpSalt, saltLength),
            gcm ? null : SrtpKdf.Derive(masterKey, masterSalt, SrtpKdf.RtpAuthentication, 20),
            SrtpKdf.Derive(masterKey, masterSalt, SrtpKdf.RtcpEncryption, 16),
            SrtpKdf.Derive(masterKey, masterSalt, SrtpKdf.RtcpSalt, saltLength),
            gcm ? null : SrtpKdf.Derive(masterKey, masterSalt, SrtpKdf.RtcpAuthentication, 20),
            maxStreams, maxPacketLength);
    }

    /// <summary>Test hook: session keys given directly (RFC 7714 §16–17 vectors are stated as session keys).</summary>
    internal static SrtpContext FromSessionKeys(SrtpProfile profile, byte[] rtpKey, byte[] rtpSalt, byte[]? rtpAuth,
        byte[] rtcpKey, byte[] rtcpSalt, byte[]? rtcpAuth, int maxStreams = 64, int maxPacketLength = 65536) =>
        new(profile, rtpKey, rtpSalt, rtpAuth, rtcpKey, rtcpSalt, rtcpAuth, maxStreams, maxPacketLength);

    private static void ValidateMaster(SrtpProfile profile, byte[] key, byte[] salt)
    {
        var expectedSalt = profile == SrtpProfile.AeadAes128Gcm ? 12 : 14;
        if (key.Length != 16 || salt.Length != expectedSalt)
        {
            throw new ArgumentException($"{profile} needs a 16-octet master key and a {expectedSalt}-octet master salt");
        }
    }

    /// <summary>Test hook: next SRTCP index the sender uses for <paramref name="ssrc"/>.</summary>
    internal void SetNextRtcpIndex(uint ssrc, uint index) => GetOrAddRtcp(ssrc)!.NextIndex = index;

    /// <summary>Test hook: sender/receiver RTP state of <paramref name="ssrc"/>.</summary>
    internal (uint Roc, ushort HighestSeq)? RtpState(uint ssrc) =>
        _rtp.TryGetValue(ssrc, out var s) && s.Started ? (s.Roc, s.HighestSeq) : null;

    // ------------------------------------------------------------------ RTP

    /// <summary>RFC 3711 §3.3 (sender) / RFC 7714 §8.1: encrypts the payload in place and appends the tag.</summary>
    public SrtpResult ProtectRtp(Span<byte> buffer, int length, out int protectedLength)
    {
        protectedLength = 0;
        if (length > _maxPacketLength)
        {
            return SrtpResult.TooLarge;
        }

        if (!TryRtpHeaderLength(buffer[..length], out var headerLength))
        {
            return length < 12 ? SrtpResult.TooShort : SrtpResult.Malformed;
        }

        if (buffer.Length < length + RtpOverhead)
        {
            return SrtpResult.BufferTooSmall;
        }

        var seq = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer[8..]);
        var stream = GetOrAddRtp(ssrc);
        if (stream is null)
        {
            return SrtpResult.TooManyStreams;
        }

        var v = stream.EstimateRoc(seq);
        if (v < 0)
        {
            return SrtpResult.Replayed; // would reuse an index from before the stream started
        }

        if (v > uint.MaxValue)
        {
            return SrtpResult.IndexExhausted;
        }

        var roc = (uint)v;
        if (_gcm)
        {
            Span<byte> iv = stackalloc byte[12];
            RtpGcmIv(iv, ssrc, roc, seq);
            var payload = buffer[headerLength..length];
            _rtpGcm!.Encrypt(iv, payload, payload, buffer.Slice(length, GcmTagLength), buffer[..headerLength]);
        }
        else
        {
            Span<byte> iv = stackalloc byte[16];
            CtrIv(iv, _rtpSalt, ssrc, ((ulong)roc << 16) | seq);
            _rtpCtr!.Apply(iv, buffer[headerLength..length]);
            HmacTag(_rtpHmac!, buffer[..length], roc, buffer.Slice(length, HmacTagLength));
        }

        stream.Update(seq, v);
        protectedLength = length + RtpOverhead;
        return SrtpResult.Ok;
    }

    /// <summary>RFC 3711 §3.3 (receiver) / RFC 7714 §8.2: verifies, checks replay, decrypts in place.</summary>
    public SrtpResult UnprotectRtp(Span<byte> packet, out int plainLength)
    {
        plainLength = 0;
        var tagLength = RtpOverhead;
        if (packet.Length > _maxPacketLength + tagLength)
        {
            return SrtpResult.TooLarge;
        }

        if (packet.Length < 12 + tagLength)
        {
            return SrtpResult.TooShort;
        }

        var bodyEnd = packet.Length - tagLength;
        if (!TryRtpHeaderLength(packet[..bodyEnd], out var headerLength))
        {
            return SrtpResult.Malformed;
        }

        var seq = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        var known = _rtp.TryGetValue(ssrc, out var stream);
        if (!known && _rtp.Count >= _maxStreams)
        {
            return SrtpResult.TooManyStreams;
        }

        // RFC 3711 §3.3.1: estimate the index from the stream state (a new stream starts at ROC 0).
        var v = known ? stream!.EstimateRoc(seq) : 0;
        if (v is < 0 or > uint.MaxValue)
        {
            return SrtpResult.Replayed;
        }

        var roc = (uint)v;
        var index = ((ulong)roc << 16) | seq;
        if (known && !stream!.Window.Check(index))
        {
            return SrtpResult.Replayed; // §3.3.2, before spending crypto on it
        }

        if (_gcm)
        {
            Span<byte> iv = stackalloc byte[12];
            RtpGcmIv(iv, ssrc, roc, seq);
            var cipher = packet[headerLength..bodyEnd];
            try
            {
                _rtpGcm!.Decrypt(iv, cipher, packet.Slice(bodyEnd, GcmTagLength), cipher, packet[..headerLength]);
            }
            catch (AuthenticationTagMismatchException)
            {
                return SrtpResult.AuthenticationFailed;
            }
        }
        else
        {
            Span<byte> tag = stackalloc byte[HmacTagLength];
            HmacTag(_rtpHmac!, packet[..bodyEnd], roc, tag);
            if (!CryptographicOperations.FixedTimeEquals(tag, packet.Slice(bodyEnd, HmacTagLength)))
            {
                return SrtpResult.AuthenticationFailed;
            }

            Span<byte> iv = stackalloc byte[16];
            CtrIv(iv, _rtpSalt, ssrc, index);
            _rtpCtr!.Apply(iv, packet[headerLength..bodyEnd]);
        }

        if (!known)
        {
            stream = new RtpStream();
            _rtp.Add(ssrc, stream);
        }

        stream!.Update(seq, v);
        stream.Window.Accept(index);
        plainLength = bodyEnd;
        return SrtpResult.Ok;
    }

    // ------------------------------------------------------------------ RTCP

    /// <summary>RFC 3711 §3.4 / RFC 7714 §9.1: encrypts everything after the first 8 octets (E = 1), appends tag and ESRTCP word.</summary>
    public SrtpResult ProtectRtcp(Span<byte> buffer, int length, out int protectedLength)
    {
        protectedLength = 0;
        if (length > _maxPacketLength)
        {
            return SrtpResult.TooLarge;
        }

        if (length < 8)
        {
            return SrtpResult.TooShort;
        }

        if (buffer[0] >> 6 != 2)
        {
            return SrtpResult.Malformed;
        }

        if (buffer.Length < length + RtcpOverhead)
        {
            return SrtpResult.BufferTooSmall;
        }

        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);
        var stream = GetOrAddRtcp(ssrc);
        if (stream is null)
        {
            return SrtpResult.TooManyStreams;
        }

        if (stream.NextIndex > MaxSrtcpIndex)
        {
            return SrtpResult.IndexExhausted;
        }

        var index = stream.NextIndex;
        var esrtcp = 0x80000000u | index;
        if (_gcm)
        {
            // Layout (RFC 7714 §17): header(8) | cipher | tag(16) | ESRTCP(4); AAD = header || ESRTCP.
            Span<byte> iv = stackalloc byte[12];
            RtcpGcmIv(iv, ssrc, index);
            Span<byte> aad = stackalloc byte[12];
            buffer[..8].CopyTo(aad);
            BinaryPrimitives.WriteUInt32BigEndian(aad[8..], esrtcp);
            var payload = buffer[8..length];
            _rtcpGcm!.Encrypt(iv, payload, payload, buffer.Slice(length, GcmTagLength), aad);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[(length + GcmTagLength)..], esrtcp);
        }
        else
        {
            // Layout (RFC 3711 §3.4): header(8) | cipher | ESRTCP(4) | tag(10); the tag covers ESRTCP.
            Span<byte> iv = stackalloc byte[16];
            CtrIv(iv, _rtcpSalt, ssrc, index);
            _rtcpCtr!.Apply(iv, buffer[8..length]);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[length..], esrtcp);
            HmacTag(_rtcpHmac!, buffer[..(length + EsrtcpLength)], null, buffer.Slice(length + EsrtcpLength, HmacTagLength));
        }

        stream.NextIndex++;
        protectedLength = length + RtcpOverhead;
        return SrtpResult.Ok;
    }

    /// <summary>RFC 3711 §3.4 / RFC 7714 §9.2: verifies, checks replay, decrypts when E = 1. Accepts tag-only (E = 0) packets.</summary>
    public SrtpResult UnprotectRtcp(Span<byte> packet, out int plainLength)
    {
        plainLength = 0;
        var overhead = RtcpOverhead;
        if (packet.Length > _maxPacketLength + overhead)
        {
            return SrtpResult.TooLarge;
        }

        if (packet.Length < 8 + overhead)
        {
            return SrtpResult.TooShort;
        }

        if (packet[0] >> 6 != 2)
        {
            return SrtpResult.Malformed;
        }

        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        var known = _rtcp.TryGetValue(ssrc, out var stream);
        if (!known && _rtcp.Count >= _maxStreams)
        {
            return SrtpResult.TooManyStreams;
        }

        var bodyEnd = packet.Length - overhead;
        uint esrtcp;
        if (_gcm)
        {
            esrtcp = BinaryPrimitives.ReadUInt32BigEndian(packet[^EsrtcpLength..]);
        }
        else
        {
            esrtcp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(bodyEnd, EsrtcpLength));
        }

        var encrypted = (esrtcp & 0x80000000u) != 0;
        var index = esrtcp & MaxSrtcpIndex;
        if (known && !stream!.Window.Check(index))
        {
            return SrtpResult.Replayed;
        }

        if (_gcm)
        {
            Span<byte> iv = stackalloc byte[12];
            RtcpGcmIv(iv, ssrc, index);
            var tag = packet.Slice(bodyEnd, GcmTagLength);
            try
            {
                if (encrypted)
                {
                    Span<byte> aad = stackalloc byte[12];
                    packet[..8].CopyTo(aad);
                    BinaryPrimitives.WriteUInt32BigEndian(aad[8..], esrtcp);
                    var cipher = packet[8..bodyEnd];
                    _rtcpGcm!.Decrypt(iv, cipher, tag, cipher, aad);
                }
                else
                {
                    // E = 0 (RFC 7714 §17.3): the whole packet plus ESRTCP is AAD, the cipher is only the tag.
                    var aadLength = bodyEnd + EsrtcpLength;
                    if (_aadScratch.Length < aadLength)
                    {
                        _aadScratch = new byte[aadLength];
                    }

                    var aad = _aadScratch.AsSpan(0, aadLength);
                    packet[..bodyEnd].CopyTo(aad);
                    BinaryPrimitives.WriteUInt32BigEndian(aad[bodyEnd..], esrtcp);
                    _rtcpGcm!.Decrypt(iv, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, aad);
                }
            }
            catch (AuthenticationTagMismatchException)
            {
                return SrtpResult.AuthenticationFailed;
            }
        }
        else
        {
            Span<byte> tag = stackalloc byte[HmacTagLength];
            HmacTag(_rtcpHmac!, packet[..(bodyEnd + EsrtcpLength)], null, tag);
            if (!CryptographicOperations.FixedTimeEquals(tag, packet[^HmacTagLength..]))
            {
                return SrtpResult.AuthenticationFailed;
            }

            if (encrypted)
            {
                Span<byte> iv = stackalloc byte[16];
                CtrIv(iv, _rtcpSalt, ssrc, index);
                _rtcpCtr!.Apply(iv, packet[8..bodyEnd]);
            }
        }

        if (!known)
        {
            stream = new RtcpStream();
            _rtcp.Add(ssrc, stream);
        }

        stream!.Window.Accept(index);
        plainLength = bodyEnd;
        return SrtpResult.Ok;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>RTP header length: fixed 12 + CSRC list + extension (RFC 3550 §5.1, §5.3.1). False if not RTP v2 or truncated.</summary>
    internal static bool TryRtpHeaderLength(ReadOnlySpan<byte> packet, out int headerLength)
    {
        headerLength = 0;
        if (packet.Length < 12 || packet[0] >> 6 != 2)
        {
            return false;
        }

        var length = 12 + 4 * (packet[0] & 0x0F);
        if ((packet[0] & 0x10) != 0)
        {
            if (length + 4 > packet.Length)
            {
                return false;
            }

            length += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(packet[(length + 2)..]);
        }

        if (length > packet.Length)
        {
            return false;
        }

        headerLength = length;
        return true;
    }

    /// <summary>RFC 7714 §8.1: IV = (0x0000 || SSRC || ROC || SEQ) XOR salt.</summary>
    private void RtpGcmIv(Span<byte> iv, uint ssrc, uint roc, ushort seq)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[6..], roc);
        BinaryPrimitives.WriteUInt16BigEndian(iv[10..], seq);
        Xor(iv, _rtpSalt);
    }

    /// <summary>RFC 7714 §9.1: IV = (0x0000 || SSRC || 0x0000 || 0 || SRTCP index) XOR salt.</summary>
    private void RtcpGcmIv(Span<byte> iv, uint ssrc, uint index)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[8..], index & MaxSrtcpIndex);
        Xor(iv, _rtcpSalt);
    }

    /// <summary>RFC 3711 §4.1.1: IV = (k_s · 2^16) XOR (SSRC · 2^64) XOR (i · 2^16), i the 48-bit packet index.</summary>
    private static void CtrIv(Span<byte> iv, ReadOnlySpan<byte> salt, uint ssrc, ulong index)
    {
        iv.Clear();
        salt.CopyTo(iv);
        Span<byte> t = stackalloc byte[16];
        t.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(t[4..], ssrc);
        t[8] = (byte)(index >> 40);
        t[9] = (byte)(index >> 32);
        BinaryPrimitives.WriteUInt32BigEndian(t[10..], (uint)index);
        Xor(iv, t);
    }

    /// <summary>RFC 3711 §4.2.1: HMAC-SHA1 over the authenticated portion (|| ROC for SRTP), truncated (§5.2).</summary>
    private static void HmacTag(IncrementalHash hmac, ReadOnlySpan<byte> authenticated, uint? roc, Span<byte> tag)
    {
        hmac.AppendData(authenticated);
        if (roc is { } r)
        {
            Span<byte> rocBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(rocBytes, r);
            hmac.AppendData(rocBytes);
        }

        Span<byte> full = stackalloc byte[20];
        hmac.TryGetHashAndReset(full, out _);
        full[..tag.Length].CopyTo(tag);
    }

    private static void Xor(Span<byte> target, ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < value.Length && i < target.Length; i++)
        {
            target[i] ^= value[i];
        }
    }

    private RtpStream? GetOrAddRtp(uint ssrc)
    {
        if (_rtp.TryGetValue(ssrc, out var s))
        {
            return s;
        }

        if (_rtp.Count >= _maxStreams)
        {
            return null;
        }

        s = new RtpStream();
        _rtp.Add(ssrc, s);
        return s;
    }

    private RtcpStream? GetOrAddRtcp(uint ssrc)
    {
        if (_rtcp.TryGetValue(ssrc, out var s))
        {
            return s;
        }

        if (_rtcp.Count >= _maxStreams)
        {
            return null;
        }

        s = new RtcpStream();
        _rtcp.Add(ssrc, s);
        return s;
    }

    public void Dispose()
    {
        _rtpGcm?.Dispose();
        _rtcpGcm?.Dispose();
        _rtpCtr?.Dispose();
        _rtcpCtr?.Dispose();
        _rtpHmac?.Dispose();
        _rtcpHmac?.Dispose();
    }

    /// <summary>Per-SSRC RTP state: s_l and ROC (RFC 3711 §3.3.1) plus the receive replay window.</summary>
    private sealed class RtpStream
    {
        public ReplayWindow Window;

        public bool Started { get; private set; }

        public ushort HighestSeq { get; private set; }

        public uint Roc { get; private set; }

        /// <summary>RFC 3711 §3.3.1 / Appendix A: guess v (the ROC of this packet) from s_l and ROC.</summary>
        public long EstimateRoc(ushort seq)
        {
            if (!Started)
            {
                return 0;
            }

            long roc = Roc;
            if (HighestSeq < 32768)
            {
                return seq - HighestSeq > 32768 ? roc - 1 : roc;
            }

            return HighestSeq - 32768 > seq ? roc + 1 : roc;
        }

        /// <summary>RFC 3711 §3.3.1: after authentication, advance ROC/s_l when the packet is the newest.</summary>
        public void Update(ushort seq, long v)
        {
            if (!Started)
            {
                Started = true;
                HighestSeq = seq;
                Roc = (uint)v;
                return;
            }

            if (v > Roc)
            {
                Roc = (uint)v;
                HighestSeq = seq;
            }
            else if (v == Roc && seq > HighestSeq)
            {
                HighestSeq = seq;
            }
        }
    }

    /// <summary>Per-SSRC SRTCP state: sender's next index, receiver's replay window (RFC 3711 §3.4).</summary>
    private sealed class RtcpStream
    {
        public ReplayWindow Window;

        public uint NextIndex { get; set; }
    }
}
