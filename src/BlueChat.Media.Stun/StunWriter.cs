using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BlueChat.Media.Stun;

/// <summary>
/// Builds one STUN message into a caller buffer (RFC 8489 §5, §14). Integrity attributes must be added last, in the
/// order MESSAGE-INTEGRITY, MESSAGE-INTEGRITY-SHA256, FINGERPRINT (§14.5–§14.7). Throws if the buffer is too small.
/// </summary>
public ref struct StunWriter
{
    private readonly Span<byte> _buffer;
    private int _length;

    public StunWriter(Span<byte> buffer, ushort method, StunClass cls, ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != 12)
        {
            throw new ArgumentException("transaction id is 96 bits", nameof(transactionId));
        }

        _buffer = buffer;
        BinaryPrimitives.WriteUInt16BigEndian(buffer, StunMessageType.Compose(method, cls));
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], StunMessage.MagicCookie);
        transactionId.CopyTo(buffer[8..]);
        _length = StunMessage.HeaderLength;
    }

    public readonly int Length => _length;

    public readonly ReadOnlySpan<byte> Written => _buffer[.._length];

    /// <summary>RFC 8489 §6: 96-bit transaction id from a cryptographically random source.</summary>
    public static void NewTransactionId(Span<byte> destination) => RandomNumberGenerator.Fill(destination[..12]);

    /// <summary>Generic TLV with zero padding to 4 bytes (§14).</summary>
    public void Add(ushort type, scoped ReadOnlySpan<byte> value)
    {
        var start = Begin(type, value.Length);
        value.CopyTo(_buffer[start..]);
        End(start, value.Length);
    }

    public void AddString(ushort type, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var start = Begin(type, byteCount);
        Encoding.UTF8.GetBytes(value, _buffer[start..]);
        End(start, byteCount);
    }

    public void AddUInt32(ushort type, uint value)
    {
        Span<byte> v = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(v, value);
        Add(type, v);
    }

    public void AddUInt64(ushort type, ulong value)
    {
        Span<byte> v = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(v, value);
        Add(type, v);
    }

    public void AddFlag(ushort type) => Add(type, ReadOnlySpan<byte>.Empty);

    /// <summary>§14.2 XOR-MAPPED-ADDRESS and the TURN attributes of the same format.</summary>
    public void AddXorAddress(ushort type, IPAddress address, int port)
    {
        var length = StunAddress.EncodedLength(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        var start = Begin(type, length);
        StunAddress.Encode(_buffer[start..], address, port, _buffer.Slice(8, 12), xor: true);
        End(start, length);
    }

    /// <summary>§14.8: class = code / 100, number = code % 100, then a UTF-8 reason (≤ 127 characters).</summary>
    public void AddErrorCode(int code, string reason)
    {
        if (code is < 300 or > 699)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        var reasonBytes = Encoding.UTF8.GetByteCount(reason);
        var start = Begin(StunAttr.ErrorCode, 4 + reasonBytes);
        _buffer[start] = 0;
        _buffer[start + 1] = 0;
        _buffer[start + 2] = (byte)(code / 100);
        _buffer[start + 3] = (byte)(code % 100);
        Encoding.UTF8.GetBytes(reason, _buffer[(start + 4)..]);
        End(start, 4 + reasonBytes);
    }

    /// <summary>§14.9 UNKNOWN-ATTRIBUTES: list of 16-bit types.</summary>
    public void AddUnknownAttributes(ReadOnlySpan<ushort> types)
    {
        var start = Begin(StunAttr.UnknownAttributes, types.Length * 2);
        for (var i = 0; i < types.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[(start + (2 * i))..], types[i]);
        }

        End(start, types.Length * 2);
    }

    /// <summary>§14.5 HMAC-SHA1 over everything so far, length field counting this attribute.</summary>
    public void AddMessageIntegrity(scoped ReadOnlySpan<byte> key) => AddMac(StunAttr.MessageIntegrity, 20, key);

    /// <summary>§14.6 full-length HMAC-SHA256.</summary>
    public void AddMessageIntegritySha256(scoped ReadOnlySpan<byte> key) => AddMac(StunAttr.MessageIntegritySha256, 32, key);

    private void AddMac(ushort type, int macLength, scoped ReadOnlySpan<byte> key)
    {
        Span<byte> mac = stackalloc byte[32];
        StunIntegrity.ComputeMac(_buffer[.._length], key, type, macLength, mac);
        Add(type, mac[..macLength]);
    }

    /// <summary>§14.7: always last.</summary>
    public void AddFingerprint()
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[2..], (ushort)(_length - StunMessage.HeaderLength + 8));
        var crc = StunIntegrity.FingerprintOf(_buffer[.._length]);
        AddUInt32(StunAttr.Fingerprint, crc);
    }

    private int Begin(ushort type, int valueLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(valueLength, ushort.MaxValue);

        var padded = (valueLength + 3) & ~3;
        if (_length + 4 + padded > _buffer.Length)
        {
            throw new InvalidOperationException("STUN buffer too small");
        }

        BinaryPrimitives.WriteUInt16BigEndian(_buffer[_length..], type);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[(_length + 2)..], (ushort)valueLength);
        return _length + 4;
    }

    private void End(int valueStart, int valueLength)
    {
        var padded = (valueLength + 3) & ~3;
        _buffer.Slice(valueStart + valueLength, padded - valueLength).Clear();
        _length = valueStart + padded;
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[2..], (ushort)(_length - StunMessage.HeaderLength));
    }
}
