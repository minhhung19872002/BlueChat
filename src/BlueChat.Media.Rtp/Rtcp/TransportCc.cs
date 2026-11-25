using System.Buffers.Binary;

namespace BlueChat.Media.Rtp.Rtcp;

/// <summary>Packet status symbols (draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1.1).</summary>
public enum TwccSymbol : byte
{
    NotReceived = 0,

    /// <summary>Received, receive delta 0…63.75 ms in one byte.</summary>
    SmallDelta = 1,

    /// <summary>Received, receive delta negative or larger, in two signed bytes.</summary>
    LargeOrNegativeDelta = 2,
}

/// <summary>One transport-wide sequence number and its fate. <see cref="DeltaTicks"/> is in 250 µs units (0 when not received).</summary>
public readonly record struct TwccPacketStatus(ushort Sequence, TwccSymbol Symbol, int DeltaTicks)
{
    public bool Received => Symbol != TwccSymbol.NotReceived;
}

/// <param name="ReferenceTime">24-bit reference time in multiples of 64 ms.</param>
public readonly record struct TwccFeedback(ushort BaseSequence, int StatusCount, uint ReferenceTime, byte FeedbackPacketCount)
{
    public const int TicksPerMillisecond = 4; // 250 µs ticks
    public const int ReferenceTimeMs = 64;
}

/// <summary>
/// Transport-wide congestion control feedback (draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1): RTPFB FMT 15.
/// </summary>
public static class TransportCc
{
    /// <summary>Hard cap on statuses one feedback may describe (a 16-bit count; libwebrtc stays far below).</summary>
    public const int MaxStatuses = 0x8000;

    /// <summary>
    /// §3.1: header fields, packet chunks (run-length and 1-/2-bit status vectors), then receive deltas.
    /// Fills <paramref name="statuses"/> (must hold <see cref="TwccFeedback.StatusCount"/> entries).
    /// </summary>
    public static TwccFeedback Read(ReadOnlySpan<byte> fci, Span<TwccPacketStatus> statuses)
    {
        if (fci.Length < 8)
        {
            throw RtpParseException.Truncated("transport-cc feedback");
        }

        var baseSeq = BinaryPrimitives.ReadUInt16BigEndian(fci);
        int count = BinaryPrimitives.ReadUInt16BigEndian(fci[2..]);
        var reference = (uint)(fci[4] << 16 | fci[5] << 8 | fci[6]);
        var feedback = new TwccFeedback(baseSeq, count, reference, fci[7]);
        if (count == 0)
        {
            throw RtpParseException.Invalid("rtcp_twcc", "transport-cc feedback with zero statuses");
        }

        if (count > statuses.Length)
        {
            throw RtpParseException.Invalid("rtcp_twcc", "more statuses than the caller's buffer");
        }

        // Packet chunks.
        var pos = 8;
        var n = 0;
        while (n < count)
        {
            if (pos + 2 > fci.Length)
            {
                throw RtpParseException.Invalid("rtcp_twcc", "packet chunks end before the status count");
            }

            var chunk = BinaryPrimitives.ReadUInt16BigEndian(fci[pos..]);
            pos += 2;
            if ((chunk & 0x8000) == 0)
            {
                // §3.1.3 run length chunk: T=0 | S(2) | run length(13)
                var symbol = (chunk >> 13) & 0x3;
                var run = chunk & 0x1FFF;
                CheckSymbol(symbol);
                for (var k = 0; k < run && n < count; k++)
                {
                    statuses[n] = new TwccPacketStatus((ushort)(baseSeq + n), (TwccSymbol)symbol, 0);
                    n++;
                }
            }
            else if ((chunk & 0x4000) == 0)
            {
                // §3.1.4 status vector, one-bit symbols: 14 × (0 not received, 1 small delta)
                for (var k = 13; k >= 0 && n < count; k--)
                {
                    statuses[n] = new TwccPacketStatus((ushort)(baseSeq + n), (TwccSymbol)((chunk >> k) & 1), 0);
                    n++;
                }
            }
            else
            {
                // §3.1.4 status vector, two-bit symbols: 7 × symbol
                for (var k = 6; k >= 0 && n < count; k--)
                {
                    var symbol = (chunk >> (2 * k)) & 0x3;
                    CheckSymbol(symbol);
                    statuses[n] = new TwccPacketStatus((ushort)(baseSeq + n), (TwccSymbol)symbol, 0);
                    n++;
                }
            }
        }

        // §3.1.5 receive deltas, one per received packet, in order.
        for (var i = 0; i < count; i++)
        {
            switch (statuses[i].Symbol)
            {
                case TwccSymbol.SmallDelta:
                    if (pos + 1 > fci.Length)
                    {
                        throw RtpParseException.Invalid("rtcp_twcc", "receive deltas cut off");
                    }

                    statuses[i] = statuses[i] with { DeltaTicks = fci[pos++] };
                    break;
                case TwccSymbol.LargeOrNegativeDelta:
                    if (pos + 2 > fci.Length)
                    {
                        throw RtpParseException.Invalid("rtcp_twcc", "receive deltas cut off");
                    }

                    statuses[i] = statuses[i] with { DeltaTicks = BinaryPrimitives.ReadInt16BigEndian(fci[pos..]) };
                    pos += 2;
                    break;
            }
        }

        return feedback;
    }

    private static void CheckSymbol(int symbol)
    {
        if (symbol == 3)
        {
            throw RtpParseException.Invalid("rtcp_twcc", "reserved packet status symbol 3");
        }
    }

    /// <summary>
    /// Builds a feedback for consecutive sequence numbers starting at <paramref name="baseSequence"/>. The symbol of each
    /// received packet follows from its delta (0…255 ticks → small, else large; outside int16 → error). Chunk choice:
    /// run-length for runs ≥ 14, else a one-bit vector when the next 14 symbols are 0/1, else run-length for runs ≥ 7,
    /// else a two-bit vector. The FCI is zero-padded to 32 bits with the RTCP padding bit set. Returns the packet length.
    /// </summary>
    public static int Write(Span<byte> dest, uint senderSsrc, uint mediaSsrc, ushort baseSequence, uint referenceTime,
        byte feedbackPacketCount, ReadOnlySpan<TwccPacketStatus> statuses)
    {
        if (statuses.IsEmpty || statuses.Length > ushort.MaxValue)
        {
            throw new ArgumentException("1–65535 statuses", nameof(statuses));
        }

        Span<byte> symbols = statuses.Length <= 4096 ? stackalloc byte[statuses.Length] : new byte[statuses.Length];
        var deltaBytes = 0;
        for (var i = 0; i < statuses.Length; i++)
        {
            if (statuses[i].Sequence != (ushort)(baseSequence + i))
            {
                throw new ArgumentException("statuses must be consecutive from the base sequence", nameof(statuses));
            }

            if (!statuses[i].Received)
            {
                symbols[i] = 0;
            }
            else if (statuses[i].DeltaTicks is >= 0 and <= 255)
            {
                symbols[i] = 1;
                deltaBytes++;
            }
            else if (statuses[i].DeltaTicks is >= short.MinValue and <= short.MaxValue)
            {
                symbols[i] = 2;
                deltaBytes += 2;
            }
            else
            {
                throw new ArgumentException($"delta {statuses[i].DeltaTicks} does not fit 16 bits; start a new feedback", nameof(statuses));
            }
        }

        // First pass: chunk count.
        var chunks = 0;
        for (var i = 0; i < symbols.Length; i += NextChunk(symbols, i, out _, out _))
        {
            chunks++;
        }

        var unpadded = 12 + 8 + 2 * chunks + deltaBytes;
        var length = (unpadded + 3) & ~3;
        FeedbackHeader.Write(dest, RtcpPacketType.TransportFeedback, FeedbackHeader.FormatTransportCc, senderSsrc, mediaSsrc, length);
        if (length != unpadded)
        {
            dest[0] |= 0x20; // RFC 3550 §6.4.1 padding bit; the last octet carries the pad count
            dest.Slice(unpadded, length - unpadded).Clear();
            dest[length - 1] = (byte)(length - unpadded);
        }

        BinaryPrimitives.WriteUInt16BigEndian(dest[12..], baseSequence);
        BinaryPrimitives.WriteUInt16BigEndian(dest[14..], (ushort)statuses.Length);
        dest[16] = (byte)(referenceTime >> 16);
        dest[17] = (byte)(referenceTime >> 8);
        dest[18] = (byte)referenceTime;
        dest[19] = feedbackPacketCount;
        var at = 20;
        for (var i = 0; i < symbols.Length;)
        {
            var take = NextChunk(symbols, i, out var kind, out var symbol);
            ushort chunk = kind switch
            {
                0 => (ushort)((symbol << 13) | take),
                1 => OneBitVector(symbols, i),
                _ => TwoBitVector(symbols, i),
            };
            BinaryPrimitives.WriteUInt16BigEndian(dest[at..], chunk);
            at += 2;
            i += take;
        }

        foreach (var s in statuses)
        {
            if (!s.Received)
            {
                continue;
            }

            if (s.DeltaTicks is >= 0 and <= 255)
            {
                dest[at++] = (byte)s.DeltaTicks;
            }
            else
            {
                BinaryPrimitives.WriteInt16BigEndian(dest[at..], (short)s.DeltaTicks);
                at += 2;
            }
        }

        return length;
    }

    /// <summary>kind 0 = run length, 1 = one-bit vector, 2 = two-bit vector; returns how many symbols it covers.</summary>
    private static int NextChunk(ReadOnlySpan<byte> symbols, int i, out int kind, out int symbol)
    {
        symbol = symbols[i];
        var run = 1;
        while (i + run < symbols.Length && symbols[i + run] == symbol && run < 0x1FFF)
        {
            run++;
        }

        var remaining = symbols.Length - i;
        if (run >= 14 || run == remaining && run >= 7)
        {
            kind = 0;
            return run;
        }

        var window = Math.Min(14, remaining);
        var allSmall = true;
        for (var k = 0; k < window; k++)
        {
            allSmall &= symbols[i + k] <= 1;
        }

        if (allSmall)
        {
            kind = 1;
            return window;
        }

        if (run >= 7)
        {
            kind = 0;
            return run;
        }

        kind = 2;
        return Math.Min(7, remaining);
    }

    private static ushort OneBitVector(ReadOnlySpan<byte> symbols, int i)
    {
        var chunk = 0x8000;
        for (var k = 0; k < 14 && i + k < symbols.Length; k++)
        {
            chunk |= symbols[i + k] << (13 - k);
        }

        return (ushort)chunk;
    }

    private static ushort TwoBitVector(ReadOnlySpan<byte> symbols, int i)
    {
        var chunk = 0xC000;
        for (var k = 0; k < 7 && i + k < symbols.Length; k++)
        {
            chunk |= symbols[i + k] << (2 * (6 - k));
        }

        return (ushort)chunk;
    }
}
