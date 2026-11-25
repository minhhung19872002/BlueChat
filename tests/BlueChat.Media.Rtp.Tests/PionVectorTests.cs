using BlueChat.Media.Rtp.Payload;
using BlueChat.Media.Rtp.Rtcp;

namespace BlueChat.Media.Rtp.Tests;

/// <summary>Packets built by Pion (independent implementation) must read exactly as Pion reads them.</summary>
public class PionVectorTests
{
    public static IEnumerable<object[]> Rtp => Vectors.OfKind("rtp");
    public static IEnumerable<object[]> Vp8V => Vectors.OfKind("vp8");
    public static IEnumerable<object[]> Vp9V => Vectors.OfKind("vp9");
    public static IEnumerable<object[]> H264V => Vectors.OfKind("h264");
    public static IEnumerable<object[]> Av1V => Vectors.OfKind("av1");
    public static IEnumerable<object[]> RtcpV => Vectors.OfKind("rtcp");
    public static IEnumerable<object[]> TwccV => Vectors.OfKind("twcc");

    [Fact]
    public void Vector_file_covers_every_kind()
    {
        Vectors.Pion.Select(v => v.Kind).Distinct().Should().BeEquivalentTo(
            ["rtp", "vp8", "vp9", "h264", "av1", "rtcp", "rtcp-compound", "twcc"]);
    }

    [Theory]
    [MemberData(nameof(Rtp))]
    public void Rtp_header_and_extensions_match_Pion(string name)
    {
        var v = Vectors.Get("rtp", name);
        var e = v.Expect;
        var h = RtpHeader.Parse(v.Bytes);
        h.Marker.Should().Be(e.Bool("marker"));
        h.PayloadType.Should().Be((byte)e.Int("pt"));
        h.SequenceNumber.Should().Be((ushort)e.Int("seq"));
        h.Timestamp.Should().Be((uint)e.Long("ts"));
        h.Ssrc.Should().Be((uint)e.Long("ssrc"));
        h.Extension.Should().Be(e.Bool("extension"));
        h.ExtensionProfile.Should().Be((ushort)e.Int("profile"));
        Enumerable.Range(0, h.CsrcCount).Select(i => (long)h.GetCsrc(v.Bytes, i)).Should().Equal(e.Longs("csrc"));
        Convert.ToHexStringLower(h.Payload(v.Bytes)).Should().Be(e.Str("payload"));
        h.PaddingLength.Should().Be(e.Int("padding"));

        var got = new Dictionary<string, string>();
        foreach (var x in new HeaderExtensionReader(v.Bytes, h))
        {
            got[x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Convert.ToHexStringLower(v.Bytes.AsSpan(x.Offset, x.Length));
        }

        got.Should().BeEquivalentTo(e.GetProperty("extensions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
    }

    [Fact]
    public void Typed_extensions_read_Pion_values()
    {
        var v = Vectors.Get("rtp", "one-byte-ext");
        var h = RtpHeader.Parse(v.Bytes);
        HeaderExtensionReader.TryFind(v.Bytes, h, 1, out var mid).Should().BeTrue();
        RtpExtensions.ReadStreamId(v.Bytes.AsSpan(mid.Offset, mid.Length), isMid: true).Should().Be("0");
        HeaderExtensionReader.TryFind(v.Bytes, h, 2, out var abs).Should().BeTrue();
        RtpExtensions.ReadAbsSendTime(v.Bytes.AsSpan(abs.Offset, abs.Length)).Should().Be(0x007E6B);
        HeaderExtensionReader.TryFind(v.Bytes, h, 3, out var lvl).Should().BeTrue();
        RtpExtensions.ReadAudioLevel(v.Bytes.AsSpan(lvl.Offset, lvl.Length)).Should().Be((true, (byte)42));
        HeaderExtensionReader.TryFind(v.Bytes, h, 5, out var tcc).Should().BeTrue();
        RtpExtensions.ReadTransportSequenceNumber(v.Bytes.AsSpan(tcc.Offset, tcc.Length)).Should().Be(0xBEEF);

        // Same instant as the generator (Unix 1700000000.123456789 s); Pion converts to NTP time (epoch 1900) first.
        const long NtpEpochOffsetSeconds = 2_208_988_800;
        RtpExtensions.AbsSendTimeFromMicroseconds((1_700_000_000 + NtpEpochOffsetSeconds) * 1_000_000 + 123_456).Should().Be(0x007E6B);
    }

    [Theory]
    [MemberData(nameof(Vp8V))]
    public void Vp8_descriptor_matches_Pion(string name)
    {
        var v = Vectors.Get("vp8", name);
        var e = v.Expect;
        var d = Vp8.ReadDescriptor(v.Bytes);
        d.Length.Should().Be(e.Int("length"));
        d.NonReference.Should().Be(e.Bool("n"));
        d.StartOfPartition.Should().Be(e.Bool("s"));
        d.PartitionId.Should().Be(e.Int("pid"));
        d.PictureId.Should().Be(e.Int("pictureId"));
        d.PictureIdBits.Should().Be(e.Int("pictureIdBits"));
        d.Tl0PicIdx.Should().Be(e.Int("tl0"));
        d.TemporalLayer.Should().Be(e.Int("tid"));
        d.LayerSync.Should().Be(e.Bool("y"));
        d.KeyIndex.Should().Be(e.Int("keyIdx"));
        d.IsKeyFrame.Should().Be(e.Bool("isKey"));
    }

    [Theory]
    [MemberData(nameof(Vp9V))]
    public void Vp9_descriptor_matches_Pion(string name)
    {
        var v = Vectors.Get("vp9", name);
        var e = v.Expect;
        var d = Vp9.ReadDescriptor(v.Bytes);
        d.Length.Should().Be(e.Int("length"));
        d.InterPicturePredicted.Should().Be(e.Bool("p"));
        d.FlexibleMode.Should().Be(e.Bool("f"));
        d.StartOfFrame.Should().Be(e.Bool("b"));
        d.EndOfFrame.Should().Be(e.Bool("e"));
        d.HasScalabilityStructure.Should().Be(e.Bool("v"));
        d.NotReferenceForUpperSpatial.Should().Be(e.Bool("z"));
        d.PictureId.Should().Be(e.Int("pictureId"));
        d.PictureIdBits.Should().Be(e.Int("pictureIdBits"));
        d.TemporalLayer.Should().Be(e.Int("tid"));
        d.SwitchingUpPoint.Should().Be(e.Bool("u"));
        d.SpatialLayer.Should().Be(e.Int("sid"));
        d.InterLayerDependency.Should().Be(e.Bool("d"));
        d.Tl0PicIdx.Should().Be(e.Int("tl0"));
        d.ReferenceCount.Should().Be(e.Int("refs"));
        d.SpatialLayerCount.Should().Be(e.Int("layers"));
        d.Width0.Should().Be(e.Int("width0"));
        d.Height0.Should().Be(e.Int("height0"));
        d.PictureGroupSize.Should().Be(e.Int("ng"));
    }

    [Theory]
    [MemberData(nameof(H264V))]
    public void H264_packets_from_Pion_payloader(string name)
    {
        var v = Vectors.Get("h264", name);
        var frame = v.Expect.Str("frame");
        var index = v.Expect.Int("index");
        var last = v.Expect.Int("count") - 1;
        var info = H264.Read(v.Bytes);
        switch (frame)
        {
            case "key" when index == 0:
                // Pion aggregates the cached SPS and PPS into one STAP-A before the IDR.
                info.PacketType.Should().Be(H264.StapA);
                info.HasSps.Should().BeTrue();
                info.HasPps.Should().BeTrue();
                info.NalUnitCount.Should().Be(2);
                info.IsKeyFrame.Should().BeTrue();
                break;
            case "key":
                info.PacketType.Should().Be(H264.FuA);
                info.HasIdr.Should().BeTrue();
                info.IsStartOfNal.Should().Be(index == 1);
                info.IsEndOfNal.Should().Be(index == last);
                break;
            case "inter":
                info.PacketType.Should().Be(H264.FuA);
                info.IsKeyFrame.Should().BeFalse();
                info.IsStartOfNal.Should().Be(index == 0);
                info.IsEndOfNal.Should().Be(index == last);
                break;
            default:
                info.PacketType.Should().Be(H264.NalSlice);
                info.IsKeyFrame.Should().BeFalse();
                info.IsStartOfNal.Should().BeTrue();
                info.IsEndOfNal.Should().BeTrue();
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Av1V))]
    public void Av1_aggregation_header_matches_Pion(string name)
    {
        var v = Vectors.Get("av1", name);
        var e = v.Expect;
        var info = Av1.Read(v.Bytes);
        info.ContinuesPrevious.Should().Be(e.Bool("z"));
        info.ContinuesNext.Should().Be(e.Bool("y"));
        info.ElementCountField.Should().Be(e.Int("w"));
        info.NewCodedVideoSequence.Should().Be(e.Bool("n"));
        info.ObuElementCount.Should().Be(e.Int("elements"));
        info.IsKeyFrame.Should().Be(e.Bool("n"));
        if (name == "payloader-key-0")
        {
            info.HasSequenceHeader.Should().BeTrue();
            info.FirstObuType.Should().Be(Av1.ObuSequenceHeader);
        }
    }

    [Theory]
    [MemberData(nameof(RtcpV))]
    public void Rtcp_packets_match_Pion(string name)
    {
        var v = Vectors.Get("rtcp", name);
        var e = v.Expect;
        RtcpCompoundReader.Validate(v.Bytes, allowReducedSize: true).Should().Be(1);
        ReadOnlySpan<byte> p = v.Bytes;
        switch (name)
        {
            case "sr":
            {
                var sr = SenderReport.Read(p);
                sr.Ssrc.Should().Be((uint)e.Long("ssrc"));
                sr.NtpTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(e.Str("ntp"));
                sr.RtpTimestamp.Should().Be((uint)e.Long("rtp"));
                sr.PacketCount.Should().Be((uint)e.Long("packets"));
                sr.OctetCount.Should().Be((uint)e.Long("octets"));
                sr.ReportCount.Should().Be(1);
                CheckBlock(SenderReport.GetReportBlock(p, 0), e.GetProperty("block"));
                break;
            }

            case "rr":
            {
                var rr = ReceiverReport.Read(p);
                rr.Ssrc.Should().Be((uint)e.Long("ssrc"));
                CheckBlock(ReceiverReport.GetReportBlock(p, 0), e.GetProperty("block"));
                break;
            }

            case "sdes":
            {
                var items = new List<(uint, byte, string)>();
                foreach (var item in new SdesReader(p))
                {
                    items.Add((item.Ssrc, item.Type, System.Text.Encoding.UTF8.GetString(p.Slice(item.Offset, item.Length))));
                }

                items.Should().Equal([((uint)e.Long("ssrc"), SdesReader.Cname, e.Str("cname"))]);
                break;
            }

            case "bye":
                Enumerable.Range(0, Goodbye.SourceCount(p)).Select(i => (long)Goodbye.GetSource(v.Bytes, i)).Should().Equal(e.Longs("sources"));
                System.Text.Encoding.UTF8.GetString(Goodbye.Reason(p)).Should().Be(e.Str("reason"));
                break;
            case "nack":
            {
                var f = FeedbackHeader.Read(p);
                f.IsNack.Should().BeTrue();
                f.SenderSsrc.Should().Be((uint)e.Long("sender"));
                f.MediaSsrc.Should().Be((uint)e.Long("media"));
                var lost = new ushort[64];
                var n = Nack.ReadLost(f.Fci(p), lost);
                lost[..n].Select(x => (long)x).Should().Equal(e.Longs("lost"));
                break;
            }

            case "pli":
            {
                var f = FeedbackHeader.Read(p);
                f.IsPli.Should().BeTrue();
                f.MediaSsrc.Should().Be((uint)e.Long("media"));
                f.FciLength.Should().Be(0);
                break;
            }

            case "fir":
            {
                var f = FeedbackHeader.Read(p);
                f.IsFir.Should().BeTrue();
                var fci = f.Fci(p);
                var entries = Enumerable.Range(0, Fir.Count(fci)).Select(i => Fir.Get(v.Bytes.AsSpan(f.FciOffset, f.FciLength), i))
                    .Select(x => new long[] { x.Ssrc, x.SequenceNumber }).ToArray();
                entries.Should().BeEquivalentTo(e.GetProperty("entries").EnumerateArray()
                    .Select(a => a.EnumerateArray().Select(x => x.GetInt64()).ToArray()).ToArray(), o => o.WithStrictOrdering());
                break;
            }

            case "remb":
            {
                var f = FeedbackHeader.Read(p);
                f.IsApplication.Should().BeTrue();
                var fci = f.Fci(p);
                Remb.IsRemb(fci).Should().BeTrue();
                var (bitrate, count) = Remb.Read(fci);
                bitrate.Should().Be((ulong)e.Long("bitrate"));
                Enumerable.Range(0, count).Select(i => (long)Remb.GetSsrc(v.Bytes.AsSpan(f.FciOffset, f.FciLength), i)).Should().Equal(e.Longs("ssrcs"));
                break;
            }

            default:
                throw new InvalidOperationException("unexpected vector " + name);
        }
    }

    private static void CheckBlock(ReportBlock b, System.Text.Json.JsonElement e)
    {
        b.Ssrc.Should().Be((uint)e.Long("ssrc"));
        b.FractionLost.Should().Be((byte)e.Int("fraction"));
        b.CumulativeLost.Should().Be(e.Int("lost"));
        b.ExtendedHighestSequence.Should().Be((uint)e.Long("highest"));
        b.Jitter.Should().Be((uint)e.Long("jitter"));
        b.LastSenderReport.Should().Be((uint)e.Long("lsr"));
        b.DelaySinceLastSenderReport.Should().Be((uint)e.Long("dlsr"));
    }

    [Fact]
    public void Compound_from_Pion_iterates_in_order()
    {
        var v = Vectors.Get("rtcp-compound", "rr-sdes-nack-pli");
        RtcpCompoundReader.Validate(v.Bytes, allowReducedSize: false).Should().Be(4);
        var types = new List<long>();
        foreach (var p in new RtcpCompoundReader(v.Bytes))
        {
            types.Add(p.PacketType);
        }

        types.Should().Equal(v.Expect.Longs("types"));
    }

    [Theory]
    [MemberData(nameof(TwccV))]
    public void Transport_cc_feedback_matches_Pion(string name)
    {
        var v = Vectors.Get("twcc", name);
        var e = v.Expect;
        RtcpCompoundReader.Validate(v.Bytes, allowReducedSize: true).Should().Be(1);
        var f = FeedbackHeader.Read(v.Bytes);
        f.IsTransportCc.Should().BeTrue();
        f.SenderSsrc.Should().Be((uint)e.Long("sender"));
        f.MediaSsrc.Should().Be((uint)e.Long("media"));
        var statuses = new TwccPacketStatus[1024];
        var fb = TransportCc.Read(f.Fci(v.Bytes), statuses);
        fb.BaseSequence.Should().Be((ushort)e.Int("base"));
        fb.StatusCount.Should().Be(e.Int("count"));
        fb.ReferenceTime.Should().Be((uint)e.Long("refTime"));
        fb.FeedbackPacketCount.Should().Be((byte)e.Int("fbCount"));
        var got = statuses[..fb.StatusCount];
        got.Select(s => (long)s.Symbol).Should().Equal(e.Longs("symbols"));
        got.Select(s => (long)s.DeltaTicks).Should().Equal(e.Longs("deltas"));
        got.Select(s => s.Sequence).Should().Equal(Enumerable.Range(0, fb.StatusCount).Select(i => (ushort)(fb.BaseSequence + i)));
    }
}
