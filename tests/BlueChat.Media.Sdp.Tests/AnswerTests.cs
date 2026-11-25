using System.Globalization;

namespace BlueChat.Media.Sdp.Tests;

public class AnswerTests
{
    private static readonly SdpFingerprint Fp = new("sha-256",
        string.Join(':', Enumerable.Range(0, 32).Select(i => (i * 7 % 256).ToString("X2", CultureInfo.InvariantCulture))));

    private static SdpAnswerOptions Options(bool echo = true) => new()
    {
        IceUfrag = "bcUfrag1",
        IcePwd = "bcPasswordIsLongEnough24",
        Fingerprint = Fp,
        Candidates = [SdpParser.ParseCandidate("1 1 udp 2130706431 192.0.2.10 50000 typ host"),
                      SdpParser.ParseCandidate("2 1 tcp 1518280447 192.0.2.10 443 typ host tcptype passive")],
        AcceptDataChannel = true,
        SessionId = "4611686018427387904",
        SendStreams = echo
            ? new Dictionary<string, SdpSendStream>
            {
                ["0"] = new(0x11111111, null, "bluechat", "echo", "echo-audio"),
                ["1"] = new(0x22222222, 0x33333333, "bluechat", "echo", "echo-video"),
            }
            : new Dictionary<string, SdpSendStream>(),
    };

    [Theory]
    [MemberData(nameof(Corpus.Files), MemberType = typeof(Corpus))]
    public void Answer_to_every_browser_offer_is_valid_and_is_written_for_the_browser_check(string file)
    {
        var offer = SdpParser.Parse(Corpus.Read(file));
        var answer = JsepAnswerer.CreateAnswer(offer, Options());
        var text = answer.Write();
        var reparsed = SdpParser.Parse(text);

        reparsed.IceLite.Should().BeTrue();
        reparsed.Media.Select(m => m.Mid).Should().Equal(offer.Media.Select(m => m.Mid));
        reparsed.Media.Select(m => m.Kind).Should().Equal(offer.Media.Select(m => m.Kind));
        var accepted = reparsed.Media.Where(m => !m.IsRejected).ToList();
        accepted.Should().NotBeEmpty();
        reparsed.BundleMids.Should().Equal(accepted.Select(m => m.Mid));
        foreach (var m in accepted)
        {
            m.Setup.Should().Be(SdpSetup.Passive);
            m.Fingerprint.Should().Be(Fp);
            m.IceUfrag.Should().Be("bcUfrag1");
            if (m.Kind != "application")
            {
                m.RtcpMux.Should().BeTrue();
                m.Formats.Should().NotBeEmpty();
                m.RtpMaps.Select(r => r.PayloadType.ToString(CultureInfo.InvariantCulture)).Should().BeEquivalentTo(m.Formats);
            }
        }

        accepted[0].Candidates.Should().HaveCount(2);
        accepted[0].EndOfCandidates.Should().BeTrue();

        Directory.CreateDirectory(Path.Combine(Corpus.OutDir(), "answers"));
        File.WriteAllText(Path.Combine(Corpus.OutDir(), "answers", file.Replace(".sdp", ".answer.sdp", StringComparison.Ordinal)), text);
    }

    [Theory]
    [InlineData("chromium-audio-video.sdp")]
    [InlineData("firefox-audio-video.sdp")]
    public void Codecs_follow_preference_order_with_rtx_and_supported_feedback_only(string file)
    {
        var offer = SdpParser.Parse(Corpus.Read(file));
        var answer = JsepAnswerer.CreateAnswer(offer, Options());
        var audio = answer.Media.Single(m => m.Kind == "audio");
        audio.RtpMaps.Single().EncodingName.Should().Be("opus");
        var video = answer.Media.Single(m => m.Kind == "video");
        var primary = video.RtpMaps.Where(r => r.EncodingName != "rtx").Select(r => r.EncodingName).ToList();
        primary.Should().StartWith("VP8");
        primary.Should().BeSubsetOf(["VP8", "VP9", "H264"]);
        primary.Should().OnlyHaveUniqueItems();
        foreach (var rtx in video.RtpMaps.Where(r => r.EncodingName == "rtx"))
        {
            var apt = video.Fmtps.Single(f => f.Format == rtx.PayloadType.ToString(CultureInfo.InvariantCulture)).ToDictionary()["apt"];
            video.Formats.Should().Contain(apt);
        }

        video.RtcpFbs.Select(f => f.Parameter is null ? f.Type : f.Type + " " + f.Parameter)
            .Should().OnlyContain(k => k == "nack" || k == "nack pli" || k == "ccm fir" || k == "transport-cc" || k == "goog-remb");
        var h264 = video.RtpMaps.FirstOrDefault(r => r.EncodingName == "H264");
        if (h264 is not null)
        {
            var fmtp = video.FmtpOf(h264.PayloadType)!;
            fmtp.Should().Contain("packetization-mode=1");
            fmtp.Should().MatchRegex("profile-level-id=(42[ce4]0|4d[cC]0|42e0)");
        }
    }

    [Fact]
    public void Codec_order_is_configurable()
    {
        var offer = SdpParser.Parse(Corpus.Read("chromium-audio-video.sdp"));
        var answer = JsepAnswerer.CreateAnswer(offer, Options() with
        {
            Codecs = [SdpCodecPreference.Opus, SdpCodecPreference.H264ConstrainedBaseline, SdpCodecPreference.Vp8],
        });
        answer.Media.Single(m => m.Kind == "video").RtpMaps.Where(r => r.EncodingName != "rtx")
            .Select(r => r.EncodingName).Should().Equal("H264", "VP8");
    }

    [Theory]
    [InlineData("chromium-simulcast.sdp")]
    [InlineData("firefox-simulcast.sdp")]
    public void Send_simulcast_is_answered_as_recv_with_the_same_rids(string file)
    {
        var answer = JsepAnswerer.CreateAnswer(SdpParser.Parse(Corpus.Read(file)), Options());
        var video = answer.Media.Single(m => m.Kind == "video");
        video.Rids.Should().OnlyContain(r => r.Direction == SdpRidDirection.Recv);
        video.Rids.Select(r => r.Id).Should().BeEquivalentTo("q", "h", "f");
        video.Simulcast!.Recv.SelectMany(a => a).Select(r => r.Id).Should().BeEquivalentTo("q", "h", "f");
        video.Simulcast.Send.Should().BeEmpty();
        answer.Write().Should().Contain("a=simulcast:recv ");
    }

    [Fact]
    public void Send_streams_add_msid_ssrc_and_fid_group()
    {
        var answer = JsepAnswerer.CreateAnswer(SdpParser.Parse(Corpus.Read("chromium-audio-video.sdp")), Options());
        var video = answer.Media.Single(m => m.Kind == "video");
        video.Direction.Should().Be(SdpDirection.SendRecv);
        video.Msids.Should().Equal(new SdpMsid("echo", "echo-video"));
        video.SsrcGroups.Should().ContainSingle(g => g.Semantics == "FID" && g.Ssrcs.SequenceEqual(new uint[] { 0x22222222, 0x33333333 }));
        video.Ssrcs.Should().Contain(new SdpSsrc(0x22222222, "cname", "bluechat"));
    }

    [Fact]
    public void Recv_only_mid_is_answered_recvonly_without_send_stream()
    {
        var o = Options() with { RecvOnlyMids = new HashSet<string> { "1" } };
        var answer = JsepAnswerer.CreateAnswer(SdpParser.Parse(Corpus.Read("chromium-audio-video.sdp")), o);
        var video = answer.Media.Single(m => m.Mid == "1");
        video.Direction.Should().Be(SdpDirection.RecvOnly);
        video.Ssrcs.Should().BeEmpty();
    }

    [Fact]
    public void Section_without_common_codec_is_rejected_with_port_zero_and_left_out_of_bundle()
    {
        var o = Options() with { Codecs = [SdpCodecPreference.Opus] };
        var answer = JsepAnswerer.CreateAnswer(SdpParser.Parse(Corpus.Read("chromium-audio-video.sdp")), o);
        var video = answer.Media.Single(m => m.Kind == "video");
        video.Port.Should().Be(0);
        video.Formats.Should().HaveCount(1);
        answer.BundleMids.Should().Equal("0");
    }

    [Fact]
    public void Data_channel_is_rejected_unless_enabled()
    {
        var offer = SdpParser.Parse(Corpus.Read("chromium-datachannel.sdp"));
        var off = JsepAnswerer.CreateAnswer(offer, Options() with { AcceptDataChannel = false });
        off.Media.Single().IsRejected.Should().BeTrue();
        off.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Offer_without_fingerprint_is_refused()
    {
        var offer = SdpParser.Parse(Corpus.Read("chromium-audio.sdp"));
        offer.Fingerprint = null;
        foreach (var m in offer.Media)
        {
            m.Fingerprint = null;
        }

        var act = () => JsepAnswerer.CreateAnswer(offer, Options());
        act.Should().Throw<SdpNegotiationException>().Which.Code.Should().Be("missing_fingerprint");
    }

    [Fact]
    public void Offer_with_passive_setup_is_refused()
    {
        var offer = SdpParser.Parse(Corpus.Read("chromium-audio.sdp"));
        offer.Media[0].Setup = SdpSetup.Passive;
        var act = () => JsepAnswerer.CreateAnswer(offer, Options());
        act.Should().Throw<SdpNegotiationException>().Which.Code.Should().Be("unsupported_setup");
    }

    [Fact]
    public void Offer_without_rtcp_mux_gets_the_section_rejected()
    {
        var offer = SdpParser.Parse(Corpus.Read("chromium-audio.sdp"));
        offer.Media[0].RtcpMux = false;
        JsepAnswerer.CreateAnswer(offer, Options()).Media[0].IsRejected.Should().BeTrue();
    }

    [Fact]
    public void Only_accepted_header_extensions_are_kept_with_the_offered_ids()
    {
        var offer = SdpParser.Parse(Corpus.Read("chromium-audio-video.sdp"));
        var answer = JsepAnswerer.CreateAnswer(offer, Options());
        foreach (var (o, a) in offer.Media.Zip(answer.Media))
        {
            foreach (var e in a.Extmaps)
            {
                o.Extmaps.Should().Contain(x => x.Id == e.Id && x.Uri == e.Uri);
            }

            a.Extmaps.Should().Contain(e => e.Uri == "urn:ietf:params:rtp-hdrext:sdes:mid");
        }
    }
}
