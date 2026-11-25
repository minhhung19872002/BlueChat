using System.Buffers.Binary;
using System.Text;

namespace BlueChat.Media.Rtp.Rtcp;

/// <summary>Report block (RFC 3550 §6.4.1). <see cref="CumulativeLost"/> is the signed 24-bit value.</summary>
public readonly record struct ReportBlock(
    uint Ssrc, byte FractionLost, int CumulativeLost, uint ExtendedHighestSequence, uint Jitter, uint LastSenderReport,
    uint DelaySinceLastSenderReport)
{
    public const int Size = 24;

    /// <summary>RFC 3550 §6.4.1 report block layout.</summary>
    public static ReportBlock Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
        {
            throw RtpParseException.Truncated("rtcp report block");
        }

        var lost = b[5] << 16 | b[6] << 8 | b[7];
        if ((lost & 0x800000) != 0)
        {
            lost |= unchecked((int)0xFF000000); // sign-extend 24 bits
        }

        return new ReportBlock(BinaryPrimitives.ReadUInt32BigEndian(b), b[4], lost,
            BinaryPrimitives.ReadUInt32BigEndian(b[8..]), BinaryPrimitives.ReadUInt32BigEndian(b[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(b[16..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..]));
    }

    public void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteUInt32BigEndian(b, Ssrc);
        b[4] = FractionLost;
        var lost = Math.Clamp(CumulativeLost, -0x800000, 0x7FFFFF);
        b[5] = (byte)(lost >> 16);
        b[6] = (byte)(lost >> 8);
        b[7] = (byte)lost;
        BinaryPrimitives.WriteUInt32BigEndian(b[8..], ExtendedHighestSequence);
        BinaryPrimitives.WriteUInt32BigEndian(b[12..], Jitter);
        BinaryPrimitives.WriteUInt32BigEndian(b[16..], LastSenderReport);
        BinaryPrimitives.WriteUInt32BigEndian(b[20..], DelaySinceLastSenderReport);
    }
}

/// <summary>Sender report (RFC 3550 §6.4.1). Report blocks: <see cref="GetReportBlock"/>.</summary>
public readonly record struct SenderReport(uint Ssrc, ulong NtpTimestamp, uint RtpTimestamp, uint PacketCount, uint OctetCount, int ReportCount)
{
    private const int BodyStart = 28;

    /// <summary>LSR as used in report blocks: the middle 32 bits of the NTP timestamp (RFC 3550 §6.4.1).</summary>
    public uint CompactNtp => (uint)(NtpTimestamp >> 16);

    /// <summary><paramref name="packet"/> is one SR from <see cref="RtcpCompoundReader"/>.</summary>
    public static SenderReport Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < BodyStart || packet[1] != RtcpPacketType.SenderReport)
        {
            throw RtpParseException.Truncated("rtcp sender report");
        }

        var rc = packet[0] & 0x1F;
        if (packet.Length < BodyStart + rc * ReportBlock.Size)
        {
            throw RtpParseException.Invalid("rtcp_report_count", "SR report count exceeds the packet");
        }

        return new SenderReport(BinaryPrimitives.ReadUInt32BigEndian(packet[4..]), BinaryPrimitives.ReadUInt64BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[16..]), BinaryPrimitives.ReadUInt32BigEndian(packet[20..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[24..]), rc);
    }

    public static ReportBlock GetReportBlock(ReadOnlySpan<byte> packet, int index) =>
        ReportBlock.Read(packet[(BodyStart + index * ReportBlock.Size)..]);

    /// <summary>Writes an SR with up to 31 report blocks; returns its length.</summary>
    public int Write(Span<byte> dest, ReadOnlySpan<ReportBlock> blocks)
    {
        if (blocks.Length > 31)
        {
            throw new ArgumentException("at most 31 report blocks", nameof(blocks));
        }

        var length = BodyStart + blocks.Length * ReportBlock.Size;
        RtcpWrite.Need(dest, length);
        RtcpWrite.Header(dest, (byte)blocks.Length, RtcpPacketType.SenderReport, length);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], Ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(dest[8..], NtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(dest[16..], RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(dest[20..], PacketCount);
        BinaryPrimitives.WriteUInt32BigEndian(dest[24..], OctetCount);
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i].Write(dest[(BodyStart + i * ReportBlock.Size)..]);
        }

        return length;
    }
}

/// <summary>Receiver report (RFC 3550 §6.4.2).</summary>
public readonly record struct ReceiverReport(uint Ssrc, int ReportCount)
{
    private const int BodyStart = 8;

    public static ReceiverReport Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < BodyStart || packet[1] != RtcpPacketType.ReceiverReport)
        {
            throw RtpParseException.Truncated("rtcp receiver report");
        }

        var rc = packet[0] & 0x1F;
        if (packet.Length < BodyStart + rc * ReportBlock.Size)
        {
            throw RtpParseException.Invalid("rtcp_report_count", "RR report count exceeds the packet");
        }

        return new ReceiverReport(BinaryPrimitives.ReadUInt32BigEndian(packet[4..]), rc);
    }

    public static ReportBlock GetReportBlock(ReadOnlySpan<byte> packet, int index) =>
        ReportBlock.Read(packet[(BodyStart + index * ReportBlock.Size)..]);

    public static int Write(Span<byte> dest, uint ssrc, ReadOnlySpan<ReportBlock> blocks)
    {
        if (blocks.Length > 31)
        {
            throw new ArgumentException("at most 31 report blocks", nameof(blocks));
        }

        var length = BodyStart + blocks.Length * ReportBlock.Size;
        RtcpWrite.Need(dest, length);
        RtcpWrite.Header(dest, (byte)blocks.Length, RtcpPacketType.ReceiverReport, length);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], ssrc);
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i].Write(dest[(BodyStart + i * ReportBlock.Size)..]);
        }

        return length;
    }
}

/// <summary>One SDES item (RFC 3550 §6.5) as offsets into the packet. Type 1 = CNAME.</summary>
public readonly record struct SdesItem(uint Ssrc, byte Type, int Offset, int Length);

/// <summary>Iterates every item of every chunk of one SDES packet (RFC 3550 §6.5).</summary>
public ref struct SdesReader
{
    public const byte Cname = 1;

    private readonly ReadOnlySpan<byte> _packet;
    private readonly int _end;
    private int _chunksLeft;
    private int _pos;
    private uint _ssrc;
    private bool _inChunk;

    public SdesReader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4 || packet[1] != RtcpPacketType.SourceDescription)
        {
            throw RtpParseException.Truncated("rtcp sdes");
        }

        _packet = packet;
        _end = (packet[0] & 0x20) != 0 ? packet.Length - packet[^1] : packet.Length;
        _chunksLeft = packet[0] & 0x1F;
        _pos = 4;
        Current = default;
    }

    public SdesItem Current { get; private set; }

    public readonly SdesReader GetEnumerator() => this;

    public bool MoveNext()
    {
        while (true)
        {
            if (!_inChunk)
            {
                if (_chunksLeft == 0)
                {
                    return false;
                }

                if (_pos + 4 > _end)
                {
                    throw RtpParseException.Invalid("rtcp_sdes", "SDES chunk count exceeds the packet");
                }

                _ssrc = BinaryPrimitives.ReadUInt32BigEndian(_packet[_pos..]);
                _pos += 4;
                _chunksLeft--;
                _inChunk = true;
            }

            if (_pos >= _end)
            {
                throw RtpParseException.Invalid("rtcp_sdes", "SDES chunk without terminating null item");
            }

            var type = _packet[_pos];
            if (type == 0)
            {
                // End of item list; skip to the next 32-bit boundary (§6.5).
                _pos = (_pos + 4) & ~3;
                _inChunk = false;
                continue;
            }

            if (_pos + 2 > _end || _pos + 2 + _packet[_pos + 1] > _end)
            {
                throw RtpParseException.Invalid("rtcp_sdes", "SDES item runs past the packet");
            }

            var len = _packet[_pos + 1];
            Current = new SdesItem(_ssrc, type, _pos + 2, len);
            _pos += 2 + len;
            return true;
        }
    }

    /// <summary>Writes an SDES packet with one chunk carrying one CNAME (RFC 3550 §6.5.1); returns its length.</summary>
    public static int WriteCname(Span<byte> dest, uint ssrc, string cname)
    {
        var text = Encoding.UTF8.GetByteCount(cname);
        if (text is 0 or > 255)
        {
            throw new ArgumentException("CNAME must be 1–255 bytes", nameof(cname));
        }

        var length = (4 + 4 + 2 + text + 1 + 3) & ~3; // header, SSRC, item, null terminator, pad to 32 bits
        RtcpWrite.Need(dest, length);
        dest[..length].Clear();
        RtcpWrite.Header(dest, 1, RtcpPacketType.SourceDescription, length);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], ssrc);
        dest[8] = Cname;
        dest[9] = (byte)text;
        Encoding.UTF8.GetBytes(cname, dest[10..]);
        return length;
    }
}

/// <summary>BYE (RFC 3550 §6.6).</summary>
public static class Goodbye
{
    public static int SourceCount(ReadOnlySpan<byte> packet)
    {
        var sc = packet[0] & 0x1F;
        if (packet[1] != RtcpPacketType.Goodbye || packet.Length < 4 + 4 * sc)
        {
            throw RtpParseException.Invalid("rtcp_bye", "BYE source count exceeds the packet");
        }

        return sc;
    }

    public static uint GetSource(ReadOnlySpan<byte> packet, int index) =>
        BinaryPrimitives.ReadUInt32BigEndian(packet[(4 + 4 * index)..]);

    /// <summary>Optional reason text after the SSRC list; empty when absent.</summary>
    public static ReadOnlySpan<byte> Reason(ReadOnlySpan<byte> packet)
    {
        var start = 4 + 4 * SourceCount(packet);
        var end = (packet[0] & 0x20) != 0 ? packet.Length - packet[^1] : packet.Length;
        if (start >= end)
        {
            return default;
        }

        var len = packet[start];
        if (start + 1 + len > end)
        {
            throw RtpParseException.Invalid("rtcp_bye", "BYE reason runs past the packet");
        }

        return packet.Slice(start + 1, len);
    }

    public static int Write(Span<byte> dest, ReadOnlySpan<uint> sources, string? reason = null)
    {
        if (sources.Length > 31)
        {
            throw new ArgumentException("at most 31 sources", nameof(sources));
        }

        var reasonLength = reason is null ? 0 : Encoding.UTF8.GetByteCount(reason);
        if (reasonLength > 255)
        {
            throw new ArgumentException("reason longer than 255 bytes", nameof(reason));
        }

        var length = (4 + 4 * sources.Length + (reason is null ? 0 : 1 + reasonLength) + 3) & ~3;
        RtcpWrite.Need(dest, length);
        dest[..length].Clear();
        RtcpWrite.Header(dest, (byte)sources.Length, RtcpPacketType.Goodbye, length);
        for (var i = 0; i < sources.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest[(4 + 4 * i)..], sources[i]);
        }

        if (reason is not null)
        {
            var at = 4 + 4 * sources.Length;
            dest[at] = (byte)reasonLength;
            Encoding.UTF8.GetBytes(reason, dest[(at + 1)..]);
        }

        return length;
    }
}

/// <summary>APP packet (RFC 3550 §6.7): passed through, never interpreted.</summary>
public readonly record struct ApplicationPacket(byte Subtype, uint Ssrc, uint Name, int DataOffset, int DataLength)
{
    public static ApplicationPacket Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || packet[1] != RtcpPacketType.Application)
        {
            throw RtpParseException.Truncated("rtcp app");
        }

        var end = (packet[0] & 0x20) != 0 ? packet.Length - packet[^1] : packet.Length;
        return new ApplicationPacket((byte)(packet[0] & 0x1F), BinaryPrimitives.ReadUInt32BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[8..]), 12, Math.Max(0, end - 12));
    }
}
