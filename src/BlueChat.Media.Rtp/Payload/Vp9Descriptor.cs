namespace BlueChat.Media.Rtp.Payload;

/// <summary>Fields of the VP9 payload descriptor (RFC 9628 §4.2). Absent numeric fields are −1.</summary>
public readonly record struct Vp9Descriptor
{
    public int Length { get; init; }
    public bool InterPicturePredicted { get; init; }
    public bool FlexibleMode { get; init; }
    public bool StartOfFrame { get; init; }
    public bool EndOfFrame { get; init; }
    public bool HasScalabilityStructure { get; init; }
    public bool NotReferenceForUpperSpatial { get; init; }
    public int PictureId { get; init; }
    public int PictureIdBits { get; init; }
    public int TemporalLayer { get; init; }
    public bool SwitchingUpPoint { get; init; }
    public int SpatialLayer { get; init; }
    public bool InterLayerDependency { get; init; }
    public int Tl0PicIdx { get; init; }

    /// <summary>Number of P_DIFF references (flexible mode, 0–3).</summary>
    public int ReferenceCount { get; init; }

    /// <summary>N_S + 1 when SS is present, else 0.</summary>
    public int SpatialLayerCount { get; init; }

    /// <summary>Width/height of spatial layer 0 from SS when Y=1, else 0.</summary>
    public int Width0 { get; init; }

    public int Height0 { get; init; }

    /// <summary>N_G of SS (pictures in the group), −1 when G=0 or no SS.</summary>
    public int PictureGroupSize { get; init; }
}

/// <summary>VP9 payload descriptor reader (RFC 9628).</summary>
public static class Vp9
{
    /// <summary>Maximum P_DIFF entries (RFC 9628 §4.2: "up to 3 reference indices").</summary>
    private const int MaxReferences = 3;

    /// <summary>RFC 9628 §4.2 descriptor and §4.2.1 scalability structure.</summary>
    public static Vp9Descriptor ReadDescriptor(ReadOnlySpan<byte> payload)
    {
        var pos = 0;
        var b0 = Byte(payload, pos++);
        bool i = (b0 & 0x80) != 0, p = (b0 & 0x40) != 0, l = (b0 & 0x20) != 0, f = (b0 & 0x10) != 0;
        bool b = (b0 & 0x08) != 0, e = (b0 & 0x04) != 0, v = (b0 & 0x02) != 0, z = (b0 & 0x01) != 0;
        int pictureId = -1, bits = 0, tid = -1, sid = -1, tl0 = -1, refs = 0;
        bool u = false, d = false;
        if (i)
        {
            var m = Byte(payload, pos++);
            if ((m & 0x80) != 0)
            {
                pictureId = (m & 0x7F) << 8 | Byte(payload, pos++);
                bits = 15;
            }
            else
            {
                pictureId = m;
                bits = 7;
            }
        }

        if (l)
        {
            var li = Byte(payload, pos++);
            tid = li >> 5;
            u = (li & 0x10) != 0;
            sid = (li >> 1) & 0x07;
            d = (li & 0x01) != 0;
            if (!f)
            {
                tl0 = Byte(payload, pos++); // non-flexible mode carries TL0PICIDX
            }
        }

        if (f && p)
        {
            // P_DIFF(7) | N(1), repeated while N=1.
            while (true)
            {
                var pd = Byte(payload, pos++);
                if (++refs > MaxReferences)
                {
                    throw RtpParseException.Invalid("vp9_descriptor", "more than 3 P_DIFF references");
                }

                if ((pd & 0x01) == 0)
                {
                    break;
                }
            }
        }

        int layers = 0, width = 0, height = 0, groupSize = -1;
        if (v)
        {
            // §4.2.1: N_S(3) Y(1) G(1) RSV(3) [WIDTH HEIGHT]×(N_S+1) [N_G (TID U R RSV P_DIFF×R)×N_G]
            var ss = Byte(payload, pos++);
            layers = (ss >> 5) + 1;
            var y = (ss & 0x10) != 0;
            var g = (ss & 0x08) != 0;
            if (y)
            {
                for (var k = 0; k < layers; k++)
                {
                    var w = Byte(payload, pos) << 8 | Byte(payload, pos + 1);
                    var h = Byte(payload, pos + 2) << 8 | Byte(payload, pos + 3);
                    pos += 4;
                    if (k == 0)
                    {
                        width = w;
                        height = h;
                    }
                }
            }

            if (g)
            {
                groupSize = Byte(payload, pos++);
                for (var k = 0; k < groupSize; k++)
                {
                    var desc = Byte(payload, pos++);
                    var r = (desc >> 2) & 0x03;
                    pos += r;
                    if (pos > payload.Length)
                    {
                        throw RtpParseException.Truncated("vp9 scalability structure");
                    }
                }
            }
        }

        return new Vp9Descriptor
        {
            Length = pos, InterPicturePredicted = p, FlexibleMode = f, StartOfFrame = b, EndOfFrame = e,
            HasScalabilityStructure = v, NotReferenceForUpperSpatial = z, PictureId = pictureId, PictureIdBits = bits,
            TemporalLayer = tid, SwitchingUpPoint = u, SpatialLayer = sid, InterLayerDependency = d, Tl0PicIdx = tl0,
            ReferenceCount = refs, SpatialLayerCount = layers, Width0 = width, Height0 = height, PictureGroupSize = groupSize,
        };
    }

    /// <summary>
    /// Key frame = first packet of a picture that is not inter-predicted in the base spatial layer
    /// (RFC 9628 §4.2: P=0, B=1, SID 0 or no layer indices).
    /// </summary>
    public static VideoFrameInfo ReadFrameInfo(ReadOnlySpan<byte> payload)
    {
        var d = ReadDescriptor(payload);
        return new VideoFrameInfo
        {
            DescriptorLength = d.Length,
            IsKeyFrame = !d.InterPicturePredicted && d.StartOfFrame && d.SpatialLayer <= 0,
            IsStartOfFrame = d.StartOfFrame, IsEndOfFrame = d.EndOfFrame, PictureId = d.PictureId, Tl0PicIdx = d.Tl0PicIdx,
            TemporalLayer = d.TemporalLayer, SpatialLayer = d.SpatialLayer, LayerSync = d.SwitchingUpPoint,
        };
    }

    private static byte Byte(ReadOnlySpan<byte> p, int i) =>
        i < p.Length ? p[i] : throw RtpParseException.Truncated("vp9 descriptor");
}
