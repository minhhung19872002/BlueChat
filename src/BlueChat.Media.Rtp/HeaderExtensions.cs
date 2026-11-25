using System.Buffers.Binary;

namespace BlueChat.Media.Rtp;

/// <summary>One RFC 8285 element: its id and where its data sits in the packet.</summary>
public readonly record struct HeaderExtensionElement(byte Id, int Offset, int Length);

/// <summary>
/// Iterates RFC 8285 header extension elements of a parsed packet without allocating:
/// <c>foreach (var e in new HeaderExtensionReader(packet, header)) …</c>. Malformed blocks throw
/// <see cref="RtpParseException"/> (code <c>rtp_extension</c>). Unknown profiles (not 0xBEDE / 0x100X) yield nothing.
/// </summary>
public ref struct HeaderExtensionReader
{
    private readonly ReadOnlySpan<byte> _packet;
    private readonly int _end;
    private readonly bool _twoByte;
    private readonly bool _supported;
    private int _pos;

    public HeaderExtensionReader(ReadOnlySpan<byte> packet, in RtpHeader header)
    {
        _packet = packet;
        _supported = header.HasOneByteExtensions || header.HasTwoByteExtensions;
        _twoByte = header.HasTwoByteExtensions;
        _pos = header.ExtensionOffset;
        _end = header.ExtensionOffset + header.ExtensionLength;
        Current = default;
    }

    public HeaderExtensionElement Current { get; private set; }

    public readonly HeaderExtensionReader GetEnumerator() => this;

    /// <summary>RFC 8285 §4.2 (one-byte) and §4.3 (two-byte) element framing.</summary>
    public bool MoveNext()
    {
        if (!_supported)
        {
            return false;
        }

        while (_pos < _end)
        {
            if (_twoByte)
            {
                var id = _packet[_pos];
                if (id == 0)
                {
                    _pos++; // padding byte
                    continue;
                }

                if (_pos + 2 > _end)
                {
                    throw RtpParseException.Invalid("rtp_extension", "two-byte extension header cut off");
                }

                var len = _packet[_pos + 1];
                if (_pos + 2 + len > _end)
                {
                    throw RtpParseException.Invalid("rtp_extension", $"two-byte extension {id} longer than the block");
                }

                Current = new HeaderExtensionElement(id, _pos + 2, len);
                _pos += 2 + len;
                return true;
            }
            else
            {
                var b = _packet[_pos];
                var id = (byte)(b >> 4);
                if (id == 0)
                {
                    _pos++; // padding byte (its length nibble must be ignored)
                    continue;
                }

                if (id == 15)
                {
                    _pos = _end; // §4.2: stop processing the block
                    return false;
                }

                var len = (b & 0x0F) + 1;
                if (_pos + 1 + len > _end)
                {
                    throw RtpParseException.Invalid("rtp_extension", $"one-byte extension {id} longer than the block");
                }

                Current = new HeaderExtensionElement(id, _pos + 1, len);
                _pos += 1 + len;
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the element with <paramref name="id"/>; false when absent.</summary>
    public static bool TryFind(ReadOnlySpan<byte> packet, in RtpHeader header, byte id, out HeaderExtensionElement element)
    {
        foreach (var e in new HeaderExtensionReader(packet, header))
        {
            if (e.Id == id)
            {
                element = e;
                return true;
            }
        }

        element = default;
        return false;
    }
}

/// <summary>
/// Typed codecs for the header extensions BlueChat negotiates. Readers take the element's data; writers fill a
/// caller span (use <see cref="RtpPacketWriter.AddExtension"/> or rewrite an existing element in place).
/// </summary>
public static class RtpExtensions
{
    public const string AbsSendTimeUri = "http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time";
    public const string AudioLevelUri = "urn:ietf:params:rtp-hdrext:ssrc-audio-level";
    public const string TransportWideCcUri = "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01";
    public const string MidUri = "urn:ietf:params:rtp-hdrext:sdes:mid";
    public const string RidUri = "urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id";
    public const string RepairedRidUri = "urn:ietf:params:rtp-hdrext:sdes:repaired-rtp-stream-id";
    public const string DependencyDescriptorUri = "https://aomediacodec.github.io/av1-rtp-spec/#dependency-descriptor-rtp-header-extension";

    /// <summary>abs-send-time: 24-bit 6.18 fixed-point seconds (webrtc.org abs-send-time).</summary>
    public static uint ReadAbsSendTime(ReadOnlySpan<byte> data)
    {
        Expect(data, 3, "abs-send-time");
        return (uint)(data[0] << 16 | data[1] << 8 | data[2]);
    }

    public static void WriteAbsSendTime(Span<byte> data, uint value24)
    {
        data[0] = (byte)(value24 >> 16);
        data[1] = (byte)(value24 >> 8);
        data[2] = (byte)value24;
    }

    /// <summary>
    /// abs-send-time from a time in microseconds: floor(seconds × 2^18) mod 2^24. Whole seconds and the fraction are
    /// handled apart so large clocks (NTP epoch) do not overflow. Pion and libwebrtc feed NTP time; any epoch works
    /// for the receiver, which only uses differences.
    /// </summary>
    public static uint AbsSendTimeFromMicroseconds(long microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        var seconds = microseconds / 1_000_000;
        var fraction = microseconds % 1_000_000;
        return (uint)((((seconds & 63) << 18) + fraction * (1L << 18) / 1_000_000) & 0xFFFFFF);
    }

    /// <summary>RFC 6464 §3: V flag (voice activity) and level in −dBov (0 loudest … 127 silence).</summary>
    public static (bool Voice, byte Level) ReadAudioLevel(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            throw RtpParseException.Truncated("ssrc-audio-level");
        }

        return ((data[0] & 0x80) != 0, (byte)(data[0] & 0x7F));
    }

    public static void WriteAudioLevel(Span<byte> data, bool voice, byte level) =>
        data[0] = (byte)((voice ? 0x80 : 0) | Math.Min(level, (byte)127));

    /// <summary>Transport-wide sequence number (draft-holmer-rmcat-transport-wide-cc-extensions-01 §2): 16 bits.</summary>
    public static ushort ReadTransportSequenceNumber(ReadOnlySpan<byte> data)
    {
        Expect(data, 2, "transport-cc");
        return BinaryPrimitives.ReadUInt16BigEndian(data);
    }

    public static void WriteTransportSequenceNumber(Span<byte> data, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data, value);

    /// <summary>
    /// MID / RID / repaired RID (RFC 9143 §15, RFC 8852 §3): the raw identifier. RFC 8851 §10 limits RID to
    /// alphanumerics, '-' and '_'; MID is a token (RFC 8843 §5). Returns false when a byte is outside that set.
    /// </summary>
    public static bool IsValidStreamId(ReadOnlySpan<byte> data, bool isMid)
    {
        if (data.IsEmpty || data.Length > 255)
        {
            return false;
        }

        foreach (var c in data)
        {
            var ok = c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                     || c == '-' || c == '_' || (isMid && c is (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&'
                         or (byte)'\'' or (byte)'*' or (byte)'+' or (byte)'.' or (byte)'^' or (byte)'`' or (byte)'{' or (byte)'|' or (byte)'}' or (byte)'~');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>MID/RID as a string (allocates: call once per new stream, not per packet).</summary>
    public static string ReadStreamId(ReadOnlySpan<byte> data, bool isMid = false) =>
        IsValidStreamId(data, isMid)
            ? System.Text.Encoding.ASCII.GetString(data)
            : throw RtpParseException.Invalid("rtp_stream_id", "MID/RID with characters outside RFC 8851 §10 / RFC 8843 §5");

    /// <summary>
    /// AV1 RTP spec Appendix A.8.1 mandatory descriptor fields: start_of_frame (1), end_of_frame (1),
    /// frame_dependency_template_id (6), frame_number (16).
    /// </summary>
    public static DependencyDescriptorMandatory ReadDependencyDescriptor(ReadOnlySpan<byte> data)
    {
        Expect(data, 3, "dependency descriptor");
        return new DependencyDescriptorMandatory(
            (data[0] & 0x80) != 0, (data[0] & 0x40) != 0, (byte)(data[0] & 0x3F),
            BinaryPrimitives.ReadUInt16BigEndian(data[1..]), data.Length > 3);
    }

    /// <summary>Writes the three mandatory bytes (no extended fields).</summary>
    public static void WriteDependencyDescriptor(Span<byte> data, DependencyDescriptorMandatory value)
    {
        data[0] = (byte)((value.StartOfFrame ? 0x80 : 0) | (value.EndOfFrame ? 0x40 : 0) | (value.TemplateId & 0x3F));
        BinaryPrimitives.WriteUInt16BigEndian(data[1..], value.FrameNumber);
    }

    private static void Expect(ReadOnlySpan<byte> data, int minimum, string what)
    {
        if (data.Length < minimum)
        {
            throw RtpParseException.Truncated(what);
        }
    }
}

/// <param name="HasExtendedFields">More than the 3 mandatory bytes are present (template structure, etc.).</param>
public readonly record struct DependencyDescriptorMandatory(
    bool StartOfFrame, bool EndOfFrame, byte TemplateId, ushort FrameNumber, bool HasExtendedFields = false);
