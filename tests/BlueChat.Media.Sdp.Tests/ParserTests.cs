using System.Text;

namespace BlueChat.Media.Sdp.Tests;

public class ParserTests
{
    private const string Head = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";

    private static string Media(params string[] attrs) =>
        Head + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\n" + string.Concat(attrs.Select(a => a + "\r\n"));

    private static string CodeOf(string sdp)
    {
        var act = () => SdpParser.Parse(sdp);
        return act.Should().Throw<SdpParseException>().Which.Code;
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("v=1\r\n", "bad_version")]
    [InlineData("o=- 1 2 IN IP4 x\r\n", "bad_version")]
    [InlineData("v=0\r\ns=-\r\nt=0 0\r\n", "missing_origin")]
    [InlineData("v=0\r\no=- 1 2 IN IP4 x\r\nt=0 0\r\n", "missing_session_name")]
    [InlineData("v=0\r\no=- 1 2 IN IP4 x\r\ns=-\r\n", "missing_timing")]
    [InlineData("v=0\r\no=- 1 2 IN IP4\r\ns=-\r\nt=0 0\r\n", "bad_origin")]
    [InlineData("v=0\r\nno equals\r\n", "bad_line")]
    [InlineData("v=0\r\nX=upper\r\n", "bad_line")]
    [InlineData("v=0\r\nq=unknown\r\n", "unknown_line_type")]
    [InlineData("v=0\r\nv=0\r\n", "duplicate_version")]
    [InlineData("v=0\r\no=- 1 2 IN IP4 x\r\ns=-\r\nm=audio 9 RTP/AVP 0\r\nt=0 0\r\n", "misplaced_line")]
    public void Session_errors_have_stable_codes(string sdp, string code) => CodeOf(sdp).Should().Be(code);

    [Theory]
    [InlineData("m=audio 9 UDP/TLS/RTP/SAVPF", "bad_media")]
    [InlineData("m=audio 70000 UDP/TLS/RTP/SAVPF 111", "bad_media")]
    [InlineData("m=audio -1 UDP/TLS/RTP/SAVPF 111", "bad_media")]
    [InlineData("m=audio 9/0 UDP/TLS/RTP/SAVPF 111", "bad_media")]
    public void Bad_media_lines(string line, string code) => CodeOf(Head + line + "\r\n").Should().Be(code);

    [Theory]
    [InlineData("a=rtpmap:128 opus/48000/2", "bad_rtpmap")]
    [InlineData("a=rtpmap:111 opus", "bad_rtpmap")]
    [InlineData("a=rtpmap:111 opus/0", "bad_rtpmap")]
    [InlineData("a=rtpmap:x opus/48000", "bad_rtpmap")]
    [InlineData("a=fmtp:111", "bad_fmtp")]
    [InlineData("a=fmtp:", "missing_value")]
    [InlineData("a=fmtp:111 ", "bad_fmtp")]
    [InlineData("a=rtcp-fb:111", "bad_rtcp_fb")]
    [InlineData("a=rtcp-fb:abc nack", "bad_rtcp_fb")]
    [InlineData("a=extmap:0 urn:x", "bad_extmap")]
    [InlineData("a=extmap:256 urn:x", "bad_extmap")]
    [InlineData("a=extmap:3/sideways urn:x", "bad_extmap")]
    [InlineData("a=extmap:3", "bad_extmap")]
    [InlineData("a=fingerprint:sha-256 ZZ:00", "bad_fingerprint")]
    [InlineData("a=fingerprint:sha-256", "bad_fingerprint")]
    [InlineData("a=setup:sometimes", "bad_setup")]
    [InlineData("a=ice-ufrag:abc", "bad_ice_ufrag")]
    [InlineData("a=ice-ufrag:abc!def", "bad_ice_ufrag")]
    [InlineData("a=ice-pwd:short", "bad_ice_pwd")]
    [InlineData("a=mid:1", "duplicate_attribute")]
    [InlineData("a=rid:q both", "bad_rid")]
    [InlineData("a=rid:q!x send", "bad_rid")]
    [InlineData("a=rid:q send pt=abc", "bad_rid")]
    [InlineData("a=simulcast:send", "bad_simulcast")]
    [InlineData("a=simulcast:send q recv h send f", "bad_simulcast")]
    [InlineData("a=simulcast:send q;;h", "bad_rid")]
    [InlineData("a=ssrc:abc cname:x", "bad_ssrc")]
    [InlineData("a=ssrc:4294967296 cname:x", "bad_ssrc")]
    [InlineData("a=ssrc-group:FID", "bad_ssrc_group")]
    [InlineData("a=candidate:1 1 udp 1 1.2.3.4 5 host", "bad_candidate")]
    [InlineData("a=candidate:1 0 udp 1 1.2.3.4 5 typ host", "bad_candidate")]
    [InlineData("a=candidate:1 1 udp 1 1.2.3.4 99999 typ host", "bad_candidate")]
    [InlineData("a=candidate:1 1 udp 1 1.2.3.4 5 typ host generation", "bad_candidate")]
    [InlineData("a=sctp-port:x", "bad_sctp_port")]
    [InlineData("a=msid:a b c", "bad_msid")]
    [InlineData("a=sendrecv\r\na=recvonly", "duplicate_attribute")]
    [InlineData("a=simulcast:send q\r\na=simulcast:send h", "duplicate_attribute")]
    [InlineData("a=:value", "bad_attribute")]
    public void Attribute_errors_have_stable_codes(string attr, string code) => CodeOf(Media(attr)).Should().Be(code);

    [Fact]
    public void Error_reports_the_line_number()
    {
        var act = () => SdpParser.Parse(Media("a=rtcp-mux", "a=rtpmap:999 x/1"));
        act.Should().Throw<SdpParseException>().Which.LineNumber.Should().Be(9);
    }

    [Fact]
    public void Control_characters_and_bad_utf8_are_refused()
    {
        CodeOf(Media("a=x:\u0001")).Should().Be("control_character");
        CodeOf(Media("a=x:a\rb")).Should().Be("control_character");
        var bytes = Encoding.UTF8.GetBytes(Media()).Concat(new byte[] { 0x61, 0x3D, 0xC3, 0x28, 0x0D, 0x0A }).ToArray();
        var act = () => SdpParser.Parse(bytes);
        act.Should().Throw<SdpParseException>().Which.Code.Should().Be("bad_encoding");
    }

    [Fact]
    public void Limits_are_enforced()
    {
        var limits = new SdpLimits { MaxBytes = 200, MaxLines = 8, MaxLineLength = 40, MaxMediaSections = 1, MaxAttributesPerSection = 2 };
        var act = () => SdpParser.Parse(Media(new string('a', 300)), limits);
        act.Should().Throw<SdpParseException>().Which.Code.Should().Be("too_large");
        act = () => SdpParser.Parse(Media("a=x", "a=y", "a=z", "a=w"), limits with { MaxBytes = 10_000 });
        act.Should().Throw<SdpParseException>().Which.Code.Should().Be("too_many_lines");
        act = () => SdpParser.Parse(Media("a=" + new string('x', 50)), limits with { MaxBytes = 10_000, MaxLines = 100 });
        act.Should().Throw<SdpParseException>().Which.Code.Should().Be("line_too_long");
        act = () => SdpParser.Parse(Head + "m=audio 9 RTP/AVP 0\r\nm=audio 9 RTP/AVP 0\r\n", limits with { MaxBytes = 10_000, MaxLines = 100 });
        act.Should().Throw<SdpParseException>().Which.Code.Should().Be("too_many_media");
        act = () => SdpParser.Parse(Media("a=x", "a=y", "a=z"), limits with { MaxBytes = 10_000, MaxLines = 100 });
        act.Should().Throw<SdpParseException>().Which.Code.Should().Be("too_many_attributes");
        SdpParser.Parse(Media(), SdpLimits.Default).Media.Should().HaveCount(1);
        var huge = Encoding.UTF8.GetBytes(new string('a', 70_000));
        var big = () => SdpParser.Parse(huge);
        big.Should().Throw<SdpParseException>().Which.Code.Should().Be("too_large");
    }

    [Fact]
    public void Lf_only_line_endings_are_accepted()
    {
        var s = SdpParser.Parse(Media("a=rtcp-mux").Replace("\r\n", "\n", StringComparison.Ordinal));
        s.Media.Single().RtcpMux.Should().BeTrue();
    }

    [Fact]
    public void Candidate_grammar_covers_tcp_related_address_and_extensions()
    {
        var c = SdpParser.ParseCandidate("candidate:842163049 1 tcp 1518280447 192.0.2.5 9 typ srflx raddr 10.0.0.2 rport 0 tcptype active generation 0 ufrag abcd network-id 1");
        c.Foundation.Should().Be("842163049");
        c.Transport.Should().Be("tcp");
        c.Priority.Should().Be(1518280447u);
        c.Type.Should().Be("srflx");
        c.RelatedAddress.Should().Be("10.0.0.2");
        c.RelatedPort.Should().Be(0);
        c.TcpType.Should().Be("active");
        c.Generation.Should().Be("0");
        c.Ufrag.Should().Be("abcd");
        c.Extension("network-id").Should().Be("1");
        SdpWriter.Candidate(c).Should().Be("842163049 1 tcp 1518280447 192.0.2.5 9 typ srflx raddr 10.0.0.2 rport 0 tcptype active generation 0 ufrag abcd network-id 1");

        var mdns = SdpParser.ParseCandidate("1 1 UDP 2122260223 4e1a5b1c-1111-2222-3333-444455556666.local 54321 typ host");
        mdns.Address.Should().EndWith(".local");
    }

    [Fact]
    public void Simulcast_alternatives_and_paused_streams_round_trip()
    {
        var s = SdpParser.Parse(Media("a=rid:a send", "a=rid:b send", "a=rid:c send pt=111;max-width=1280;max-fps", "a=simulcast:send a,~b;c recv x"));
        var m = s.Media.Single();
        m.Simulcast!.Send.Should().HaveCount(2);
        m.Simulcast.Send[0].Select(r => (r.Id, r.Paused)).Should().Equal(("a", false), ("b", true));
        m.Simulcast.Recv.Single().Single().Id.Should().Be("x");
        var rid = m.Rids.Single(r => r.Id == "c");
        rid.PayloadTypes.Should().Equal(111);
        rid.Restrictions.Should().Equal(new KeyValuePair<string, string?>("max-width", "1280"), new KeyValuePair<string, string?>("max-fps", null));
        s.Write().Should().Contain("a=simulcast:send a,~b;c recv x\r\n").And.Contain("a=rid:c send pt=111;max-width=1280;max-fps\r\n");
    }

    [Fact]
    public void Unknown_attributes_and_lines_round_trip()
    {
        var sdp = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\ni=info\r\nt=0 0\r\na=x-custom:42\r\na=flag\r\n"
                  + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\ni=media info\r\nc=IN IP4 0.0.0.0\r\nb=AS:64\r\na=mid:0\r\na=ptime:20\r\na=x-other\r\n";
        var s = SdpParser.Parse(sdp);
        s.OtherAttributes.Should().Equal(new SdpAttribute("x-custom", "42"), new SdpAttribute("flag", null));
        s.Media[0].OtherAttributes.Should().Equal(new SdpAttribute("ptime", "20"), new SdpAttribute("x-other", null));
        s.Write().Should().Be(sdp);
    }

    [Fact]
    public void Extmap_direction_and_attributes_round_trip()
    {
        var m = SdpParser.Parse(Media("a=extmap:2/recvonly urn:ietf:params:rtp-hdrext:csrc-audio-level", "a=extmap:20 urn:x:y extra")).Media.Single();
        m.Extmaps[0].Should().Be(new SdpExtmap(2, SdpDirection.RecvOnly, "urn:ietf:params:rtp-hdrext:csrc-audio-level", null));
        m.Extmaps[1].Should().Be(new SdpExtmap(20, null, "urn:x:y", "extra"));
    }
}
