using System.Buffers.Binary;

namespace BlueChat.Media.Rtp.Rtcp;

/// <summary>RTCP packet types (RFC 3550 §12.1, RFC 4585 §6.1).</summary>
public static class RtcpPacketType
{
    public const byte SenderReport = 200;
    public const byte ReceiverReport = 201;
    public const byte SourceDescription = 202;
    public const byte Goodbye = 203;
    public const byte Application = 204;

    /// <summary>Transport-layer feedback (RFC 4585 §6.2): FMT 1 generic NACK, FMT 15 transport-cc.</summary>
    public const byte TransportFeedback = 205;

    /// <summary>Payload-specific feedback (RFC 4585 §6.3): FMT 1 PLI, FMT 4 FIR (RFC 5104), FMT 15 application (REMB).</summary>
    public const byte PayloadFeedback = 206;
}

/// <summary>Common header of one RTCP packet inside a compound (RFC 3550 §6.4.1), as offsets.</summary>
/// <param name="Count">RC / SC / FMT field (5 bits).</param>
/// <param name="Offset">Offset of this packet in the compound.</param>
/// <param name="Length">Length of this packet in bytes, header and padding included.</param>
public readonly record struct RtcpPacketHeader(bool Padding, byte Count, byte PacketType, int Offset, int Length)
{
    public const int Size = 4;

    /// <summary>This packet's bytes, padding included.</summary>
    public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> compound) => compound.Slice(Offset, Length);
}

/// <summary>
/// Iterates and validates a compound (or RFC 5506 reduced-size) RTCP packet:
/// <c>foreach (var p in new RtcpCompoundReader(data)) …</c>. Throws <see cref="RtpParseException"/> on any violation.
/// </summary>
public ref struct RtcpCompoundReader
{
    /// <summary>Upper bound on packets in one datagram (a 1500-byte datagram holds at most 375 empty packets).</summary>
    public const int MaxPackets = 400;

    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private int _count;

    public RtcpCompoundReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Current = default;
    }

    public RtcpPacketHeader Current { get; private set; }

    public readonly RtcpCompoundReader GetEnumerator() => this;

    /// <summary>RFC 3550 §6.4.1 header checks and §A.2 length/padding rules for every packet.</summary>
    public bool MoveNext()
    {
        if (_pos >= _data.Length)
        {
            return false;
        }

        if (_data.Length - _pos < RtcpPacketHeader.Size)
        {
            throw RtpParseException.Truncated("rtcp header");
        }

        if (++_count > MaxPackets)
        {
            throw RtpParseException.Invalid("rtcp_too_many", "too many RTCP packets in one compound");
        }

        var b0 = _data[_pos];
        if (b0 >> 6 != 2)
        {
            throw RtpParseException.Invalid("rtcp_version", "RTCP version is not 2");
        }

        var length = 4 * (BinaryPrimitives.ReadUInt16BigEndian(_data[(_pos + 2)..]) + 1);
        if (_pos + length > _data.Length)
        {
            throw RtpParseException.Invalid("rtcp_length", "RTCP length field runs past the datagram");
        }

        var padding = (b0 & 0x20) != 0;
        if (padding)
        {
            // §A.2: only the last packet of a compound may carry padding.
            var padCount = _data[_pos + length - 1];
            if (_pos + length != _data.Length || padCount == 0 || padCount > length - RtcpPacketHeader.Size)
            {
                throw RtpParseException.Invalid("rtcp_padding", "RTCP padding not in the last packet or bad pad count");
            }
        }

        Current = new RtcpPacketHeader(padding, (byte)(b0 & 0x1F), _data[_pos + 1], _pos, length);
        _pos += length;
        return true;
    }

    /// <summary>
    /// Whole-datagram validation. Full compound rules (RFC 3550 §6.1, §A.2): first packet SR or RR. With
    /// <paramref name="allowReducedSize"/> (RFC 5506 §3.4) any packet type may come first. Returns the packet count.
    /// </summary>
    public static int Validate(ReadOnlySpan<byte> data, bool allowReducedSize)
    {
        if (data.IsEmpty)
        {
            throw RtpParseException.Truncated("rtcp");
        }

        var n = 0;
        foreach (var p in new RtcpCompoundReader(data))
        {
            if (n == 0 && !allowReducedSize && p.PacketType is not (RtcpPacketType.SenderReport or RtcpPacketType.ReceiverReport))
            {
                throw RtpParseException.Invalid("rtcp_compound_first", "a compound RTCP packet must start with SR or RR");
            }

            if (p.PacketType is < 192 or > 223)
            {
                throw RtpParseException.Invalid("rtcp_type", $"packet type {p.PacketType} is not RTCP");
            }

            n++;
        }

        return n;
    }
}

/// <summary>Shared helpers for writers.</summary>
internal static class RtcpWrite
{
    /// <summary>Writes the 4-byte common header (RFC 3550 §6.4.1) for a packet of <paramref name="length"/> bytes.</summary>
    public static void Header(Span<byte> dest, byte count, byte packetType, int length)
    {
        if ((length & 3) != 0 || length < 4 || length > 4 * 65536)
        {
            throw new ArgumentException("RTCP length must be a multiple of 4");
        }

        dest[0] = (byte)(0x80 | (count & 0x1F));
        dest[1] = packetType;
        BinaryPrimitives.WriteUInt16BigEndian(dest[2..], (ushort)(length / 4 - 1));
    }

    public static void Need(Span<byte> dest, int length)
    {
        if (dest.Length < length)
        {
            throw new ArgumentException("destination too small");
        }
    }
}
