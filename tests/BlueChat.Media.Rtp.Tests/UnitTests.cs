using BlueChat.Media.Rtp.Payload;
using BlueChat.Media.Rtp.Rtcp;

namespace BlueChat.Media.Rtp.Tests;

public class SequenceNumberTests
{
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 65535, true)]
    [InlineData(65535, 0, false)]
    [InlineData(5, 5, false)]
    [InlineData(32767, 0, true)]
    [InlineData(32768, 0, false)]
    public void IsNewer_handles_wraparound(int a, int b, bool expected) =>
        SequenceNumbers.IsNewer((ushort)a, (ushort)b).Should().Be(expected);

    [Fact]
    public void Unwrapper_extends_across_wraps_and_keeps_late_packets_in_the_old_cycle()
    {
        var u = new SequenceUnwrapper();
        u.Unwrap(65534).Should().Be(65534);
        u.Unwrap(65535).Should().Be(65535);
        u.Unwrap(0).Should().Be(65536);
        u.Unwrap(65533).Should().Be(65533); // late, previous cycle
        u.Unwrap(5).Should().Be(65541);
        for (var i = 0; i < 3 * 65536; i += 1000)
        {
            u.Unwrap((ushort)(65541 + i)).Should().Be(65541 + i);
        }
    }
}

public class RtpHeaderTests
{
    [Fact]
    public void Rejects_wrong_version_truncation_and_bad_padding()
    {
        Code(() => RtpHeader.Parse(new byte[11])).Should().Be("truncated");
        Code(() => RtpHeader.Parse([0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])).Should().Be("rtp_version");
        Code(() => RtpHeader.Parse([0x81, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])).Should().Be("truncated"); // CSRC missing
        Code(() => RtpHeader.Parse([0x90, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xBE, 0xDE, 0, 2, 0x10])).Should().Be("truncated");
        Code(() => RtpHeader.Parse([0xA0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0])).Should().Be("rtp_padding"); // pad count 0
        Code(() => RtpHeader.Parse([0xA0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 9])).Should().Be("rtp_padding"); // pad > payload
    }

    [Fact]
    public void One_byte_extensions_skip_padding_and_stop_at_id_15()
    {
        // id1 len1 "AB", pad 0x00, id15 → stop, then garbage that must not be read
        byte[] p = [0x90, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0xBE, 0xDE, 0, 2, 0x11, 0xAA, 0xBB, 0x00, 0xF0, 0x55, 0x55, 0x55, 9];
        var h = RtpHeader.Parse(p);
        var ids = new List<(byte, int)>();
        foreach (var e in new HeaderExtensionReader(p, h))
        {
            ids.Add((e.Id, e.Length));
        }

        ids.Should().Equal([(1, 2)]);
        h.Payload(p).ToArray().Should().Equal([9]);
    }

    [Fact]
    public void Extension_running_past_the_block_is_reported()
    {
        byte[] p = [0x90, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0xBE, 0xDE, 0, 1, 0x1F, 0, 0, 0];
        var h = RtpHeader.Parse(p);
        Code(() =>
        {
            foreach (var _ in new HeaderExtensionReader(p, h))
            {
            }
        }).Should().Be("rtp_extension");
    }

    [Fact]
    public void In_place_rewrites_change_only_their_field()
    {
        var buf = new byte[64];
        var w = new RtpPacketWriter(buf, new RtpHeaderFields(false, 96, 1, 2, 3));
        var tcc = new byte[2];
        w.AddExtension(3, tcc);
        w.WritePayload([1, 2, 3]);
        var p = buf.AsSpan(0, w.Finish());
        RtpHeader.WriteSequenceNumber(p, 500);
        RtpHeader.WriteTimestamp(p, 123456);
        RtpHeader.WriteSsrc(p, 0xFEEDFACE);
        RtpHeader.WriteMarker(p, true);
        RtpHeader.WritePayloadType(p, 100);
        var h = RtpHeader.Parse(p);
        HeaderExtensionReader.TryFind(p, h, 3, out var e).Should().BeTrue();
        RtpExtensions.WriteTransportSequenceNumber(p.Slice(e.Offset, e.Length), 4242);
        h = RtpHeader.Parse(p);
        (h.SequenceNumber, h.Timestamp, h.Ssrc, h.Marker, h.PayloadType).Should().Be(((ushort)500, 123456u, 0xFEEDFACEu, true, (byte)100));
        RtpExtensions.ReadTransportSequenceNumber(p.Slice(e.Offset, e.Length)).Should().Be(4242);
        h.Payload(p).ToArray().Should().Equal([1, 2, 3]);
    }

    [Fact]
    public void Writer_refuses_ids_and_lengths_outside_RFC_8285()
    {
        var buf = new byte[64];
        var act = () =>
        {
            var w = new RtpPacketWriter(buf, default);
            w.AddExtension(15, [1]);
        };
        act.Should().Throw<ArgumentException>();
        var act2 = () =>
        {
            var w = new RtpPacketWriter(buf, default);
            w.AddExtension(1, new byte[17]);
        };
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dependency_descriptor_mandatory_fields_round_trip()
    {
        var b = new byte[3];
        var dd = new DependencyDescriptorMandatory(true, true, 63, 65535);
        RtpExtensions.WriteDependencyDescriptor(b, dd);
        RtpExtensions.ReadDependencyDescriptor(b).Should().Be(dd);
        RtpExtensions.ReadDependencyDescriptor([0x40, 0, 7, 0xFF]).Should().Be(new DependencyDescriptorMandatory(false, true, 0, 7, true));
    }

    [Fact]
    public void Stream_ids_follow_RFC_8851()
    {
        RtpExtensions.ReadStreamId("hi-res_1"u8).Should().Be("hi-res_1");
        Code(() => RtpExtensions.ReadStreamId("a b"u8)).Should().Be("rtp_stream_id");
        RtpExtensions.IsValidStreamId("audio.0"u8, isMid: true).Should().BeTrue();
        RtpExtensions.IsValidStreamId("audio.0"u8, isMid: false).Should().BeFalse();
    }

    internal static string? Code(Action a)
    {
        try
        {
            a();
            return null;
        }
        catch (RtpParseException e)
        {
            return e.Code;
        }
    }
}

public class RtcpTests
{
    [Fact]
    public void Compound_rules_are_enforced()
    {
        var buf = new byte[256];
        var pli = Pli.Write(buf, 1, 2);
        RtpHeaderTests.Code(() => RtcpCompoundReader.Validate(buf.AsSpan(0, pli), allowReducedSize: false)).Should().Be("rtcp_compound_first");
        RtcpCompoundReader.Validate(buf.AsSpan(0, pli), allowReducedSize: true).Should().Be(1); // RFC 5506

        // Length field past the datagram.
        RtpHeaderTests.Code(() => RtcpCompoundReader.Validate(buf.AsSpan(0, pli - 4), true)).Should().Be("rtcp_length");

        // Padding on a packet that is not last.
        var a = ReceiverReport.Write(buf, 1, []);
        buf[0] |= 0x20;
        buf[a - 1] = 4;
        var b = Pli.Write(buf.AsSpan(a), 1, 2);
        RtpHeaderTests.Code(() => RtcpCompoundReader.Validate(buf.AsSpan(0, a + b), false)).Should().Be("rtcp_padding");
    }

    [Fact]
    public void Report_block_negative_cumulative_loss_survives()
    {
        var b = new byte[24];
        new ReportBlock(1, 2, -8388608, 3, 4, 5, 6).Write(b);
        ReportBlock.Read(b).CumulativeLost.Should().Be(-8388608);
    }

    [Fact]
    public void Nack_over_a_wrap_compresses_into_one_pair()
    {
        var buf = new byte[64];
        var n = Nack.Write(buf, 1, 2, [65535, 0, 15]);
        n.Should().Be(16); // header 12 + one pair
        var lost = new ushort[17];
        Nack.ReadLost(buf.AsSpan(12, 4), lost).Should().Be(3);
        lost[..3].Should().Equal(65535, 0, 15);
    }

    [Fact]
    public void Nack_reader_refuses_to_overflow_the_caller_buffer()
    {
        byte[] fci = [0, 1, 0xFF, 0xFF];
        RtpHeaderTests.Code(() => Nack.ReadLost(fci, new ushort[5])).Should().Be("rtcp_nack");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(262143UL)]
    [InlineData(262144UL)]
    [InlineData(1_000_000_000UL)]
    public void Remb_keeps_18_bit_mantissa_precision(ulong bitrate)
    {
        var buf = new byte[64];
        var n = Remb.Write(buf, 1, bitrate, [9]);
        var (got, count) = Remb.Read(buf.AsSpan(12, n - 12));
        count.Should().Be(1);
        got.Should().BeLessThanOrEqualTo(bitrate);
        ((double)(bitrate - got)).Should().BeLessThanOrEqualTo(bitrate / 262144.0 + 1);
    }

    [Fact]
    public void Twcc_rejects_reserved_symbol_and_short_deltas()
    {
        // base 0, count 2, ref 0, fb 0, run-length chunk symbol 3
        byte[] fci = [0, 0, 0, 2, 0, 0, 0, 0, 0x60, 0x02, 0, 0];
        RtpHeaderTests.Code(() => TransportCc.Read(fci, new TwccPacketStatus[4])).Should().Be("rtcp_twcc");
        byte[] cut = [0, 0, 0, 2, 0, 0, 0, 0, 0x20, 0x02, 5];
        RtpHeaderTests.Code(() => TransportCc.Read(cut, new TwccPacketStatus[4])).Should().Be("rtcp_twcc");
    }

    [Fact]
    public void Sdes_reads_several_chunks_and_items()
    {
        // chunk1: ssrc 1, CNAME "a", NAME "bb", end; chunk2: ssrc 2, CNAME "c", end
        byte[] p =
        [
            0x82, 202, 0, 5,
            0, 0, 0, 1, 1, 1, (byte)'a', 2, 2, (byte)'b', (byte)'b', 0,
            0, 0, 0, 2, 1, 1, (byte)'c', 0,
        ];
        RtcpCompoundReader.Validate(p, true).Should().Be(1);
        var items = new List<(uint, byte, int)>();
        foreach (var i in new SdesReader(p))
        {
            items.Add((i.Ssrc, i.Type, i.Length));
        }

        items.Should().Equal([(1u, (byte)1, 1), (1u, (byte)2, 2), (2u, (byte)1, 1)]);
    }
}

public class PayloadTests
{
    [Fact]
    public void Vp8_rewrites_picture_id_and_tl0_in_place()
    {
        byte[] p = [0x90, 0xE0, 0x81, 0x23, 0x45, 0x40, 0x00];
        Vp8.RewritePictureId(p, 0x7FFF).Should().BeTrue();
        Vp8.RewriteTl0PicIdx(p, 0x99).Should().BeTrue();
        var d = Vp8.ReadDescriptor(p);
        (d.PictureId, d.Tl0PicIdx, d.TemporalLayer).Should().Be((0x7FFF, 0x99, 1));

        byte[] seven = [0x90, 0x80, 0x05];
        Vp8.RewritePictureId(seven, 300).Should().BeTrue();
        Vp8.ReadDescriptor(seven).PictureId.Should().Be(300 & 0x7F);
        Vp8.RewritePictureId([0x10, 0x00], 1).Should().BeFalse();
        Vp8.RewriteTl0PicIdx([0x10, 0x00], 1).Should().BeFalse();
    }

    [Fact]
    public void Vp8_and_vp9_truncation_is_loud()
    {
        RtpHeaderTests.Code(() => Vp8.ReadDescriptor([0x90, 0x80, 0x81])).Should().Be("truncated");
        RtpHeaderTests.Code(() => Vp9.ReadDescriptor([0xA0])).Should().Be("truncated");
        RtpHeaderTests.Code(() => Vp9.ReadDescriptor([0x50, 0x03, 0x03, 0x03, 0x02])).Should().Be("vp9_descriptor"); // 4 P_DIFF
    }

    [Fact]
    public void H264_rejects_unsupported_and_malformed_packets()
    {
        RtpHeaderTests.Code(() => H264.Read([0x99])).Should().Be("h264_forbidden_bit");
        RtpHeaderTests.Code(() => H264.Read([0x19, 0])).Should().Be("h264_packetization"); // STAP-B
        RtpHeaderTests.Code(() => H264.Read([0x1C, 0xC5, 1])).Should().Be("h264_fu_a"); // S and E
        RtpHeaderTests.Code(() => H264.Read([0x18, 0, 9, 0x67])).Should().Be("h264_stap_a");
        H264.Read([0x65, 1, 2]).IsKeyFrame.Should().BeTrue();
    }

    [Fact]
    public void Av1_checks_element_count_and_leb128()
    {
        RtpHeaderTests.Code(() => Av1.Read([0x88, 0x12, 0x00])).Should().Be("av1_aggregation"); // N with Z
        RtpHeaderTests.Code(() => Av1.Read([0x20, 0x02, 0x12, 0x00])).Should().Be("av1_aggregation"); // W=2 but the first element takes everything
        RtpHeaderTests.Code(() => Av1.Read([0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01])).Should().Be("av1_leb128");
        RtpHeaderTests.Code(() => Av1.Read([0x00, 0x09, 0x0A])).Should().Be("av1_obu_length");
        // Found by FuzzTests: a length ≥ 2^31 must not turn negative and move the cursor backwards.
        RtpHeaderTests.Code(() => Av1.Read([0x00, 0x80, 0x80, 0x80, 0x80, 0x0F, 0x0A])).Should().Be("av1_obu_length");
        var info = Av1.Read([0x18, 0x0E, 0x20, 0x22]); // W=1, N=1: one sequence header OBU with extension (tid 1, sid 0)
        (info.IsKeyFrame, info.FirstObuType, info.TemporalLayer, info.SpatialLayer).Should().Be((true, 1, 1, 0));
    }

    [Theory]
    [InlineData(0xF8, 1, 20_000, "celt")]   // config 31, code 0
    [InlineData(0x09, 2, 20_000, "silk")]   // config 1, code 1: two 20 ms frames
    [InlineData(0x78, 1, 20_000, "hybrid")] // config 15, code 0
    [InlineData(0x80, 1, 2_500, "celt")]    // config 16
    public void Opus_toc(byte toc, int frames, int frameDuration, string mode)
    {
        var t = OpusToc.Read([toc, 0x03]);
        t.FrameCount.Should().Be(frames);
        t.Mode.Should().Be(mode);
        t.FrameDurationMicroseconds.Should().Be(frameDuration);
    }

    [Fact]
    public void Opus_code_3_reads_frame_count_and_caps_duration()
    {
        OpusToc.Read([0xFB, 0x06]).PacketDurationMicroseconds.Should().Be(120_000); // 6 × 20 ms CELT
        RtpHeaderTests.Code(() => OpusToc.Read([0xFB, 0x07])).Should().Be("opus_duration");
        RtpHeaderTests.Code(() => OpusToc.Read([0xFB, 0x00])).Should().Be("opus_frame_count");
    }
}
