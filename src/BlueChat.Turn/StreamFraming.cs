using System.Buffers.Binary;

namespace BlueChat.Turn;

/// <summary>
/// Message boundaries on TCP/TLS (RFC 8656 §3.1, §12.5): a STUN message is delimited by its own length field
/// (20-byte header + length); a ChannelData message by its length field, padded to a multiple of 4 bytes.
/// The first two bits tell them apart (00 STUN, 01 ChannelData); anything else closes the connection.
/// </summary>
public static class StreamFraming
{
    /// <summary>
    /// Reads one message into <paramref name="buffer"/>. Returns the message length without ChannelData padding,
    /// 0 at a clean end of stream. Throws <see cref="InvalidDataException"/> on bad framing or an oversized message.
    /// </summary>
    public static async ValueTask<int> ReadMessageAsync(Stream stream, byte[] buffer, int maxLength, CancellationToken ct)
    {
        if (!await ReadExactlyOrEndAsync(stream, buffer.AsMemory(0, 4), ct).ConfigureAwait(false))
        {
            return 0;
        }

        var kind = buffer[0] & 0xC0;
        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
        int total, message;
        if (kind == 0x00)
        {
            total = 20 + length;
            message = total;
        }
        else if (kind == 0x40)
        {
            message = 4 + length;
            total = 4 + ((length + 3) & ~3);
        }
        else
        {
            throw new InvalidDataException("neither STUN nor ChannelData");
        }

        if (total > maxLength || total > buffer.Length)
        {
            throw new InvalidDataException("message too large");
        }

        await stream.ReadExactlyAsync(buffer.AsMemory(4, total - 4), ct).ConfigureAwait(false);
        return message;
    }

    private static async ValueTask<bool> ReadExactlyOrEndAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await stream.ReadAsync(destination[read..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException();
            }

            read += n;
        }

        return true;
    }
}
