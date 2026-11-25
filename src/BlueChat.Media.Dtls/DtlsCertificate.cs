using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueChat.Media.Dtls;

/// <summary>
/// Self-signed ECDSA P-256 certificate of a media node (RFC 8827 §6.5: certificates are not validated by a CA,
/// they are pinned by the SDP fingerprint). Generated with .NET cryptography; OpenSSL only receives the DER bytes.
/// </summary>
public sealed class DtlsCertificate
{
    private DtlsCertificate(byte[] certificateDer, byte[] privateKeyDer, DateTimeOffset notAfter)
    {
        CertificateDer = certificateDer;
        PrivateKeyDer = privateKeyDer;
        NotAfter = notAfter;
        Fingerprint = FingerprintOf(certificateDer);
    }

    public byte[] CertificateDer { get; }

    /// <summary>SEC1 ECPrivateKey DER (RFC 5915).</summary>
    internal byte[] PrivateKeyDer { get; }

    public DateTimeOffset NotAfter { get; }

    /// <summary>SHA-256 fingerprint as written in <c>a=fingerprint:sha-256 …</c> (RFC 8122 §5).</summary>
    public string Fingerprint { get; }

    public static DtlsCertificate Generate(TimeProvider clock, TimeSpan lifetime)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BlueChat media", key, HashAlgorithmName.SHA256);
        var now = clock.GetUtcNow();
        var notAfter = now + lifetime;
        // Serial: 16 random bytes, top bit cleared so the DER INTEGER stays positive.
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var cert = request.Create(request.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
            now.AddDays(-1), notAfter, serial);
        return new DtlsCertificate(cert.RawData, key.ExportECPrivateKey(), notAfter);
    }

    /// <summary>Uppercase hex pairs separated by colons, as browsers emit and compare them.</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> certificateDer) =>
        string.Join(':', SHA256.HashData(certificateDer).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
