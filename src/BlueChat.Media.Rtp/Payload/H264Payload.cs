using System.Buffers.Binary;

namespace BlueChat.Media.Rtp.Payload;

/// <summary>What one H.264 RTP payload carries (RFC 6184, packetization-mode 0 and 1).</summary>
public readonly record struct H264PayloadInfo
{
    /// <summary>1–23 single NAL unit, 24 STAP-A, 28 FU-A (RFC 6184 §5.4).</summary>
    public int PacketType { get; init; }

    public bool HasIdr { get; init; }
    public bool HasSps { get; init; }
    public bool HasPps { get; init; }

    /// <summary>A NAL unit begins in this packet (single NAL, STAP-A, or FU-A with S=1).</summary>
    public bool IsStartOfNal { get; init; }

    /// <summary>A NAL unit ends in this packet (single NAL, STAP-A, or FU-A with E=1).</summary>
    public bool IsEndOfNal { get; init; }

    public int NalUnitCount { get; init; }

    /// <summary>IDR slice or SPS present: a decoder can start here (SPS+PPS+IDR is how browsers send key frames).</summary>
    public bool IsKeyFrame => HasIdr || HasSps;
}

/// <summary>H.264 RTP payload inspection (RFC 6184). STAP-B, MTAP and FU-B (interleaved mode) are rejected.</summary>
public static class H264
{
    public const int NalSlice = 1;
    public const int NalIdr = 5;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int StapA = 24;
    public const int FuA = 28;

    /// <summary>Upper bound on NAL units aggregated in one STAP-A.</summary>
    public const int MaxAggregated = 256;

    /// <summary>RFC 6184 §5.6 (single NAL), §5.7.1 (STAP-A), §5.8 (FU-A).</summary>
    public static H264PayloadInfo Read(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw RtpParseException.Truncated("h264 payload");
        }

        if ((payload[0] & 0x80) != 0)
        {
            throw RtpParseException.Invalid("h264_forbidden_bit", "H.264 NAL header forbidden_zero_bit is set");
        }

        var type = payload[0] & 0x1F;
        switch (type)
        {
            case >= 1 and <= 23:
                return Single(type) with { PacketType = type, IsStartOfNal = true, IsEndOfNal = true, NalUnitCount = 1 };
            case StapA:
            {
                bool idr = false, sps = false, pps = false;
                var pos = 1;
                var n = 0;
                while (pos < payload.Length)
                {
                    if (pos + 2 > payload.Length)
                    {
                        throw RtpParseException.Truncated("h264 stap-a size");
                    }

                    var size = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
                    pos += 2;
                    if (size == 0 || pos + size > payload.Length)
                    {
                        throw RtpParseException.Invalid("h264_stap_a", "STAP-A NAL size is zero or runs past the payload");
                    }

                    if (++n > MaxAggregated)
                    {
                        throw RtpParseException.Invalid("h264_stap_a", "too many aggregated NAL units");
                    }

                    var t = payload[pos] & 0x1F;
                    idr |= t == NalIdr;
                    sps |= t == NalSps;
                    pps |= t == NalPps;
                    pos += size;
                }

                if (n == 0)
                {
                    throw RtpParseException.Invalid("h264_stap_a", "empty STAP-A");
                }

                return new H264PayloadInfo
                {
                    PacketType = type, HasIdr = idr, HasSps = sps, HasPps = pps, IsStartOfNal = true, IsEndOfNal = true, NalUnitCount = n,
                };
            }

            case FuA:
            {
                if (payload.Length < 2)
                {
                    throw RtpParseException.Truncated("h264 fu-a header");
                }

                var fu = payload[1];
                var start = (fu & 0x80) != 0;
                var end = (fu & 0x40) != 0;
                if (start && end)
                {
                    throw RtpParseException.Invalid("h264_fu_a", "FU-A with both start and end bits (RFC 6184 §5.8)");
                }

                return Single(fu & 0x1F) with { PacketType = type, IsStartOfNal = start, IsEndOfNal = end, NalUnitCount = start ? 1 : 0 };
            }

            default:
                throw RtpParseException.Invalid("h264_packetization", $"NAL type {type} (STAP-B/MTAP/FU-B or reserved) not supported");
        }
    }

    private static H264PayloadInfo Single(int nalType) =>
        new() { HasIdr = nalType == NalIdr, HasSps = nalType == NalSps, HasPps = nalType == NalPps };
}
