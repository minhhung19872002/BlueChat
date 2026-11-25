using System.Buffers;
using System.Buffers.Binary;

namespace BlueChat.Media.Ice;

/// <summary>
/// RFC 4571 §2 framing used by ICE-TCP (RFC 6544 §3): each packet (STUN, DTLS, RTP, RTCP) is prefixed with a
/// 16-bit big-endian length. Decoding works over System.IO.Pipelines buffers; zero-length frames are protocol errors.
/// </summary>
public static class Rfc4571Framer
{
    public const int MaxFrame = ushort.MaxValue;

    /// <summary>Takes one complete frame from <paramref name="buffer"/> if available.</summary>
    public static FrameResult TryRead(ref ReadOnlySequence<byte> buffer, int maxFrame, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if (buffer.Length < 2)
        {
            return FrameResult.NeedMore;
        }

        Span<byte> header = stackalloc byte[2];
        buffer.Slice(0, 2).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length == 0 || length > maxFrame)
        {
            return FrameResult.Invalid;
        }

        if (buffer.Length < 2 + length)
        {
            return FrameResult.NeedMore;
        }

        frame = buffer.Slice(2, length);
        buffer = buffer.Slice(2 + length);
        return FrameResult.Frame;
    }

    /// <summary>Writes the 2-byte header and the packet into <paramref name="destination"/>; returns bytes written.</summary>
    public static int Write(ReadOnlySpan<byte> packet, Span<byte> destination)
    {
        if (packet.IsEmpty || packet.Length > MaxFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(packet));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)packet.Length);
        packet.CopyTo(destination[2..]);
        return 2 + packet.Length;
    }
}

public enum FrameResult
{
    NeedMore,
    Frame,
    Invalid,
}
