namespace BlueChat.Media.Sdp.Tests;

public class CorpusTests
{
    [Fact]
    public void Corpus_has_offers_from_two_browsers_and_five_cases()
    {
        var names = Corpus.Files().Select(f => (string)f[0]).ToList();
        names.Should().HaveCountGreaterThanOrEqualTo(10);
        names.Should().Contain(n => n.StartsWith("chromium-", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("firefox-", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Corpus.Files), MemberType = typeof(Corpus))]
    public void Browser_offer_parses(string file)
    {
        var s = SdpParser.Parse(Corpus.Read(file));
        s.Media.Should().NotBeEmpty();
        s.BundleMids.Should().NotBeEmpty();
        foreach (var m in s.Media)
        {
            m.Mid.Should().NotBeNull();
            (m.Fingerprint ?? s.Fingerprint).Should().NotBeNull();
            (m.Setup ?? s.Setup).Should().Be(SdpSetup.ActPass);
            (m.IceUfrag ?? s.IceUfrag).Should().NotBeNull();
        }
    }

    [Theory]
    [MemberData(nameof(Corpus.Files), MemberType = typeof(Corpus))]
    public void Write_keeps_every_line_and_is_stable(string file)
    {
        var original = Corpus.Read(file);
        var written = SdpParser.Parse(original).Write();
        // Same lines (attribute order inside a section may be normalised), CRLF endings.
        Lines(written).Should().BeEquivalentTo(Lines(original));
        written.Should().EndWith("\r\n").And.NotContain("\r\n\r\n");
        // parse → write → parse → write is a fixed point.
        SdpParser.Parse(written).Write().Should().Be(written);
    }

    private static List<string> Lines(string sdp) =>
        sdp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    [Fact]
    public void Chromium_simulcast_offer_has_three_send_rids()
    {
        var video = SdpParser.Parse(Corpus.Read("chromium-simulcast.sdp")).Media.Single(m => m.Kind == "video");
        video.Rids.Select(r => r.Id).Should().Equal("q", "h", "f");
        video.Rids.Should().OnlyContain(r => r.Direction == SdpRidDirection.Send);
        video.Simulcast!.Send.Select(a => a.Single().Id).Should().Equal("q", "h", "f");
        video.Extmaps.Should().Contain(e => e.Uri == "urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id");
    }

    [Fact]
    public void Firefox_simulcast_offer_has_three_send_rids()
    {
        var video = SdpParser.Parse(Corpus.Read("firefox-simulcast.sdp")).Media.Single(m => m.Kind == "video");
        video.Simulcast!.Send.SelectMany(a => a).Select(r => r.Id).Should().BeEquivalentTo("q", "h", "f");
    }

    [Theory]
    [InlineData("chromium-gathered.sdp")]
    [InlineData("firefox-gathered.sdp")]
    public void Gathered_offer_has_parsed_candidates(string file)
    {
        var s = SdpParser.Parse(Corpus.Read(file));
        var candidates = s.Media.SelectMany(m => m.Candidates).ToList();
        candidates.Should().NotBeEmpty();
        candidates.Should().OnlyContain(c => c.Component >= 1 && c.Port >= 0 && c.Type.Length > 0);
    }

    [Theory]
    [InlineData("chromium-datachannel.sdp")]
    [InlineData("firefox-datachannel.sdp")]
    public void Data_channel_offer_is_sctp(string file)
    {
        var m = SdpParser.Parse(Corpus.Read(file)).Media.Single();
        m.Kind.Should().Be("application");
        m.Protocol.Should().Be("UDP/DTLS/SCTP");
        m.Formats.Should().Equal("webrtc-datachannel");
        m.SctpPort.Should().Be(5000);
    }
}
