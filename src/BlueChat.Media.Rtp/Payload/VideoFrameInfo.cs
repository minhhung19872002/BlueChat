namespace BlueChat.Media.Rtp.Payload;

/// <summary>
/// What the SFU needs to know about one video RTP packet without decoding it. Layers and ids are −1 when the
/// descriptor does not carry them. <see cref="IsEndOfFrame"/> is only set when the payload format says so
/// (VP9 E bit, FU-A E bit); otherwise use the RTP marker bit.
/// </summary>
public readonly record struct VideoFrameInfo
{
    public int DescriptorLength { get; init; }
    public bool IsKeyFrame { get; init; }
    public bool IsStartOfFrame { get; init; }
    public bool IsEndOfFrame { get; init; }
    public int PictureId { get; init; }
    public int Tl0PicIdx { get; init; }
    public int TemporalLayer { get; init; }
    public int SpatialLayer { get; init; }

    /// <summary>Switching up to this temporal layer is possible from this packet (VP8 Y, VP9 U).</summary>
    public bool LayerSync { get; init; }

    internal static VideoFrameInfo Empty => new() { PictureId = -1, Tl0PicIdx = -1, TemporalLayer = -1, SpatialLayer = -1 };
}
