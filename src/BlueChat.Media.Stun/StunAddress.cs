using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BlueChat.Media.Stun;

/// <summary>Address attribute encoding (RFC 8489 §14.1 MAPPED-ADDRESS, §14.2 XOR-MAPPED-ADDRESS).</summary>
public static class StunAddress
{
    private const byte FamilyIPv4 = 0x01;
    private const byte FamilyIPv6 = 0x02;

    public static int EncodedLength(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? 20 : 8;

    /// <summary>RFC 8489 §14.1.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> value, out IPEndPoint? endPoint)
    {
        endPoint = null;
        if (value.Length < 4)
        {
            return false;
        }

        var family = value[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        if ((family == FamilyIPv4 && value.Length == 8) || (family == FamilyIPv6 && value.Length == 20))
        {
            endPoint = new IPEndPoint(new IPAddress(value[4..]), port);
            return true;
        }

        return false;
    }

    /// <summary>RFC 8489 §14.2: port XOR the cookie's top 16 bits; IPv4 XOR cookie; IPv6 XOR cookie ‖ transaction id.</summary>
    public static bool TryDecodeXor(ReadOnlySpan<byte> value, ReadOnlySpan<byte> transactionId, out IPEndPoint? endPoint)
    {
        endPoint = null;
        if (value.Length < 4)
        {
            return false;
        }

        var family = value[1];
        var port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (StunMessage.MagicCookie >> 16));
        Span<byte> mask = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask, StunMessage.MagicCookie);
        transactionId.CopyTo(mask[4..]);
        int addressLength;
        if (family == FamilyIPv4 && value.Length == 8)
        {
            addressLength = 4;
        }
        else if (family == FamilyIPv6 && value.Length == 20)
        {
            addressLength = 16;
        }
        else
        {
            return false;
        }

        Span<byte> address = stackalloc byte[addressLength];
        for (var i = 0; i < addressLength; i++)
        {
            address[i] = (byte)(value[4 + i] ^ mask[i]);
        }

        endPoint = new IPEndPoint(new IPAddress(address), port);
        return true;
    }

    /// <summary>Writes the value (no attribute header) of an address attribute; returns its length.</summary>
    public static int Encode(Span<byte> destination, IPAddress address, int port, ReadOnlySpan<byte> transactionId, bool xor)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var length = v6 ? 20 : 8;
        destination[0] = 0;
        destination[1] = v6 ? FamilyIPv6 : FamilyIPv4;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)(xor ? port ^ (StunMessage.MagicCookie >> 16) : port));
        if (!address.TryWriteBytes(destination[4..length], out _))
        {
            throw new ArgumentException("address does not fit", nameof(address));
        }

        if (xor)
        {
            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask, StunMessage.MagicCookie);
            transactionId.CopyTo(mask[4..]);
            for (var i = 0; i < length - 4; i++)
            {
                destination[4 + i] ^= mask[i];
            }
        }

        return length;
    }
}
