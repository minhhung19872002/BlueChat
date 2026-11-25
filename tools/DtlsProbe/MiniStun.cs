using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DtlsProbe;

/// <summary>
/// Just enough STUN (RFC 8489) for an ICE-lite answerer in the Z3 prototype: check a Binding request's
/// USERNAME/MESSAGE-INTEGRITY and build the success response. The real library is BlueChat.Media.Stun (Phase 5).
/// </summary>
internal static class MiniStun
{
    private const uint MagicCookie = 0x2112A442;
    private const ushort AttrUsername = 0x0006;
    private const ushort AttrMessageIntegrity = 0x0008;
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrFingerprint = 0x8028;

    public static bool IsBindingRequest(ReadOnlySpan<byte> m) =>
        m.Length >= 20 && BinaryPrimitives.ReadUInt16BigEndian(m) == 0x0001
                       && BinaryPrimitives.ReadUInt32BigEndian(m[4..]) == MagicCookie;

    /// <summary>Validates USERNAME prefix and MESSAGE-INTEGRITY; returns the success response or null.</summary>
    public static byte[]? Answer(ReadOnlySpan<byte> request, string localUfrag, string localPwd, IPEndPoint from)
    {
        var declared = BinaryPrimitives.ReadUInt16BigEndian(request[2..]);
        if (20 + declared != request.Length)
        {
            return null;
        }

        string? username = null;
        var integrityOffset = -1;
        var offset = 20;
        while (offset + 4 <= request.Length)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(request[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(request[(offset + 2)..]);
            if (offset + 4 + length > request.Length)
            {
                return null;
            }

            if (type == AttrUsername)
            {
                username = Encoding.UTF8.GetString(request.Slice(offset + 4, length));
            }
            else if (type == AttrMessageIntegrity && length == 20)
            {
                integrityOffset = offset;
            }

            offset += 4 + ((length + 3) & ~3);
        }

        if (username is null || !username.StartsWith(localUfrag + ":", StringComparison.Ordinal) || integrityOffset < 0)
        {
            return null;
        }

        // RFC 8489 §14.5: HMAC over the message up to MESSAGE-INTEGRITY, length field covering it.
        var key = Encoding.UTF8.GetBytes(localPwd);
        var covered = request[..integrityOffset].ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(covered.AsSpan(2), (ushort)(integrityOffset - 20 + 24));
        var expected = HMACSHA1.HashData(key, covered);
        if (!CryptographicOperations.FixedTimeEquals(expected, request.Slice(integrityOffset + 4, 20)))
        {
            return null;
        }

        return BuildSuccess(request.Slice(8, 12), from, key);
    }

    private static byte[] BuildSuccess(ReadOnlySpan<byte> transactionId, IPEndPoint from, byte[] key)
    {
        var addr = from.Address.MapToIPv4().GetAddressBytes();
        var body = new List<byte>();
        // XOR-MAPPED-ADDRESS (IPv4)
        var xma = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(xma, AttrXorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(xma.AsSpan(2), 8);
        xma[5] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(xma.AsSpan(6), (ushort)(from.Port ^ (MagicCookie >> 16)));
        var cookie = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(cookie, MagicCookie);
        for (var i = 0; i < 4; i++)
        {
            xma[8 + i] = (byte)(addr[i] ^ cookie[i]);
        }

        body.AddRange(xma);

        var message = new byte[20 + body.Count + 24 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(message, 0x0101);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8));
        body.CopyTo(message, 20);

        var miOffset = 20 + body.Count;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(body.Count + 24));
        var mac = HMACSHA1.HashData(key, message.AsSpan(0, miOffset));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(miOffset), AttrMessageIntegrity);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(miOffset + 2), 20);
        mac.CopyTo(message, miOffset + 4);

        var fpOffset = miOffset + 24;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(body.Count + 24 + 8));
        var crc = Crc32(message.AsSpan(0, fpOffset)) ^ 0x5354554E;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(fpOffset), AttrFingerprint);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(fpOffset + 2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(fpOffset + 4), crc);
        return message;
    }

    /// <summary>CRC-32 (ISO 3309, reflected 0xEDB88320) as FINGERPRINT requires.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
