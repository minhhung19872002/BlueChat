using System.Security.Cryptography;

namespace BlueChat.Media.Srtp;

/// <summary>SRTP key derivation (RFC 3711 §4.3) with key_derivation_rate = 0, the only rate WebRTC uses.</summary>
internal static class SrtpKdf
{
    // RFC 3711 §4.3.2 labels
    public const byte RtpEncryption = 0x00;
    public const byte RtpAuthentication = 0x01;
    public const byte RtpSalt = 0x02;
    public const byte RtcpEncryption = 0x03;
    public const byte RtcpAuthentication = 0x04;
    public const byte RtcpSalt = 0x05;

    /// <summary>
    /// RFC 3711 §4.3.1/§4.3.3: x = (label || index DIV kdr) XOR master_salt, where index DIV kdr = 0 (48 bits), then the
    /// AES-CM PRF keystream with IV = x · 2^16. The label lands on octet 7 of the 112-bit salt. A 96-bit master salt
    /// (AEAD_AES_128_GCM, RFC 7714 §11) is right-padded with zero octets to 112 bits, as libsrtp does.
    /// </summary>
    public static byte[] Derive(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte label, int length)
    {
        if (masterSalt.Length > 14)
        {
            throw new ArgumentException("master salt longer than 112 bits", nameof(masterSalt));
        }

        Span<byte> x = stackalloc byte[16];
        x.Clear();
        masterSalt.CopyTo(x);
        x[7] ^= label;

        using var aes = Aes.Create();
        aes.Key = masterKey.ToArray();
        var blocks = (length + 15) / 16;
        var input = new byte[blocks * 16];
        for (var i = 0; i < blocks; i++)
        {
            x.CopyTo(input.AsSpan(i * 16));
            input[i * 16 + 14] = (byte)(i >> 8);
            input[i * 16 + 15] = (byte)i;
        }

        var output = aes.EncryptEcb(input, PaddingMode.None);
        return output[..length];
    }
}
