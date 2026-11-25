namespace BlueChat.Media.Rtp.Payload;

/// <summary>Fields of the VP8 payload descriptor (RFC 7741 §4.2).</summary>
public readonly record struct Vp8Descriptor
{
    public int Length { get; init; }
    public bool NonReference { get; init; }
    public bool StartOfPartition { get; init; }
    public int PartitionId { get; init; }

    /// <summary>−1 when absent.</summary>
    public int PictureId { get; init; }

    /// <summary>7 or 15 (M bit), 0 when absent.</summary>
    public int PictureIdBits { get; init; }

    public int Tl0PicIdx { get; init; }
    public int TemporalLayer { get; init; }
    public bool LayerSync { get; init; }
    public int KeyIndex { get; init; }

    /// <summary>S=1, PID=0 and the VP8 payload header P bit is 0 (RFC 7741 §4.3, RFC 6386 §9.1).</summary>
    public bool IsKeyFrame { get; init; }
}

/// <summary>VP8 payload descriptor reader and in-place rewriter (RFC 7741).</summary>
public static class Vp8
{
    /// <summary>RFC 7741 §4.2: X R N S R PID | I L T K RSV | M PictureID | TL0PICIDX | TID Y KEYIDX.</summary>
    public static Vp8Descriptor ReadDescriptor(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw RtpParseException.Truncated("vp8 descriptor");
        }

        var b0 = payload[0];
        var pos = 1;
        int pictureId = -1, bits = 0, tl0 = -1, tid = -1, keyIdx = -1;
        var sync = false;
        if ((b0 & 0x80) != 0)
        {
            var x = Byte(payload, pos++);
            var i = (x & 0x80) != 0;
            var l = (x & 0x40) != 0;
            var t = (x & 0x20) != 0;
            var k = (x & 0x10) != 0;
            if (i)
            {
                var p = Byte(payload, pos++);
                if ((p & 0x80) != 0)
                {
                    pictureId = (p & 0x7F) << 8 | Byte(payload, pos++);
                    bits = 15;
                }
                else
                {
                    pictureId = p;
                    bits = 7;
                }
            }

            if (l)
            {
                tl0 = Byte(payload, pos++);
            }

            if (t || k)
            {
                var tk = Byte(payload, pos++);
                if (t)
                {
                    tid = tk >> 6;
                    sync = (tk & 0x20) != 0;
                }

                if (k)
                {
                    keyIdx = tk & 0x1F;
                }
            }
        }

        var start = (b0 & 0x10) != 0;
        var pid = b0 & 0x07;
        // RFC 7741 §4.3: at the start of partition 0 the VP8 payload header follows; its first bit P is 0 for key frames.
        var key = start && pid == 0 && payload.Length > pos && (payload[pos] & 0x01) == 0;
        return new Vp8Descriptor
        {
            Length = pos, NonReference = (b0 & 0x20) != 0, StartOfPartition = start, PartitionId = pid,
            PictureId = pictureId, PictureIdBits = bits, Tl0PicIdx = tl0, TemporalLayer = tid, LayerSync = sync,
            KeyIndex = keyIdx, IsKeyFrame = key,
        };
    }

    public static VideoFrameInfo ReadFrameInfo(ReadOnlySpan<byte> payload)
    {
        var d = ReadDescriptor(payload);
        return new VideoFrameInfo
        {
            DescriptorLength = d.Length, IsKeyFrame = d.IsKeyFrame, IsStartOfFrame = d.StartOfPartition && d.PartitionId == 0,
            PictureId = d.PictureId, Tl0PicIdx = d.Tl0PicIdx, TemporalLayer = d.TemporalLayer, SpatialLayer = -1,
            LayerSync = d.LayerSync,
        };
    }

    /// <summary>
    /// Rewrites PictureID in place keeping the field width (RFC 7741 §4.2: 7 or 15 bits; the value is masked).
    /// Returns false when the packet has no PictureID.
    /// </summary>
    public static bool RewritePictureId(Span<byte> payload, int pictureId)
    {
        if (payload.Length < 3 || (payload[0] & 0x80) == 0 || (payload[1] & 0x80) == 0)
        {
            return false;
        }

        if ((payload[2] & 0x80) != 0)
        {
            if (payload.Length < 4)
            {
                return false;
            }

            payload[2] = (byte)(0x80 | ((pictureId >> 8) & 0x7F));
            payload[3] = (byte)pictureId;
        }
        else
        {
            payload[2] = (byte)(pictureId & 0x7F);
        }

        return true;
    }

    /// <summary>Rewrites TL0PICIDX in place; false when the packet has none.</summary>
    public static bool RewriteTl0PicIdx(Span<byte> payload, byte tl0PicIdx)
    {
        var d = ReadDescriptor(payload);
        if (d.Tl0PicIdx < 0)
        {
            return false;
        }

        var pos = 2 + (d.PictureIdBits / 8 + (d.PictureIdBits > 0 ? 1 : 0));
        payload[pos] = tl0PicIdx;
        return true;
    }

    private static byte Byte(ReadOnlySpan<byte> p, int i) =>
        i < p.Length ? p[i] : throw RtpParseException.Truncated("vp8 descriptor");
}
