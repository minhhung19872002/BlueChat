using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BlueChat.Media.Dtls;

/// <summary>
/// P/Invoke surface of the operating system's OpenSSL 3.x (libssl + libcrypto). Only what DTLS 1.2 with the
/// use_srtp extension (RFC 5764) and the keying-material exporter (RFC 5705) need. No cryptography is done here:
/// OpenSSL performs the whole handshake; we only move datagrams through memory BIOs.
/// C <c>long</c>/<c>unsigned long</c> are 32-bit on Windows, hence <see cref="CLong"/>/<see cref="CULong"/>.
/// </summary>
internal static unsafe partial class Native
{
    private const string Ssl = "bc_ssl";
    private const string Crypto = "bc_crypto";

    // ssl.h / dtls1.h / bio.h constants (OpenSSL 3.0 ABI).
    public const int SslErrorWantRead = 2;
    public const int SslErrorWantWrite = 3;
    public const int SslErrorSyscall = 5;
    public const int SslErrorZeroReturn = 6;
    public const int SslVerifyPeer = 0x01;
    public const int SslVerifyFailIfNoPeerCert = 0x02;
    public const ulong SslOpNoQueryMtu = 0x00001000;
    public const int SslCtrlSetMtu = 17;
    public const int SslCtrlSetMinProtoVersion = 123;
    public const int DtlsCtrlGetTimeout = 73;
    public const int DtlsCtrlHandleTimeout = 74;
    public const int Dtls12Version = 0xFEFD;
    public const int BioCtrlPending = 10;
    public const int BioCSetBufMemEofReturn = 130;

#pragma warning disable CA2255 // the resolver must be in place before the first P/Invoke of this assembly
    [ModuleInitializer]
    internal static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
#pragma warning restore CA2255

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Ssl && name != Crypto)
        {
            return IntPtr.Zero;
        }

        var ssl = name == Ssl;
        foreach (var candidate in Candidates(ssl))
        {
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException(
            $"OpenSSL 3.x ({(ssl ? "libssl" : "libcrypto")}) not found. Install OpenSSL 3 or set BC_OPENSSL_DIR.");
    }

    private static IEnumerable<string> Candidates(bool ssl)
    {
        string[] names;
        if (OperatingSystem.IsWindows())
        {
            names = ssl ? ["libssl-3-x64.dll", "libssl-3.dll"] : ["libcrypto-3-x64.dll", "libcrypto-3.dll"];
        }
        else if (OperatingSystem.IsMacOS())
        {
            names = ssl ? ["libssl.3.dylib"] : ["libcrypto.3.dylib"];
        }
        else
        {
            names = ssl ? ["libssl.so.3"] : ["libcrypto.so.3"];
        }

        var dir = Environment.GetEnvironmentVariable("BC_OPENSSL_DIR");
        foreach (var n in names)
        {
            if (!string.IsNullOrEmpty(dir))
            {
                yield return Path.Combine(dir, n);
            }

            yield return n;
        }
    }

    // ---- libssl ----
    [LibraryImport(Ssl)] public static partial IntPtr DTLS_method();
    [LibraryImport(Ssl)] public static partial IntPtr SSL_CTX_new(IntPtr method);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_free(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial CLong SSL_CTX_ctrl(IntPtr ctx, int cmd, CLong larg, IntPtr parg);
    [LibraryImport(Ssl)] public static partial ulong SSL_CTX_set_options(IntPtr ctx, ulong options);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_use_certificate(IntPtr ctx, IntPtr x509);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_use_PrivateKey(IntPtr ctx, IntPtr pkey);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_check_private_key(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_verify(IntPtr ctx, int mode, delegate* unmanaged[Cdecl]<int, IntPtr, int> callback);

    /// <summary>Returns 0 on success (OpenSSL quirk).</summary>
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_set_tlsext_use_srtp(IntPtr ctx, string profiles);

    [LibraryImport(Ssl)] public static partial IntPtr SSL_new(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial void SSL_free(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_bio(IntPtr ssl, IntPtr rbio, IntPtr wbio);
    [LibraryImport(Ssl)] public static partial void SSL_set_accept_state(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_connect_state(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial CLong SSL_ctrl(IntPtr ssl, int cmd, CLong larg, IntPtr parg);
    [LibraryImport(Ssl)] public static partial ulong SSL_set_options(IntPtr ssl, ulong options);
    [LibraryImport(Ssl)] public static partial int SSL_do_handshake(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial int SSL_is_init_finished(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial int SSL_get_error(IntPtr ssl, int ret);
    [LibraryImport(Ssl)] public static partial int SSL_read(IntPtr ssl, byte* buffer, int length);
    [LibraryImport(Ssl)] public static partial int SSL_shutdown(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get1_peer_certificate(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_selected_srtp_profile(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_version(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_current_cipher(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_CIPHER_get_name(IntPtr cipher);

    [LibraryImport(Ssl)]
    public static partial int SSL_export_keying_material(IntPtr ssl, byte* output, nuint outputLength, byte* label,
        nuint labelLength, byte* context, nuint contextLength, int useContext);

    [LibraryImport(Crypto)] public static partial CULong OpenSSL_version_num();

    // ---- libcrypto ----
    [LibraryImport(Crypto)] public static partial IntPtr BIO_s_mem();
    [LibraryImport(Crypto)] public static partial IntPtr BIO_new(IntPtr method);
    [LibraryImport(Crypto)] public static partial int BIO_free(IntPtr bio);
    [LibraryImport(Crypto)] public static partial int BIO_write(IntPtr bio, byte* data, int length);
    [LibraryImport(Crypto)] public static partial int BIO_read(IntPtr bio, byte* data, int length);
    [LibraryImport(Crypto)] public static partial CLong BIO_ctrl(IntPtr bio, int cmd, CLong larg, IntPtr parg);
    [LibraryImport(Crypto)] public static partial IntPtr d2i_X509(IntPtr reuse, ref byte* input, CLong length);
    [LibraryImport(Crypto)] public static partial int i2d_X509(IntPtr x509, byte** output);
    [LibraryImport(Crypto)] public static partial void X509_free(IntPtr x509);
    [LibraryImport(Crypto)] public static partial IntPtr d2i_AutoPrivateKey(IntPtr reuse, ref byte* input, CLong length);
    [LibraryImport(Crypto)] public static partial void EVP_PKEY_free(IntPtr pkey);
    [LibraryImport(Crypto)] public static partial CULong ERR_get_error();
    [LibraryImport(Crypto)] public static partial void ERR_error_string_n(CULong error, byte* buffer, nuint length);
    [LibraryImport(Crypto)] public static partial void ERR_clear_error();

    /// <summary>Drains the thread's OpenSSL error queue into one message.</summary>
    public static string TakeErrors()
    {
        var parts = new List<string>();
        var buffer = stackalloc byte[256];
        for (var e = ERR_get_error(); e.Value != 0; e = ERR_get_error())
        {
            ERR_error_string_n(e, buffer, 256);
            parts.Add(Marshal.PtrToStringUTF8((IntPtr)buffer) ?? "?");
        }

        return parts.Count == 0 ? "no OpenSSL error queued" : string.Join("; ", parts);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int AcceptAnyCertificate(int preverifyOk, IntPtr storeContext) =>
        // WebRTC certificates are self-signed; trust comes from the SDP a=fingerprint, which DtlsSession checks
        // after the handshake and before any SRTP key is handed out (RFC 8122 §5, RFC 8842 §5).
        1;
}
