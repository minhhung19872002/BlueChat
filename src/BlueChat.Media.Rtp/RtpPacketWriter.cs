using System.Buffers.Binary;

namespace BlueChat.Media.Rtp;

/// <summary>
/// Writes an RTP packet into a caller buffer: fixed header and CSRCs (RFC 3550 §5.1), optional RFC 8285 header
/// extensions (one-byte or two-byte form chosen up front), payload, padding. Call order: constructor →
/// <see cref="AddExtension"/>* → <see cref="WritePayload"/> → <see cref="Finish"/>.
/// </summary>
public ref struct RtpPacketWriter
{
    private readonly Span<byte> _dest;
    private readonly bool _twoByte;
    private int _length;
    private int _extStart = -1;
    private bool _payloadWritten;

    public RtpPacketWriter(Span<byte> destination, RtpHeaderFields fields, ReadOnlySpan<uint> csrcs = default, bool twoByteExtensions = false)
    {
        if (csrcs.Length > 15)
        {
            throw new ArgumentException("at most 15 CSRCs (RFC 3550 §5.1)", nameof(csrcs));
        }

        var needed = RtpHeader.FixedLength + 4 * csrcs.Length;
        if (destination.Length < needed)
        {
            throw new ArgumentException("destination too small", nameof(destination));
        }

        _dest = destination;
        _twoByte = twoByteExtensions;
        destination[0] = (byte)(0x80 | csrcs.Length);
        destination[1] = (byte)((fields.Marker ? 0x80 : 0) | (fields.PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], fields.SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], fields.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], fields.Ssrc);
        for (var i = 0; i < csrcs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[(12 + 4 * i)..], csrcs[i]);
        }

        _length = needed;
    }

    /// <summary>RFC 8285 §4.2 (one-byte: id 1–14, 1–16 bytes) or §4.3 (two-byte: id 1–255, 0–255 bytes).</summary>
    public void AddExtension(byte id, ReadOnlySpan<byte> data)
    {
        if (_payloadWritten)
        {
            throw new InvalidOperationException("extensions must come before the payload");
        }

        if (_twoByte ? id == 0 || data.Length > 255 : id is 0 or 15 || data.Length is 0 or > 16)
        {
            throw new ArgumentException($"extension id {id} / length {data.Length} not allowed in the {(_twoByte ? "two" : "one")}-byte form (RFC 8285)");
        }

        if (_extStart < 0)
        {
            Need(4);
            _dest[0] |= 0x10;
            BinaryPrimitives.WriteUInt16BigEndian(_dest[_length..], _twoByte ? (ushort)0x1000 : RtpHeader.OneByteProfile);
            _extStart = _length + 4;
            _length += 4;
        }

        if (_twoByte)
        {
            Need(2 + data.Length);
            _dest[_length++] = id;
            _dest[_length++] = (byte)data.Length;
        }
        else
        {
            Need(1 + data.Length);
            _dest[_length++] = (byte)((id << 4) | (data.Length - 1));
        }

        data.CopyTo(_dest[_length..]);
        _length += data.Length;
    }

    public void WritePayload(ReadOnlySpan<byte> payload)
    {
        CloseExtensions();
        Need(payload.Length);
        payload.CopyTo(_dest[_length..]);
        _length += payload.Length;
        _payloadWritten = true;
    }

    /// <summary>Optional padding (RFC 3550 §5.1); returns the total packet length.</summary>
    public int Finish(byte paddingLength = 0)
    {
        CloseExtensions();
        if (paddingLength > 0)
        {
            Need(paddingLength);
            _dest.Slice(_length, paddingLength - 1).Clear();
            _length += paddingLength;
            _dest[_length - 1] = paddingLength;
            _dest[0] |= 0x20;
        }

        return _length;
    }

    private void CloseExtensions()
    {
        if (_extStart < 0 || _payloadWritten)
        {
            return;
        }

        // Pad the block to a 32-bit boundary with zero bytes (RFC 8285 §4.2: id 0 padding) and write its length.
        var dataLength = _length - _extStart;
        var padded = (dataLength + 3) & ~3;
        Need(padded - dataLength);
        _dest.Slice(_length, padded - dataLength).Clear();
        _length = _extStart + padded;
        BinaryPrimitives.WriteUInt16BigEndian(_dest[(_extStart - 2)..], (ushort)(padded / 4));
        _payloadWritten = true; // no more extensions
    }

    private readonly void Need(int bytes)
    {
        if (_length + bytes > _dest.Length)
        {
            throw new ArgumentException("destination too small");
        }
    }
}
