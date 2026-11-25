using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlueChat.Media.Stun;

public enum IntegrityResult
{
    Ok,
    Missing,
    Mismatch,
    Malformed,
}

/// <summary>MESSAGE-INTEGRITY (RFC 8489 §14.5), MESSAGE-INTEGRITY-SHA256 (§14.6), FINGERPRINT (§14.7).</summary>
public static class StunIntegrity
{
    private const uint FingerprintXor = 0x5354554E;

    /// <summary>
    /// RFC 8489 §14.5: HMAC-SHA1 over the message up to (excluding) MESSAGE-INTEGRITY, with the header length set as if
    /// MESSAGE-INTEGRITY were the last attribute.
    /// </summary>
    public static IntegrityResult VerifySha1(in StunMessage message, ReadOnlySpan<byte> key) =>
        Verify(message, key, StunAttr.MessageIntegrity, 20);

    /// <summary>RFC 8489 §14.6 (full 32-byte HMAC-SHA256; truncated forms rejected as Malformed).</summary>
    public static IntegrityResult VerifySha256(in StunMessage message, ReadOnlySpan<byte> key) =>
        Verify(message, key, StunAttr.MessageIntegritySha256, 32);

    private static IntegrityResult Verify(in StunMessage message, ReadOnlySpan<byte> key, ushort attribute, int macLength)
    {
        var offset = message.OffsetOf(attribute);
        if (offset < 0)
        {
            return IntegrityResult.Missing;
        }

        var bytes = message.Bytes;
        if (BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]) != macLength)
        {
            return IntegrityResult.Malformed;
        }

        Span<byte> mac = stackalloc byte[32];
        ComputeMac(bytes[..offset], key, attribute, macLength, mac);
        return CryptographicOperations.FixedTimeEquals(mac[..macLength], bytes.Slice(offset + 4, macLength))
            ? IntegrityResult.Ok
            : IntegrityResult.Mismatch;
    }

    /// <summary>Computes the MAC over <paramref name="prefix"/> (header + attributes before the integrity attribute).</summary>
    internal static void ComputeMac(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> key, ushort attribute, int macLength, Span<byte> mac)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(prefix.Length);
        try
        {
            var data = buffer.AsSpan(0, prefix.Length);
            prefix.CopyTo(data);
            BinaryPrimitives.WriteUInt16BigEndian(data[2..], (ushort)(prefix.Length - StunMessage.HeaderLength + 4 + macLength));
            if (attribute == StunAttr.MessageIntegrity)
            {
                HMACSHA1.HashData(key, data, mac);
            }
            else
            {
                HMACSHA256.HashData(key, data, mac);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>RFC 8489 §14.7: CRC-32 of the message up to FINGERPRINT, XOR 0x5354554E; must be the last attribute.</summary>
    public static IntegrityResult VerifyFingerprint(in StunMessage message)
    {
        var offset = message.OffsetOf(StunAttr.Fingerprint);
        if (offset < 0)
        {
            return IntegrityResult.Missing;
        }

        var bytes = message.Bytes;
        if (offset + 8 != bytes.Length || BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]) != 4)
        {
            return IntegrityResult.Malformed;
        }

        // The length field already covers FINGERPRINT because it is last.
        var expected = Crc32.Compute(bytes[..offset]) ^ FingerprintXor;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]) == expected ? IntegrityResult.Ok : IntegrityResult.Mismatch;
    }

    internal static uint FingerprintOf(ReadOnlySpan<byte> prefix) => Crc32.Compute(prefix) ^ FingerprintXor;
}

/// <summary>Keys for MESSAGE-INTEGRITY (RFC 8489 §9.1.1 short-term, §9.2.2 long-term) and USERHASH (§14.4).</summary>
public static class StunCredentials
{
    /// <summary>§9.1.1: key = OpaqueString(password). ICE passwords are ASCII (RFC 8445 §5.3), so this is their UTF-8.</summary>
    public static byte[] ShortTermKey(string password) => Encoding.UTF8.GetBytes(PreparePassword(password));

    /// <summary>§9.2.2: MD5(username ":" OpaqueString(realm) ":" OpaqueString(password)) for algorithm MD5.</summary>
    public static byte[] LongTermKeyMd5(string username, string realm, string password) =>
        MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{PreparePassword(realm)}:{PreparePassword(password)}"));

    /// <summary>§9.2.2: the same input hashed with SHA-256 for algorithm SHA-256.</summary>
    public static byte[] LongTermKeySha256(string username, string realm, string password) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{username}:{PreparePassword(realm)}:{PreparePassword(password)}"));

    /// <summary>§14.4 USERHASH = SHA-256(OpaqueString(username) ":" OpaqueString(realm)).</summary>
    public static byte[] UserHash(string username, string realm) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{PreparePassword(username)}:{PreparePassword(realm)}"));

    /// <summary>
    /// Password preparation as the published vectors expect (RFC 5769 §2.4 and RFC 8489 App. B.1 give
    /// "The&lt;U+00AD&gt;M&lt;U+00AA&gt;tr&lt;U+2168&gt;" → "TheMatrIX"): SASLprep (RFC 4013) mapping — characters
    /// "commonly mapped to nothing" (RFC 3454 B.1) removed, non-ASCII spaces (C.1.2) mapped to U+0020 — then NFKC.
    /// Control characters (C.2) are rejected.
    /// </summary>
    public static string PreparePassword(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (IsMappedToNothing(ch))
            {
                continue;
            }

            sb.Append(ch != ' ' && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator ? ' ' : ch);
        }

        var result = sb.ToString().Normalize(NormalizationForm.FormKC);
        foreach (var ch in result)
        {
            if (char.IsControl(ch))
            {
                throw new ArgumentException("control character in credential", nameof(input));
            }
        }

        return result;
    }

    // RFC 3454 table B.1
    private static bool IsMappedToNothing(char c) =>
        c is '­' or '͏' or '᠆' or '᠋' or '᠌' or '᠍' or '​' or '‌' or '‍'
            or '⁠' or '﻿' || (c >= '︀' && c <= '️');
}

/// <summary>CRC-32 (ISO 3309 / ITU-T V.42, reflected polynomial 0xEDB88320) as required by RFC 8489 §14.7.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
