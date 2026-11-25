namespace BlueChat.Media.Rtp;

/// <summary>16-bit RTP sequence number arithmetic with wraparound (RFC 3550 §A.1, RFC 1982 serial numbers).</summary>
public static class SequenceNumbers
{
    /// <summary>True when <paramref name="a"/> comes after <paramref name="b"/> (forward distance 1…32767).</summary>
    public static bool IsNewer(ushort a, ushort b)
    {
        var d = (ushort)(a - b);
        return d != 0 && d < 0x8000;
    }

    /// <summary>Signed distance a − b in the range −32768…32767.</summary>
    public static int Diff(ushort a, ushort b) => (short)(ushort)(a - b);
}

/// <summary>
/// Extends 16-bit sequence numbers to 64 bits (RFC 3550 §A.1 "cycles" counter). Late packets just before a wrap map to
/// the previous cycle; forward jumps of up to 32767 advance. Not thread-safe: one per stream.
/// </summary>
public sealed class SequenceUnwrapper
{
    private long _last = -1;

    public long Unwrap(ushort sequence)
    {
        if (_last < 0)
        {
            _last = sequence;
            return sequence;
        }

        var result = _last + SequenceNumbers.Diff(sequence, (ushort)_last);
        if (result < 0)
        {
            result += 0x10000; // a "late" packet before the first one ever seen cannot be negative
        }

        if (result > _last)
        {
            _last = result;
        }

        return result;
    }
}
