using System.Buffers.Binary;

namespace BlueChat.Media.Rtp.Rtcp;

/// <summary>Common part of a feedback message (RFC 4585 §6.1): FMT, sender SSRC, media SSRC, FCI.</summary>
public readonly record struct FeedbackHeader(byte PacketType, byte Format, uint SenderSsrc, uint MediaSsrc, int FciOffset, int FciLength)
{
    public const byte FormatNack = 1;
    public const byte FormatTransportCc = 15;
    public const byte FormatPli = 1;
    public const byte FormatFir = 4;
    public const byte FormatApplication = 15;

    public bool IsNack => PacketType == RtcpPacketType.TransportFeedback && Format == FormatNack;
    public bool IsTransportCc => PacketType == RtcpPacketType.TransportFeedback && Format == FormatTransportCc;
    public bool IsPli => PacketType == RtcpPacketType.PayloadFeedback && Format == FormatPli;
    public bool IsFir => PacketType == RtcpPacketType.PayloadFeedback && Format == FormatFir;
    public bool IsApplication => PacketType == RtcpPacketType.PayloadFeedback && Format == FormatApplication;

    public ReadOnlySpan<byte> Fci(ReadOnlySpan<byte> packet) => packet.Slice(FciOffset, FciLength);

    /// <summary>RFC 4585 §6.1 layout; <paramref name="packet"/> is one packet from <see cref="RtcpCompoundReader"/>.</summary>
    public static FeedbackHeader Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || packet[1] is not (RtcpPacketType.TransportFeedback or RtcpPacketType.PayloadFeedback))
        {
            throw RtpParseException.Truncated("rtcp feedback");
        }

        var end = (packet[0] & 0x20) != 0 ? packet.Length - packet[^1] : packet.Length;
        return new FeedbackHeader(packet[1], (byte)(packet[0] & 0x1F), BinaryPrimitives.ReadUInt32BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[8..]), 12, Math.Max(0, end - 12));
    }

    internal static void Write(Span<byte> dest, byte packetType, byte format, uint sender, uint media, int length)
    {
        RtcpWrite.Need(dest, length);
        RtcpWrite.Header(dest, format, packetType, length);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], sender);
        BinaryPrimitives.WriteUInt32BigEndian(dest[8..], media);
    }
}

/// <summary>Generic NACK (RFC 4585 §6.2.1): FCI pairs of PID (16) and BLP (16 following packets).</summary>
public static class Nack
{
    /// <summary>Upper bound on lost sequence numbers per packet a reader will expand (protects callers).</summary>
    public const int MaxPairs = 256;

    /// <summary>Expands the FCI into <paramref name="lost"/>; returns the count. Throws if the buffer is too small.</summary>
    public static int ReadLost(ReadOnlySpan<byte> fci, Span<ushort> lost)
    {
        if ((fci.Length & 3) != 0 || fci.Length == 0)
        {
            throw RtpParseException.Invalid("rtcp_nack", "NACK FCI is not a whole number of PID/BLP pairs");
        }

        if (fci.Length / 4 > MaxPairs)
        {
            throw RtpParseException.Invalid("rtcp_nack", "too many NACK pairs");
        }

        var n = 0;
        for (var i = 0; i < fci.Length; i += 4)
        {
            var pid = BinaryPrimitives.ReadUInt16BigEndian(fci[i..]);
            var blp = BinaryPrimitives.ReadUInt16BigEndian(fci[(i + 2)..]);
            Add(lost, ref n, pid);
            for (var bit = 0; bit < 16; bit++)
            {
                if ((blp & (1 << bit)) != 0)
                {
                    Add(lost, ref n, (ushort)(pid + bit + 1));
                }
            }
        }

        return n;
    }

    private static void Add(Span<ushort> lost, ref int n, ushort seq)
    {
        if (n >= lost.Length)
        {
            throw RtpParseException.Invalid("rtcp_nack", "more lost packets than the caller's buffer");
        }

        lost[n++] = seq;
    }

    /// <summary>
    /// Builds a NACK for <paramref name="lost"/> (in send order, wraparound allowed): each PID covers itself and the 16
    /// following sequence numbers. Returns the packet length.
    /// </summary>
    public static int Write(Span<byte> dest, uint senderSsrc, uint mediaSsrc, ReadOnlySpan<ushort> lost)
    {
        if (lost.IsEmpty)
        {
            throw new ArgumentException("nothing to NACK", nameof(lost));
        }

        // Count pairs first to size the packet.
        var pairs = 0;
        for (var i = 0; i < lost.Length;)
        {
            var pid = lost[i++];
            while (i < lost.Length && (ushort)(lost[i] - pid) is >= 1 and <= 16)
            {
                i++;
            }

            pairs++;
        }

        var length = 12 + 4 * pairs;
        FeedbackHeader.Write(dest, RtcpPacketType.TransportFeedback, FeedbackHeader.FormatNack, senderSsrc, mediaSsrc, length);
        var at = 12;
        for (var i = 0; i < lost.Length;)
        {
            var pid = lost[i++];
            ushort blp = 0;
            while (i < lost.Length && (ushort)(lost[i] - pid) is >= 1 and <= 16)
            {
                blp |= (ushort)(1 << ((ushort)(lost[i] - pid) - 1));
                i++;
            }

            BinaryPrimitives.WriteUInt16BigEndian(dest[at..], pid);
            BinaryPrimitives.WriteUInt16BigEndian(dest[(at + 2)..], blp);
            at += 4;
        }

        return length;
    }
}

/// <summary>Picture Loss Indication (RFC 4585 §6.3.1): no FCI.</summary>
public static class Pli
{
    public static int Write(Span<byte> dest, uint senderSsrc, uint mediaSsrc)
    {
        FeedbackHeader.Write(dest, RtcpPacketType.PayloadFeedback, FeedbackHeader.FormatPli, senderSsrc, mediaSsrc, 12);
        return 12;
    }
}

/// <summary>Full Intra Request entry (RFC 5104 §4.3.1.1).</summary>
public readonly record struct FirEntry(uint Ssrc, byte SequenceNumber);

/// <summary>FIR (RFC 5104 §4.3.1): media SSRC field is 0, FCI entries of SSRC + command sequence number.</summary>
public static class Fir
{
    public static int Count(ReadOnlySpan<byte> fci) =>
        (fci.Length & 7) == 0 && fci.Length > 0
            ? fci.Length / 8
            : throw RtpParseException.Invalid("rtcp_fir", "FIR FCI is not a whole number of 8-byte entries");

    public static FirEntry Get(ReadOnlySpan<byte> fci, int index) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(fci[(8 * index)..]), fci[8 * index + 4]);

    public static int Write(Span<byte> dest, uint senderSsrc, ReadOnlySpan<FirEntry> entries)
    {
        if (entries.IsEmpty)
        {
            throw new ArgumentException("no FIR entry", nameof(entries));
        }

        var length = 12 + 8 * entries.Length;
        FeedbackHeader.Write(dest, RtcpPacketType.PayloadFeedback, FeedbackHeader.FormatFir, senderSsrc, 0, length);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = dest[(12 + 8 * i)..];
            BinaryPrimitives.WriteUInt32BigEndian(e, entries[i].Ssrc);
            e[4] = entries[i].SequenceNumber;
            e[5] = e[6] = e[7] = 0;
        }

        return length;
    }
}

/// <summary>
/// Receiver Estimated Maximum Bitrate (draft-alvestrand-rmcat-remb-03 §2.2): application-layer feedback with
/// "REMB", SSRC count, 6-bit exponent, 18-bit mantissa, SSRC list.
/// </summary>
public static class Remb
{
    private const uint Magic = 0x52454D42; // "REMB"

    public static bool IsRemb(ReadOnlySpan<byte> fci) => fci.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(fci) == Magic;

    /// <summary>Bitrate in bits/s and the SSRC count; SSRCs via <see cref="GetSsrc"/>.</summary>
    public static (ulong Bitrate, int SsrcCount) Read(ReadOnlySpan<byte> fci)
    {
        if (!IsRemb(fci))
        {
            throw RtpParseException.Invalid("rtcp_remb", "not a REMB message");
        }

        int count = fci[4];
        if (fci.Length < 8 + 4 * count)
        {
            throw RtpParseException.Invalid("rtcp_remb", "REMB SSRC count exceeds the packet");
        }

        var exp = fci[5] >> 2;
        var mantissa = (ulong)((fci[5] & 0x03) << 16 | fci[6] << 8 | fci[7]);
        var bitrate = exp > 46 ? ulong.MaxValue : mantissa << exp;
        return (bitrate, count);
    }

    public static uint GetSsrc(ReadOnlySpan<byte> fci, int index) => BinaryPrimitives.ReadUInt32BigEndian(fci[(8 + 4 * index)..]);

    public static int Write(Span<byte> dest, uint senderSsrc, ulong bitrate, ReadOnlySpan<uint> ssrcs)
    {
        if (ssrcs.Length > 255)
        {
            throw new ArgumentException("at most 255 SSRCs", nameof(ssrcs));
        }

        var exp = 0;
        var mantissa = bitrate;
        while (mantissa > 0x3FFFF)
        {
            mantissa >>= 1;
            exp++;
        }

        var length = 12 + 8 + 4 * ssrcs.Length;
        FeedbackHeader.Write(dest, RtcpPacketType.PayloadFeedback, FeedbackHeader.FormatApplication, senderSsrc, 0, length);
        BinaryPrimitives.WriteUInt32BigEndian(dest[12..], Magic);
        dest[16] = (byte)ssrcs.Length;
        dest[17] = (byte)((exp << 2) | (int)(mantissa >> 16));
        dest[18] = (byte)(mantissa >> 8);
        dest[19] = (byte)mantissa;
        for (var i = 0; i < ssrcs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest[(20 + 4 * i)..], ssrcs[i]);
        }

        return length;
    }
}
