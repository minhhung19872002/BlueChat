namespace BlueChat.Media.Rtp.Payload;

/// <summary>AV1 aggregation header and first OBU (AV1 RTP specification §4.4, §4.5).</summary>
public readonly record struct Av1PayloadInfo
{
    /// <summary>Z: the first OBU element continues an OBU from the previous packet.</summary>
    public bool ContinuesPrevious { get; init; }

    /// <summary>Y: the last OBU element continues in the next packet.</summary>
    public bool ContinuesNext { get; init; }

    /// <summary>W: number of OBU elements (0 = each has a length field).</summary>
    public int ElementCountField { get; init; }

    /// <summary>N: first packet of a coded video sequence — a key frame.</summary>
    public bool NewCodedVideoSequence { get; init; }

    public bool HasSequenceHeader { get; init; }

    /// <summary>OBU type of the first whole OBU in the packet, −1 when the packet starts mid-OBU.</summary>
    public int FirstObuType { get; init; }

    public int TemporalLayer { get; init; }
    public int SpatialLayer { get; init; }
    public int ObuElementCount { get; init; }

    public bool IsKeyFrame => NewCodedVideoSequence;
}

/// <summary>AV1 RTP payload inspection (https://aomediacodec.github.io/av1-rtp-spec/).</summary>
public static class Av1
{
    public const int ObuSequenceHeader = 1;
    public const int ObuTemporalDelimiter = 2;
    public const int ObuFrameHeader = 3;
    public const int ObuFrame = 6;

    /// <summary>Upper bound on OBU elements walked in one packet.</summary>
    public const int MaxElements = 256;

    /// <summary>§4.4 aggregation header Z|Y|W(2)|N|-(3), §4.5 OBU elements (LEB128 lengths), AV1 §5.3 OBU header.</summary>
    public static Av1PayloadInfo Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw RtpParseException.Truncated("av1 payload");
        }

        var agg = payload[0];
        var z = (agg & 0x80) != 0;
        var y = (agg & 0x40) != 0;
        var w = (agg >> 4) & 0x03;
        var n = (agg & 0x08) != 0;
        if (n && z)
        {
            throw RtpParseException.Invalid("av1_aggregation", "N=1 with Z=1 (a new sequence cannot continue an OBU)");
        }

        var pos = 1;
        var count = 0;
        var seq = false;
        int firstType = -1, tid = -1, sid = -1;
        while (pos < payload.Length)
        {
            if (++count > MaxElements)
            {
                throw RtpParseException.Invalid("av1_aggregation", "too many OBU elements");
            }

            int length;
            var last = w != 0 && count == w;
            if (last)
            {
                length = payload.Length - pos;
            }
            else
            {
                // Compare before narrowing: a leb128 value up to 2^32 − 1 would turn negative as int.
                var declared = Leb128(payload, ref pos);
                if (declared > (ulong)(payload.Length - pos))
                {
                    throw RtpParseException.Invalid("av1_obu_length", "OBU element length runs past the payload");
                }

                length = (int)declared;
            }

            var isContinuation = count == 1 && z;
            if (!isContinuation && length > 0)
            {
                // AV1 §5.3.1: forbidden(1) type(4) extension_flag(1) has_size(1) reserved(1)
                var header = payload[pos];
                if ((header & 0x80) != 0)
                {
                    throw RtpParseException.Invalid("av1_obu_header", "OBU forbidden bit set");
                }

                var type = (header >> 3) & 0x0F;
                if (firstType < 0)
                {
                    firstType = type;
                }

                seq |= type == ObuSequenceHeader;
                if ((header & 0x04) != 0 && length >= 2 && tid < 0)
                {
                    // §5.3.3 extension: temporal_id(3) spatial_id(2) reserved(3)
                    tid = payload[pos + 1] >> 5;
                    sid = (payload[pos + 1] >> 3) & 0x03;
                }
            }

            pos += length;
            if (last)
            {
                break;
            }
        }

        if (w != 0 && count != w)
        {
            throw RtpParseException.Invalid("av1_aggregation", $"W={w} but {count} OBU elements present");
        }

        return new Av1PayloadInfo
        {
            ContinuesPrevious = z, ContinuesNext = y, ElementCountField = w, NewCodedVideoSequence = n, HasSequenceHeader = seq,
            FirstObuType = firstType, TemporalLayer = tid, SpatialLayer = sid, ObuElementCount = count,
        };
    }

    /// <summary>AV1 §4.10.5 leb128(): little-endian base-128, at most 8 bytes, value below 2^32.</summary>
    internal static ulong Leb128(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            if (pos >= data.Length)
            {
                throw RtpParseException.Truncated("av1 leb128");
            }

            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return value <= uint.MaxValue ? value : throw RtpParseException.Invalid("av1_leb128", "leb128 value above 2^32 − 1");
            }
        }

        throw RtpParseException.Invalid("av1_leb128", "leb128 longer than 8 bytes");
    }
}

/// <summary>Opus TOC byte (RFC 6716 §3.1) as carried in RTP (RFC 7587 §4.2).</summary>
public readonly record struct OpusToc(int Config, bool Stereo, int FrameCountCode, int FrameCount, int FrameDurationMicroseconds)
{
    /// <summary>SILK 0–11, hybrid 12–15, CELT 16–31 (RFC 6716 §3.1 table 2).</summary>
    public string Mode => Config < 12 ? "silk" : Config < 16 ? "hybrid" : "celt";

    public int PacketDurationMicroseconds => FrameCount * FrameDurationMicroseconds;

    /// <summary>RFC 6716 §3.1–3.2: TOC config/stereo/code; code 3 reads the frame count byte.</summary>
    public static OpusToc Read(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw RtpParseException.Truncated("opus toc");
        }

        var toc = payload[0];
        var config = toc >> 3;
        var code = toc & 0x03;
        int frames;
        switch (code)
        {
            case 0:
                frames = 1;
                break;
            case 1:
            case 2:
                frames = 2;
                break;
            default:
                if (payload.Length < 2)
                {
                    throw RtpParseException.Truncated("opus frame count");
                }

                frames = payload[1] & 0x3F;
                if (frames == 0)
                {
                    throw RtpParseException.Invalid("opus_frame_count", "code 3 packet with zero frames (RFC 6716 §3.2.5)");
                }

                break;
        }

        int duration;
        if (config < 12)
        {
            duration = (config & 3) switch { 0 => 10_000, 1 => 20_000, 2 => 40_000, _ => 60_000 };
        }
        else if (config < 16)
        {
            duration = (config & 1) == 0 ? 10_000 : 20_000;
        }
        else
        {
            duration = (config & 3) switch { 0 => 2_500, 1 => 5_000, 2 => 10_000, _ => 20_000 };
        }

        if (frames * duration > 120_000)
        {
            throw RtpParseException.Invalid("opus_duration", "Opus packet longer than 120 ms (RFC 6716 §3.2.5)");
        }

        return new OpusToc(config, (toc & 0x04) != 0, code, frames, duration);
    }
}
