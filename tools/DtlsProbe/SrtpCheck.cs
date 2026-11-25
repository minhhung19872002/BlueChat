using System.Buffers.Binary;
using System.Security.Cryptography;
using BlueChat.Media.Dtls;

namespace DtlsProbe;

/// <summary>
/// Proves the exported keys are the ones the browser uses: derive the SRTP session key and salt (RFC 3711 §4.3,
/// AES-CM PRF, kdr = 0) and open one AEAD_AES_128_GCM packet (RFC 7714 §8). A failed tag means wrong keys.
/// </summary>
internal static class SrtpCheck
{
    public static string TryOpen(SrtpKeyMaterial keys, ReadOnlySpan<byte> packet)
    {
        if (keys.Profile != SrtpProfile.AeadAes128Gcm)
        {
            return "profile_not_gcm";
        }

        if (packet.Length < 12 + 16 || (packet[0] >> 6) != 2)
        {
            return "not_rtp";
        }

        var csrc = packet[0] & 0x0F;
        var headerLength = 12 + 4 * csrc;
        if ((packet[0] & 0x10) != 0)
        {
            var words = BinaryPrimitives.ReadUInt16BigEndian(packet[(headerLength + 2)..]);
            headerLength += 4 + 4 * words;
        }

        var seq = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        var sessionKey = Derive(keys.RemoteMasterKey, keys.RemoteMasterSalt, 0x00, 16);
        var sessionSalt = Derive(keys.RemoteMasterKey, keys.RemoteMasterSalt, 0x02, 12);

        // RFC 7714 §8.1: IV = (00 00 || SSRC || ROC || SEQ) XOR salt; ROC = 0 for the first packets of a stream.
        var iv = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(2), ssrc);
        BinaryPrimitives.WriteUInt16BigEndian(iv.AsSpan(10), seq);
        for (var i = 0; i < 12; i++)
        {
            iv[i] ^= sessionSalt[i];
        }

        var aad = packet[..headerLength];
        var body = packet[headerLength..^16];
        var tag = packet[^16..];
        var plain = new byte[body.Length];
        using var gcm = new AesGcm(sessionKey, 16);
        try
        {
            gcm.Decrypt(iv, body, tag, plain, aad);
            return $"opened seq={seq} ssrc={ssrc:X8} payload={plain.Length}B";
        }
        catch (AuthenticationTagMismatchException)
        {
            return "tag_mismatch";
        }
    }

    private static byte[] Derive(byte[] masterKey, byte[] masterSalt, byte label, int length)
    {
        // 112-bit x: master salt (a 96-bit GCM salt is right-padded with zeros, as libsrtp does), label XORed at
        // byte 7 (key_id = label || r with r = 0); counter block = x || 0x0000.
        var block = new byte[16];
        masterSalt.CopyTo(block, 0);
        block[7] ^= label;
        using var aes = Aes.Create();
        aes.Key = masterKey;
        var output = new byte[(length + 15) / 16 * 16];
        for (var i = 0; i * 16 < output.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(14), (ushort)i);
            aes.EncryptEcb(block, output.AsSpan(i * 16, 16), PaddingMode.None);
        }

        return output[..length];
    }
}
