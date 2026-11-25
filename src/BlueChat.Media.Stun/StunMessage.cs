using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace BlueChat.Media.Stun;

/// <summary>
/// Read-only view of one STUN message over the received bytes (RFC 8489 §5, §14). Parsing validates the framing only;
/// authentication is <see cref="StunIntegrity"/>. No allocation: attribute values are slices of the input.
/// </summary>
public readonly ref struct StunMessage
{
    public const int HeaderLength = 20;
    public const uint MagicCookie = 0x2112A442;
    public const int DefaultMaxLength = 4096;
    public const int MaxAttributes = 64;

    private StunMessage(ReadOnlySpan<byte> bytes)
    {
        Bytes = bytes;
    }

    /// <summary>The whole message (header + attributes).</summary>
    public ReadOnlySpan<byte> Bytes { get; }

    public ushort Type => BinaryPrimitives.ReadUInt16BigEndian(Bytes);

    /// <summary>RFC 8489 §5: method bits M0–M11 interleaved with class bits C0, C1.</summary>
    public ushort Method => StunMessageType.MethodOf(Type);

    public StunClass Class => StunMessageType.ClassOf(Type);

    public ReadOnlySpan<byte> TransactionId => Bytes.Slice(8, 12);

    /// <summary>
    /// RFC 7983 §7: the first byte of a STUN message is 0–3; RFC 8489 §6: magic cookie at bytes 4–7.
    /// Cheap pre-check for demultiplexing before <see cref="TryParse"/>.
    /// </summary>
    public static bool LooksLikeStun(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderLength && data[0] < 4 && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == MagicCookie;

    /// <summary>RFC 8489 §5, §14: header and attribute framing; FINGERPRINT, if present, must be last (§14.7).</summary>
    public static StunParseError TryParse(ReadOnlySpan<byte> data, out StunMessage message, int maxLength = DefaultMaxLength)
    {
        message = default;
        if (data.Length < HeaderLength)
        {
            return StunParseError.TooShort;
        }

        if (data.Length > maxLength)
        {
            return StunParseError.TooLong;
        }

        if ((data[0] & 0xC0) != 0)
        {
            return StunParseError.NotStun; // §5: the two most significant bits are zero
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie)
        {
            return StunParseError.BadMagicCookie;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if ((length & 3) != 0)
        {
            return StunParseError.LengthNotAligned; // §5: always a multiple of 4
        }

        if (HeaderLength + length != data.Length)
        {
            return StunParseError.LengthMismatch;
        }

        var offset = HeaderLength;
        var count = 0;
        var sawFingerprint = false;
        while (offset < data.Length)
        {
            if (sawFingerprint)
            {
                return StunParseError.FingerprintNotLast;
            }

            if (data.Length - offset < 4)
            {
                return StunParseError.AttributeOverflow;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var padded = (valueLength + 3) & ~3; // §14: values padded to 4 bytes
            if (offset + 4 + padded > data.Length)
            {
                return StunParseError.AttributeOverflow;
            }

            if (++count > MaxAttributes)
            {
                return StunParseError.TooManyAttributes;
            }

            sawFingerprint = type == StunAttr.Fingerprint;
            offset += 4 + padded;
        }

        message = new StunMessage(data);
        return StunParseError.None;
    }

    public AttributeEnumerator Attributes => new(Bytes);

    /// <summary>First attribute of the given type (RFC 8489 §14: duplicates after the first are ignored).</summary>
    public bool TryGetAttribute(ushort type, out ReadOnlySpan<byte> value)
    {
        foreach (var a in Attributes)
        {
            if (a.Type == type)
            {
                value = a.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Offset of the attribute header of the first attribute of this type, or -1.</summary>
    public int OffsetOf(ushort type)
    {
        foreach (var a in Attributes)
        {
            if (a.Type == type)
            {
                return a.Offset;
            }
        }

        return -1;
    }

    public bool Has(ushort type) => OffsetOf(type) >= 0;

    public string? GetString(ushort type) =>
        TryGetAttribute(type, out var v) ? Encoding.UTF8.GetString(v) : null;

    public bool TryGetUInt32(ushort type, out uint value)
    {
        value = 0;
        if (!TryGetAttribute(type, out var v) || v.Length != 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(v);
        return true;
    }

    public bool TryGetUInt64(ushort type, out ulong value)
    {
        value = 0;
        if (!TryGetAttribute(type, out var v) || v.Length != 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64BigEndian(v);
        return true;
    }

    /// <summary>RFC 8489 §14.2 XOR-MAPPED-ADDRESS (and the TURN XOR-PEER/RELAYED-ADDRESS of the same format).</summary>
    public bool TryGetXorAddress(ushort type, out IPEndPoint? endPoint)
    {
        endPoint = null;
        return TryGetAttribute(type, out var v) && StunAddress.TryDecodeXor(v, TransactionId, out endPoint);
    }

    /// <summary>RFC 8489 §14.1 MAPPED-ADDRESS (not XORed).</summary>
    public bool TryGetAddress(ushort type, out IPEndPoint? endPoint)
    {
        endPoint = null;
        return TryGetAttribute(type, out var v) && StunAddress.TryDecode(v, out endPoint);
    }

    /// <summary>RFC 8489 §14.8 ERROR-CODE: class × 100 + number, and the reason phrase.</summary>
    public bool TryGetErrorCode(out int code, out string reason)
    {
        code = 0;
        reason = "";
        if (!TryGetAttribute(StunAttr.ErrorCode, out var v) || v.Length < 4)
        {
            return false;
        }

        code = ((v[2] & 0x07) * 100) + v[3];
        reason = Encoding.UTF8.GetString(v[4..]);
        return true;
    }

    /// <summary>
    /// RFC 8489 §6.3.1.1 / §15: comprehension-required attribute types not in <paramref name="known"/>
    /// (for the 420 UNKNOWN-ATTRIBUTES answer). Writes at most destination.Length entries.
    /// </summary>
    public int UnknownComprehensionRequired(ReadOnlySpan<ushort> known, Span<ushort> destination)
    {
        var n = 0;
        foreach (var a in Attributes)
        {
            if (StunAttr.IsComprehensionRequired(a.Type) && !known.Contains(a.Type) && n < destination.Length
                && !destination[..n].Contains(a.Type))
            {
                destination[n++] = a.Type;
            }
        }

        return n;
    }

    public ref struct AttributeEnumerator
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _next;

        internal AttributeEnumerator(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _next = HeaderLength;
            Current = default;
        }

        public StunAttribute Current { get; private set; }

        public readonly AttributeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next + 4 > _bytes.Length)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(_bytes[_next..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(_bytes[(_next + 2)..]);
            Current = new StunAttribute(type, _next, _bytes.Slice(_next + 4, length));
            _next += 4 + ((length + 3) & ~3);
            return true;
        }
    }
}

/// <summary>One attribute: type, offset of its header within the message, value without padding.</summary>
public readonly ref struct StunAttribute(ushort type, int offset, ReadOnlySpan<byte> value)
{
    public ushort Type { get; } = type;

    public int Offset { get; } = offset;

    public ReadOnlySpan<byte> Value { get; } = value;
}

/// <summary>RFC 8489 §5 type encoding: M11..M7 C1 M6..M4 C0 M3..M0.</summary>
public static class StunMessageType
{
    public static ushort Compose(ushort method, StunClass cls)
    {
        var c = (int)cls;
        return (ushort)((method & 0x000F) | ((method & 0x0070) << 1) | ((method & 0x0F80) << 2)
                        | ((c & 1) << 4) | ((c & 2) << 7));
    }

    public static ushort MethodOf(ushort type) =>
        (ushort)((type & 0x000F) | ((type & 0x00E0) >> 1) | ((type & 0x3E00) >> 2));

    public static StunClass ClassOf(ushort type) => (StunClass)(((type >> 4) & 1) | ((type >> 7) & 2));
}
