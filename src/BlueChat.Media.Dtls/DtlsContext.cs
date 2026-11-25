using System.Runtime.InteropServices;

namespace BlueChat.Media.Dtls;

/// <summary>
/// One OpenSSL <c>SSL_CTX</c> per node certificate: DTLS 1.2 minimum, use_srtp with the profiles BlueChat accepts,
/// peer certificate required (checked against the SDP fingerprint by <see cref="DtlsSession"/>).
/// </summary>
public sealed unsafe class DtlsContext : IDisposable
{
    /// <summary>RFC 7714 AEAD first, RFC 5764 AES-CM fallback (both mandatory for browsers, RFC 8827 §6.5).</summary>
    public const string SrtpProfiles = "SRTP_AEAD_AES_128_GCM:SRTP_AES128_CM_SHA1_80";

    private IntPtr _ctx;

    public DtlsContext(DtlsCertificate certificate)
    {
        Certificate = certificate;
        _ctx = Native.SSL_CTX_new(Native.DTLS_method());
        if (_ctx == IntPtr.Zero)
        {
            throw new DtlsException("SSL_CTX_new: " + Native.TakeErrors());
        }

        try
        {
            if (Native.SSL_CTX_ctrl(_ctx, Native.SslCtrlSetMinProtoVersion, new CLong(Native.Dtls12Version), IntPtr.Zero).Value != 1)
            {
                throw new DtlsException("min protocol DTLS 1.2: " + Native.TakeErrors());
            }

            // The memory BIO cannot report a path MTU; DtlsSession sets the MTU explicitly.
            Native.SSL_CTX_set_options(_ctx, Native.SslOpNoQueryMtu);
            LoadCertificate(certificate);
            if (Native.SSL_CTX_set_tlsext_use_srtp(_ctx, SrtpProfiles) != 0)
            {
                throw new DtlsException("use_srtp: " + Native.TakeErrors());
            }

            Native.SSL_CTX_set_verify(_ctx, Native.SslVerifyPeer | Native.SslVerifyFailIfNoPeerCert, &Native.AcceptAnyCertificate);
        }
        catch
        {
            Native.SSL_CTX_free(_ctx);
            _ctx = IntPtr.Zero;
            throw;
        }
    }

    public DtlsCertificate Certificate { get; }

    internal IntPtr Handle => _ctx != IntPtr.Zero ? _ctx : throw new ObjectDisposedException(nameof(DtlsContext));

    /// <summary>OpenSSL version number as reported by the loaded library (e.g. 0x30000020 = 3.0.2).</summary>
    public static ulong OpenSslVersion => Native.OpenSSL_version_num().Value;

    private void LoadCertificate(DtlsCertificate certificate)
    {
        fixed (byte* certStart = certificate.CertificateDer)
        fixed (byte* keyStart = certificate.PrivateKeyDer)
        {
            var p = certStart;
            var x509 = Native.d2i_X509(IntPtr.Zero, ref p, new CLong(certificate.CertificateDer.Length));
            if (x509 == IntPtr.Zero)
            {
                throw new DtlsException("d2i_X509: " + Native.TakeErrors());
            }

            try
            {
                if (Native.SSL_CTX_use_certificate(_ctx, x509) != 1)
                {
                    throw new DtlsException("use_certificate: " + Native.TakeErrors());
                }
            }
            finally
            {
                Native.X509_free(x509); // SSL_CTX_use_certificate took its own reference
            }

            var k = keyStart;
            var pkey = Native.d2i_AutoPrivateKey(IntPtr.Zero, ref k, new CLong(certificate.PrivateKeyDer.Length));
            if (pkey == IntPtr.Zero)
            {
                throw new DtlsException("d2i_AutoPrivateKey: " + Native.TakeErrors());
            }

            try
            {
                if (Native.SSL_CTX_use_PrivateKey(_ctx, pkey) != 1 || Native.SSL_CTX_check_private_key(_ctx) != 1)
                {
                    throw new DtlsException("use_PrivateKey: " + Native.TakeErrors());
                }
            }
            finally
            {
                Native.EVP_PKEY_free(pkey);
            }
        }
    }

    public void Dispose()
    {
        if (_ctx != IntPtr.Zero)
        {
            Native.SSL_CTX_free(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}

public sealed class DtlsException(string message) : Exception(message);

internal static class NativeStrings
{
    public static string Read(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}
