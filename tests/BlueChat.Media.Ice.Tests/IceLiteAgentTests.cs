using System.Buffers;
using System.Net;
using BlueChat.Media.Ice;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Ice.Tests;

public class IceLiteAgentTests
{
    private static readonly IceCredentials Server = new("srvU", "serverpassword1234567890");
    private static readonly IceCredentials Browser = new("brwU", "browserpassword123456789");
    private static readonly IceEndpoint A = new(IceTransport.Udp, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000));
    private static readonly IceEndpoint B = new(IceTransport.Udp, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40002));

    /// <summary>A connectivity check as a controlling full agent (browser) sends it (RFC 8445 §7.2.2).</summary>
    internal static byte[] Check(IceCredentials to, IceCredentials from, bool useCandidate = false, bool controlling = true,
        string? password = null, bool fingerprint = true, ushort extraAttribute = 0)
    {
        var buf = new byte[256];
        Span<byte> tid = stackalloc byte[12];
        StunWriter.NewTransactionId(tid);
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.Request, tid);
        w.AddString(StunAttr.Username, $"{to.Ufrag}:{from.Ufrag}");
        w.AddUInt32(StunAttr.Priority, 0x6e0001ff);
        w.AddUInt64(controlling ? StunAttr.IceControlling : StunAttr.IceControlled, 0x1122334455667788);
        if (useCandidate)
        {
            w.AddFlag(StunAttr.UseCandidate);
        }

        if (extraAttribute != 0)
        {
            w.AddUInt32(extraAttribute, 7);
        }

        w.AddMessageIntegrity(StunCredentials.ShortTermKey(password ?? to.Pwd));
        if (fingerprint)
        {
            w.AddFingerprint();
        }

        return w.Written.ToArray();
    }

    private static (CheckOutcome Outcome, byte[] Response) Send(IceLiteAgent agent, byte[] check, IceEndpoint from)
    {
        StunMessage.TryParse(check, out var m).Should().Be(StunParseError.None);
        var response = new byte[256];
        var outcome = agent.HandleRequest(m, from, response, out var n);
        return (outcome, response[..n]);
    }

    [Fact]
    public void Valid_check_is_answered_with_xor_mapped_address_integrity_and_fingerprint()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        var (outcome, response) = Send(agent, Check(Server, Browser), A);

        outcome.Should().Be(CheckOutcome.Answered);
        agent.State.Should().Be(IceState.Checking);
        StunMessage.TryParse(response, out var r).Should().Be(StunParseError.None);
        r.Class.Should().Be(StunClass.SuccessResponse);
        r.TryGetXorAddress(StunAttr.XorMappedAddress, out var mapped).Should().BeTrue();
        mapped.Should().Be(A.Remote);
        StunIntegrity.VerifySha1(r, StunCredentials.ShortTermKey(Server.Pwd)).Should().Be(IntegrityResult.Ok);
        StunIntegrity.VerifyFingerprint(r).Should().Be(IntegrityResult.Ok);
    }

    [Fact]
    public void Use_candidate_nominates_and_renomination_moves_the_selected_pair()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        var changes = new List<IceEndpoint>();
        agent.SelectedChanged += changes.Add;

        Send(agent, Check(Server, Browser, useCandidate: true), A);
        agent.State.Should().Be(IceState.Connected);
        agent.Selected.Should().Be(A);
        agent.MaySendTo(A).Should().BeTrue();
        agent.MaySendTo(B).Should().BeFalse();

        Send(agent, Check(Server, Browser, useCandidate: true), B);
        agent.Selected.Should().Be(B);
        changes.Should().Equal(A, B);
    }

    [Fact]
    public void Wrong_password_is_dropped_silently()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        var (outcome, response) = Send(agent, Check(Server, Browser, password: "not-the-server-password-x"), A);
        outcome.Should().Be(CheckOutcome.Unauthenticated);
        response.Should().BeEmpty();
        agent.State.Should().Be(IceState.New);
    }

    [Fact]
    public void Other_ufrag_is_not_for_us()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        Send(agent, Check(new IceCredentials("zzzz", Server.Pwd), Browser), A).Outcome.Should().Be(CheckOutcome.NotForUs);
        Send(agent, Check(Server, new IceCredentials("other", Browser.Pwd)), A).Outcome.Should().Be(CheckOutcome.NotForUs);
    }

    [Fact]
    public void Missing_fingerprint_is_ignored()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        Send(agent, Check(Server, Browser, fingerprint: false), A).Outcome.Should().Be(CheckOutcome.Ignored);
    }

    [Fact]
    public void Controlled_peer_gets_487_role_conflict()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        var (outcome, response) = Send(agent, Check(Server, Browser, controlling: false), A);
        outcome.Should().Be(CheckOutcome.Rejected);
        StunMessage.TryParse(response, out var r).Should().Be(StunParseError.None);
        r.TryGetErrorCode(out var code, out _).Should().BeTrue();
        code.Should().Be(487);
    }

    [Fact]
    public void Unknown_comprehension_required_attribute_gets_420()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        var (outcome, response) = Send(agent, Check(Server, Browser, extraAttribute: 0x0042), A);
        outcome.Should().Be(CheckOutcome.Rejected);
        StunMessage.TryParse(response, out var r).Should().Be(StunParseError.None);
        r.TryGetErrorCode(out var code, out _).Should().BeTrue();
        code.Should().Be(420);
        r.Has(StunAttr.UnknownAttributes).Should().BeTrue();
    }

    [Fact]
    public void Consent_lapses_to_disconnected_then_failed_and_recovers_before_failure()
    {
        var clock = new ManualClock();
        var agent = new IceLiteAgent(Server, Browser, clock);
        Send(agent, Check(Server, Browser, useCandidate: true), A);

        clock.Advance(TimeSpan.FromSeconds(11));
        agent.Tick();
        agent.State.Should().Be(IceState.Disconnected);
        agent.MaySendTo(A).Should().BeTrue("consent lasts 30 s (RFC 7675)");

        Send(agent, Check(Server, Browser), A); // consent refresh
        agent.State.Should().Be(IceState.Connected);

        clock.Advance(TimeSpan.FromSeconds(30));
        agent.Tick();
        agent.State.Should().Be(IceState.Failed);
        agent.MaySendTo(A).Should().BeFalse();
    }

    [Fact]
    public void Ice_restart_keeps_old_credentials_until_the_first_new_check()
    {
        var agent = new IceLiteAgent(Server, Browser, TimeProvider.System);
        Send(agent, Check(Server, Browser, useCandidate: true), A);

        var newServer = IceCredentials.Generate();
        var newBrowser = IceCredentials.Generate();
        agent.Restart(newServer, newBrowser);

        Send(agent, Check(Server, Browser), A).Outcome.Should().Be(CheckOutcome.Answered, "old pair still valid during restart");
        Send(agent, Check(newServer, newBrowser, useCandidate: true), B).Outcome.Should().Be(CheckOutcome.Answered);
        agent.Selected.Should().Be(B);
        Send(agent, Check(Server, Browser), A).Outcome.Should().Be(CheckOutcome.NotForUs, "restart finished");
    }

    [Fact]
    public void Generated_credentials_follow_rfc_8445_lengths()
    {
        var c = IceCredentials.Generate();
        IceCredentials.IsValidUfrag(c.Ufrag).Should().BeTrue();
        IceCredentials.IsValidPwd(c.Pwd).Should().BeTrue();
        IceCredentials.Generate().Should().NotBe(c);
    }

    [Fact]
    public void Shared_port_mux_binds_endpoints_only_through_authenticated_checks()
    {
        var mux = new SharedPortMux<string>();
        var agent1 = new IceLiteAgent(Server, Browser, TimeProvider.System);
        var other = IceCredentials.Generate();
        var otherBrowser = IceCredentials.Generate();
        var agent2 = new IceLiteAgent(other, otherBrowser, TimeProvider.System);
        mux.Register("s1", agent1);
        mux.Register("s2", agent2);
        var response = new byte[256];

        mux.Lookup(A).Should().BeNull();
        StunMessage.TryParse(Check(Server, Browser, password: "not-the-server-password-x"), out var bad);
        mux.RouteCheck(bad, A, response, out _, out var badOutcome).Should().BeNull();
        badOutcome.Should().Be(CheckOutcome.Unauthenticated);
        mux.Lookup(A).Should().BeNull("an unauthenticated packet must not bind an address");

        StunMessage.TryParse(Check(Server, Browser), out var good1);
        mux.RouteCheck(good1, A, response, out var n1, out _).Should().Be("s1");
        n1.Should().BeGreaterThan(0);
        StunMessage.TryParse(Check(other, otherBrowser), out var good2);
        mux.RouteCheck(good2, B, response, out _, out _).Should().Be("s2");

        mux.Lookup(A).Should().Be("s1");
        mux.Lookup(B).Should().Be("s2");
        mux.Unregister("s1");
        mux.Lookup(A).Should().BeNull();
        mux.Lookup(B).Should().Be("s2");
    }

    [Fact]
    public void Rfc4571_framing_reads_split_and_coalesced_frames()
    {
        var p1 = new byte[] { 1, 2, 3 };
        var p2 = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var wire = new byte[2 + p1.Length + 2 + p2.Length];
        var n = Rfc4571Framer.Write(p1, wire);
        Rfc4571Framer.Write(p2, wire.AsSpan(n));

        // Coalesced
        var seq = new ReadOnlySequence<byte>(wire);
        Rfc4571Framer.TryRead(ref seq, 1500, out var f1).Should().Be(FrameResult.Frame);
        f1.ToArray().Should().Equal(p1);
        Rfc4571Framer.TryRead(ref seq, 1500, out var f2).Should().Be(FrameResult.Frame);
        f2.ToArray().Should().Equal(p2);
        Rfc4571Framer.TryRead(ref seq, 1500, out _).Should().Be(FrameResult.NeedMore);

        // Split mid-header and mid-body
        var partial = new ReadOnlySequence<byte>(wire.AsMemory(0, 1));
        Rfc4571Framer.TryRead(ref partial, 1500, out _).Should().Be(FrameResult.NeedMore);
        partial = new ReadOnlySequence<byte>(wire.AsMemory(0, 4));
        Rfc4571Framer.TryRead(ref partial, 1500, out _).Should().Be(FrameResult.NeedMore);

        // Limits
        var zero = new ReadOnlySequence<byte>(new byte[] { 0, 0 });
        Rfc4571Framer.TryRead(ref zero, 1500, out _).Should().Be(FrameResult.Invalid);
        var huge = new ReadOnlySequence<byte>(new byte[] { 0xFF, 0xFF });
        Rfc4571Framer.TryRead(ref huge, 1500, out _).Should().Be(FrameResult.Invalid);
    }

    [Theory]
    [InlineData(0x00, PacketKind.Stun)]
    [InlineData(0x01, PacketKind.Stun)]
    [InlineData(0x16, PacketKind.Dtls)]
    [InlineData(0x17, PacketKind.Dtls)]
    [InlineData(0x40, PacketKind.TurnChannel)]
    [InlineData(0x80, PacketKind.Rtp)]
    [InlineData(0xFF, PacketKind.Unknown)]
    public void Rfc7983_classification(byte first, PacketKind kind)
    {
        PacketClassifier.Classify([first, 0x60, 0, 0]).Should().Be(kind);
    }

    [Fact]
    public void Rtcp_is_told_apart_from_rtp_by_payload_type_range()
    {
        PacketClassifier.Classify([0x80, 200, 0, 6]).Should().Be(PacketKind.Rtcp); // SR
        PacketClassifier.Classify([0x81, 205, 0, 3]).Should().Be(PacketKind.Rtcp); // RTPFB
        PacketClassifier.Classify([0x80, 111, 0, 1]).Should().Be(PacketKind.Rtp);
        PacketClassifier.Classify([0x80, 0x80 | 96, 0, 1]).Should().Be(PacketKind.Rtp); // marker + PT 96
    }

    [Fact]
    public void Candidate_lines_prefer_udp_over_passive_tcp()
    {
        var udp = IceCandidates.HostUdp("1", IPAddress.Parse("203.0.113.5"), 50000);
        var tcp = IceCandidates.HostTcpPassive("2", IPAddress.Parse("203.0.113.5"), 50000);
        udp.Should().Be("candidate:1 1 udp 2130706431 203.0.113.5 50000 typ host");
        tcp.Should().EndWith("typ host tcptype passive");
        uint.Parse(udp.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(uint.Parse(tcp.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
