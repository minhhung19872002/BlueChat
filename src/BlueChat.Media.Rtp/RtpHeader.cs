using System.Buffers.Binary;

namespace BlueChat.Media.Rtp;

/// <summary>
/// Parsed RTP fixed header (RFC 3550 §5.1) as offsets into the caller's buffer: no copy, no allocation.
/// <see cref="PayloadOffset"/>/<see cref="PayloadLength"/> exclude CSRCs, the extension block and padding.
/// </summary>
public readonly record struct RtpHeader
{
    public const int FixedLength = 12;

    /// <summary>RFC 8285 §4.2 one-byte header profile.</summary>
    public const ushort OneByteProfile = 0xBEDE;

    public bool Padding { get; init; }
    public bool Extension { get; init; }
    public int CsrcCount { get; init; }
    public bool Marker { get; init; }
    public byte PayloadType { get; init; }
    public ushort SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public uint Ssrc { get; init; }

    /// <summary>Profile field of the extension block (0xBEDE one-byte, 0x100X two-byte), 0 when none.</summary>
    public ushort ExtensionProfile { get; init; }

    /// <summary>Offset of the extension data (after the 4-byte block header); 0 when none.</summary>
    public int ExtensionOffset { get; init; }

    /// <summary>Length of the extension data in bytes (a multiple of 4).</summary>
    public int ExtensionLength { get; init; }

    public int PayloadOffset { get; init; }
    public int PayloadLength { get; init; }
    public int PaddingLength { get; init; }

    public bool HasOneByteExtensions => Extension && ExtensionProfile == OneByteProfile;

    /// <summary>RFC 8285 §4.3: 0x100 in the top 12 bits, the low 4 bits are "appbits".</summary>
    public bool HasTwoByteExtensions => Extension && (ExtensionProfile & 0xFFF0) == 0x1000;

    /// <summary>First byte 128–191 and payload type outside 64–95 (RFC 5761 §4 with RFC 7983 demultiplexing).</summary>
    public static bool LooksLikeRtp(ReadOnlySpan<byte> packet) =>
        packet.Length >= FixedLength && (packet[0] >> 6) == 2 && (packet[1] & 0x7F) is < 64 or > 95;

    /// <summary>RFC 3550 §5.1 + §5.3.1. Throws <see cref="RtpParseException"/>.</summary>
    public static RtpHeader Parse(ReadOnlySpan<byte> packet)
    {
        if (!TryParse(packet, out var header, out var error))
        {
            throw error!;
        }

        return header;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out RtpHeader header, out RtpParseException? error)
    {
        header = default;
        error = null;
        if (packet.Length < FixedLength)
        {
            error = RtpParseException.Truncated("rtp header");
            return false;
        }

        var b0 = packet[0];
        if (b0 >> 6 != 2)
        {
            error = RtpParseException.Invalid("rtp_version", "RTP version is not 2");
            return false;
        }

        var padding = (b0 & 0x20) != 0;
        var extension = (b0 & 0x10) != 0;
        var cc = b0 & 0x0F;
        var offset = FixedLength + 4 * cc;
        if (packet.Length < offset)
        {
            error = RtpParseException.Truncated("rtp csrc list");
            return false;
        }

        ushort profile = 0;
        int extOffset = 0, extLength = 0;
        if (extension)
        {
            // RFC 3550 §5.3.1: 16-bit profile, 16-bit length in 32-bit words, then the data.
            if (packet.Length < offset + 4)
            {
                error = RtpParseException.Truncated("rtp extension header");
                return false;
            }

            profile = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            extLength = 4 * BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            extOffset = offset + 4;
            offset = extOffset + extLength;
            if (packet.Length < offset)
            {
                error = RtpParseException.Truncated("rtp extension data");
                return false;
            }
        }

        var paddingLength = 0;
        if (padding)
        {
            // RFC 3550 §5.1: last octet counts the padding octets including itself; never zero.
            paddingLength = packet[^1];
            if (paddingLength == 0 || offset + paddingLength > packet.Length)
            {
                error = RtpParseException.Invalid("rtp_padding", "RTP padding length is zero or larger than the payload");
                return false;
            }
        }

        header = new RtpHeader
        {
            Padding = padding,
            Extension = extension,
            CsrcCount = cc,
            Marker = (packet[1] & 0x80) != 0,
            PayloadType = (byte)(packet[1] & 0x7F),
            SequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]),
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]),
            ExtensionProfile = profile,
            ExtensionOffset = extOffset,
            ExtensionLength = extLength,
            PayloadOffset = offset,
            PayloadLength = packet.Length - offset - paddingLength,
            PaddingLength = paddingLength,
        };
        return true;
    }

    public uint GetCsrc(ReadOnlySpan<byte> packet, int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)CsrcCount, nameof(index));
        return BinaryPrimitives.ReadUInt32BigEndian(packet[(FixedLength + 4 * index)..]);
    }

    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> packet) => packet.Slice(PayloadOffset, PayloadLength);

    public ReadOnlySpan<byte> ExtensionData(ReadOnlySpan<byte> packet) => packet.Slice(ExtensionOffset, ExtensionLength);

    // ---- in-place rewriting for forwarding (RFC 3550 §5.1 field positions) ----

    public static void WriteSequenceNumber(Span<byte> packet, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(packet[2..], value);

    public static void WriteTimestamp(Span<byte> packet, uint value) => BinaryPrimitives.WriteUInt32BigEndian(packet[4..], value);

    public static void WriteSsrc(Span<byte> packet, uint value) => BinaryPrimitives.WriteUInt32BigEndian(packet[8..], value);

    public static void WritePayloadType(Span<byte> packet, byte payloadType) => packet[1] = (byte)((packet[1] & 0x80) | (payloadType & 0x7F));

    public static void WriteMarker(Span<byte> packet, bool marker) => packet[1] = (byte)(marker ? packet[1] | 0x80 : packet[1] & 0x7F);
}

/// <summary>Fields of a fixed header to write.</summary>
public readonly record struct RtpHeaderFields(bool Marker, byte PayloadType, ushort SequenceNumber, uint Timestamp, uint Ssrc);
