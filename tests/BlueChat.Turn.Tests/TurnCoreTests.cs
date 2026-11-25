using System.Buffers.Binary;
using System.Net;
using BlueChat.Media.Stun;
using BlueChat.Turn;

namespace BlueChat.Turn.Tests;

public class AuthenticationTests
{
    [Fact]
    public void Unauthenticated_allocate_gets_401_with_realm_and_nonce_and_no_integrity()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var r = c.Send(StunMethod.Allocate, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24), null, sign: false);
        r.Code.Should().Be(401);
        r.Realm.Should().Be("bluechat.test");
        r.Nonce.Should().HaveLength(27);
        r.HasIntegrity.Should().BeFalse();
        r.FingerprintOk.Should().BeTrue();
    }

    [Fact]
    public void Allocate_succeeds_with_time_limited_credentials_and_the_response_is_signed()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var r = c.Allocate();
        r.IsSuccess.Should().BeTrue();
        r.Relayed.Should().Be(new IPEndPoint(IPAddress.Parse("10.79.0.10"), 50000));
        r.Mapped.Should().Be(c.Tuple.Client);
        r.Lifetime.Should().Be(600);
        r.VerifiesWith(c.Key).Should().BeTrue();
        rig.Core.AllocationCount.Should().Be(1);
    }

    [Fact]
    public void Advertised_relay_address_replaces_the_socket_address()
    {
        var rig = new Rig(o => o.RelayAdvertisedAddress = IPAddress.Parse("203.0.113.5"));
        rig.NewClient().Allocate().Relayed!.Address.Should().Be(IPAddress.Parse("203.0.113.5"));
    }

    [Fact]
    public void Wrong_password_is_401()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Password = "not-the-hmac";
        var r = c.Allocate();
        r.Code.Should().Be(401);
        r.Nonce.Should().NotBeNull();
        rig.Core.AllocationCount.Should().Be(0);
    }

    [Fact]
    public void Expired_time_limited_username_is_401()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        (c.Username, c.Password) = TurnRestCredentials.Issue(Rig.Secret, "alice", rig.Time.GetUtcNow().AddSeconds(-1));
        c.Allocate().Code.Should().Be(401);
    }

    [Fact]
    public void Username_without_expiry_prefix_is_401()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Username = "alice";
        c.Password = TurnRestCredentials.PasswordFor(Rig.Secret, "alice");
        c.Allocate().Code.Should().Be(401);
    }

    [Fact]
    public void Expired_nonce_gets_438_with_a_new_nonce_and_the_retry_succeeds()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.Lifetime, 3600)).IsSuccess.Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        var raw = c.Send(StunMethod.Refresh, null, null, sign: true);
        raw.Code.Should().Be(438);
        raw.Nonce.Should().NotBeNull();
        c.Refresh(null).IsSuccess.Should().BeTrue(); // helper retries with the new nonce
    }

    [Fact]
    public void Nonce_issued_to_another_ip_is_stale()
    {
        var rig = new Rig();
        var a = rig.NewClient(clientIp: "198.51.100.7");
        var challenge = a.Send(StunMethod.Allocate, null, null, sign: false);
        var b = rig.NewClient(clientIp: "198.51.100.8");
        // b reuses a's nonce
        var field = typeof(Client).GetField("_nonce", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(b, challenge.Nonce);
        b.Send(StunMethod.Allocate, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24), null, sign: true)
            .Code.Should().Be(438);
    }

    [Fact]
    public void Missing_username_realm_or_nonce_with_integrity_is_400()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var buf = new byte[512];
        var w = new StunWriter(buf, StunMethod.Allocate, StunClass.Request, Client.NewTid());
        w.AddUInt32(StunAttr.RequestedTransport, 17u << 24);
        w.AddString(StunAttr.Username, c.Username);
        w.AddMessageIntegrity(c.Key);
        c.Raw(w.Written);
        new Response(rig.Sink.Last).Code.Should().Be(400);
    }

    [Fact]
    public void Wrong_realm_is_401()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var challenge = c.Send(StunMethod.Allocate, null, null, sign: false);
        var buf = new byte[512];
        var w = new StunWriter(buf, StunMethod.Allocate, StunClass.Request, Client.NewTid());
        w.AddUInt32(StunAttr.RequestedTransport, 17u << 24);
        w.AddString(StunAttr.Username, c.Username);
        w.AddString(StunAttr.Realm, "evil.test");
        w.AddString(StunAttr.Nonce, challenge.Nonce!);
        w.AddMessageIntegrity(StunCredentials.LongTermKeyMd5(c.Username, "evil.test", c.Password));
        c.Raw(w.Written);
        new Response(rig.Sink.Last).Code.Should().Be(401);
    }

    [Fact]
    public void Bad_fingerprint_is_discarded_silently()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var buf = new byte[256];
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.Request, Client.NewTid());
        w.AddFingerprint();
        var bytes = w.Written.ToArray();
        bytes[^1] ^= 1;
        rig.Sink.Sent.Clear();
        c.Raw(bytes);
        rig.Sink.Sent.Should().BeEmpty();
    }

    [Fact]
    public void Binding_request_needs_no_credentials()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var r = c.Send(StunMethod.Binding, null, null, sign: false);
        r.IsSuccess.Should().BeTrue();
        r.Mapped.Should().Be(c.Tuple.Client);
    }

    [Fact]
    public void Unknown_method_is_400()
    {
        var rig = new Rig();
        rig.NewClient().Send(0x00A, null, null, sign: false).Code.Should().Be(400);
    }
}

public class AllocateTests
{
    [Fact]
    public void Missing_requested_transport_is_400_and_tcp_is_442()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Request(StunMethod.Allocate).Code.Should().Be(400);
        c.Request(StunMethod.Allocate, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 6u << 24)).Code.Should().Be(442);
    }

    [Fact]
    public void Retransmitted_allocate_gets_the_same_response_and_a_new_transaction_gets_437()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        var tid = Client.NewTid();
        var first = c.Request(StunMethod.Allocate, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24), tid);
        first.IsSuccess.Should().BeTrue();
        var again = c.Request(StunMethod.Allocate, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24), tid);
        again.Bytes.Should().Equal(first.Bytes);
        c.Allocate().Code.Should().Be(437);
        rig.Relays.Created.Should().HaveCount(1);
    }

    [Fact]
    public void Dont_fragment_is_answered_420_listing_it()
    {
        var rig = new Rig();
        var r = rig.NewClient().Allocate((ref StunWriter w) => w.AddFlag(StunAttr.DontFragment));
        r.Code.Should().Be(420);
        r.Unknown.Should().Equal(StunAttr.DontFragment);
        rig.Core.AllocationCount.Should().Be(0);
    }

    [Fact]
    public void Unknown_comprehension_required_attribute_is_420()
    {
        var rig = new Rig();
        var r = rig.NewClient().Allocate((ref StunWriter w) => w.AddUInt32(0x0077, 1));
        r.Code.Should().Be(420);
        r.Unknown.Should().Equal((ushort)0x0077);
    }

    [Fact]
    public void Even_port_is_honoured_and_reservations_are_refused()
    {
        var rig = new Rig();
        rig.NewClient(port: 1).Allocate((ref StunWriter w) => w.Add(StunAttr.EvenPort, [0x00])).Relayed!.Port.Should().Be(50000);
        rig.NewClient(port: 2).Allocate((ref StunWriter w) => w.Add(StunAttr.EvenPort, [0x80])).Code.Should().Be(508);
        rig.NewClient(port: 3).Allocate((ref StunWriter w) => w.AddUInt64(StunAttr.ReservationToken, 1)).Code.Should().Be(508);
        rig.NewClient(port: 4).Allocate((ref StunWriter w) => w.Add(StunAttr.EvenPort, [0x00, 0x00])).Code.Should().Be(400);
    }

    [Fact]
    public void Requested_address_family()
    {
        var rig = new Rig();
        rig.NewClient(port: 1).Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.RequestedAddressFamily, 0x02000000)).Code.Should().Be(440);
        rig.NewClient(port: 2).Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.RequestedAddressFamily, 0x03000000)).Code.Should().Be(440);
        rig.NewClient(port: 3).Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.RequestedAddressFamily, 0x01000000)).IsSuccess.Should().BeTrue();

        var v6 = new Rig();
        v6.Relays.SupportsIPv6 = true;
        v6.NewClient().Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.RequestedAddressFamily, 0x02000000))
            .Relayed!.Address.Should().Be(IPAddress.Parse("2001:db8::10"));
    }

    [Fact]
    public void Lifetime_is_clamped_between_default_and_maximum()
    {
        var rig = new Rig();
        rig.NewClient(port: 1).Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.Lifetime, 100)).Lifetime.Should().Be(600);
        rig.NewClient(port: 2).Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.Lifetime, 1800)).Lifetime.Should().Be(1800);
        rig.NewClient(port: 3).Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.Lifetime, 99999)).Lifetime.Should().Be(3600);
    }

    [Fact]
    public void Quota_per_user_per_ip_and_server_capacity()
    {
        var rig = new Rig(o =>
        {
            o.MaxAllocationsPerUser = 2;
            o.MaxAllocationsPerClientIp = 3;
            o.MaxAllocations = 4;
        });
        rig.NewClient("alice", port: 1).Allocate().IsSuccess.Should().BeTrue();
        rig.NewClient("alice", port: 2).Allocate().IsSuccess.Should().BeTrue();
        rig.NewClient("alice", port: 3).Allocate().Code.Should().Be(486); // per user
        rig.NewClient("bob", port: 4).Allocate().IsSuccess.Should().BeTrue();
        rig.NewClient("carol", port: 5).Allocate().Code.Should().Be(486); // per IP (3)
        rig.NewClient("carol", "198.51.100.9", 6).Allocate().IsSuccess.Should().BeTrue();
        rig.NewClient("dave", "198.51.100.10", 7).Allocate().Code.Should().Be(508); // server (4)
    }

    [Fact]
    public void No_free_relay_port_is_508()
    {
        var rig = new Rig();
        rig.Relays.Capacity = 0;
        rig.NewClient().Allocate().Code.Should().Be(508);
    }
}

public class RefreshAndExpiryTests
{
    [Fact]
    public void Refresh_extends_and_zero_deletes()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        rig.Time.Advance(TimeSpan.FromSeconds(500));
        c.Refresh(null).Lifetime.Should().Be(600);
        rig.Time.Advance(TimeSpan.FromSeconds(500));
        rig.Core.Tick();
        rig.Core.AllocationCount.Should().Be(1);
        var r = c.Refresh(0);
        r.IsSuccess.Should().BeTrue();
        r.Lifetime.Should().Be(0);
        rig.Core.AllocationCount.Should().Be(0);
        rig.Relays.Created[0].Disposed.Should().BeTrue();
        c.Refresh(null).Code.Should().Be(437);
    }

    [Fact]
    public void Allocation_expires_on_tick_and_releases_its_relay()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        rig.Time.Advance(TimeSpan.FromSeconds(601));
        rig.Core.Tick();
        rig.Core.AllocationCount.Should().Be(0);
        rig.Relays.Created[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public void Requests_on_an_allocation_by_another_user_are_441()
    {
        var rig = new Rig();
        var c = rig.NewClient("alice");
        c.Allocate();
        (c.Username, c.Password) = TurnRestCredentials.Issue(Rig.Secret, "mallory", rig.Time.GetUtcNow().AddHours(1));
        c.Refresh(null).Code.Should().Be(441);
        c.Permit("10.79.0.20").Code.Should().Be(441);
    }

    [Fact]
    public void Refresh_with_other_address_family_is_443()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Request(StunMethod.Refresh, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedAddressFamily, 0x02000000)).Code.Should().Be(443);
    }

    [Fact]
    public void Requests_without_an_allocation_are_437()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Permit("10.79.0.20").Code.Should().Be(437);
        c.Bind(0x4000, "10.79.0.20", 9).Code.Should().Be(437);
    }

    [Fact]
    public void Closing_a_stream_connection_deletes_its_allocation()
    {
        var rig = new Rig();
        var c = rig.NewClient(transport: TurnTransport.Tcp);
        c.Allocate();
        rig.Core.OnClientDisconnected(c.Tuple);
        rig.Core.AllocationCount.Should().Be(0);
    }
}

public class PermissionAndDataTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("10.79.0.20"), 3480);

    [Fact]
    public void Send_indication_reaches_the_peer_only_with_a_permission()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        var relay = rig.Relays.Created[0];
        c.SendIndication("10.79.0.20", 3480, [1, 2, 3]);
        relay.Sent.Should().BeEmpty();

        c.Permit("10.79.0.20").IsSuccess.Should().BeTrue();
        c.SendIndication("10.79.0.20", 3480, [1, 2, 3]);
        relay.Sent.Should().ContainSingle();
        relay.Sent[0].Peer.Should().Be(Peer);
        relay.Sent[0].Data.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Peer_data_becomes_a_data_indication_only_with_a_permission()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        var relay = rig.Relays.Created[0];
        rig.Sink.Sent.Clear();
        rig.Core.OnPeerData(relay, Peer, [9, 9]);
        rig.Sink.Sent.Should().BeEmpty();

        c.Permit("10.79.0.20");
        rig.Core.OnPeerData(relay, Peer, [9, 9]);
        var r = new Response(rig.Sink.Last);
        r.Class.Should().Be(StunClass.Indication);
        r.Method.Should().Be(StunMethod.Data);
        r.Peer.Should().Be(Peer);
        r.Data.Should().Equal(9, 9);
    }

    [Fact]
    public void Permission_expires_after_300_seconds()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Permit("10.79.0.20");
        rig.Time.Advance(TimeSpan.FromSeconds(301));
        rig.Core.Tick();
        rig.Sink.Sent.Clear();
        rig.Core.OnPeerData(rig.Relays.Created[0], Peer, [1]);
        rig.Sink.Sent.Should().BeEmpty();
    }

    [Fact]
    public void Denied_peer_is_403_and_installs_nothing_even_with_an_allowed_one_in_the_same_request()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Permit("10.79.0.20", "127.0.0.1").Code.Should().Be(403);
        c.SendIndication("10.79.0.20", 3480, [1]);
        rig.Relays.Created[0].Sent.Should().BeEmpty();
    }

    [Fact]
    public void Create_permission_without_peer_is_400()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Permit().Code.Should().Be(400);
    }

    [Fact]
    public void Bandwidth_limit_drops_excess()
    {
        var rig = new Rig(o =>
        {
            o.BandwidthBytesPerSecond = 1000;
            o.BandwidthBurstBytes = 1000;
        });
        var c = rig.NewClient();
        c.Allocate();
        c.Permit("10.79.0.20");
        for (var i = 0; i < 5; i++)
        {
            c.SendIndication("10.79.0.20", 3480, new byte[400]);
        }

        rig.Relays.Created[0].Sent.Should().HaveCount(2);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        c.SendIndication("10.79.0.20", 3480, new byte[400]);
        rig.Relays.Created[0].Sent.Should().HaveCount(3);
    }

    [Fact]
    public void Oversized_peer_datagram_is_dropped()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Permit("10.79.0.20");
        rig.Sink.Sent.Clear();
        rig.Core.OnPeerData(rig.Relays.Created[0], Peer, new byte[rig.Options.MaxPeerPayload + 1]);
        rig.Sink.Sent.Should().BeEmpty();
    }
}

public class ChannelTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("10.79.0.20"), 3480);

    [Fact]
    public void Channel_data_flows_both_ways_and_binding_installs_a_permission()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Bind(0x4001, "10.79.0.20", 3480).IsSuccess.Should().BeTrue();

        c.Raw([0x40, 0x01, 0x00, 0x03, 7, 8, 9, 0]); // UDP may carry padding
        rig.Relays.Created[0].Sent.Should().ContainSingle();
        rig.Relays.Created[0].Sent[0].Data.Should().Equal(7, 8, 9);

        rig.Sink.Sent.Clear();
        rig.Core.OnPeerData(rig.Relays.Created[0], Peer, [5, 6, 7]);
        rig.Sink.Last.Should().Equal(0x40, 0x01, 0x00, 0x03, 5, 6, 7); // UDP: no padding
    }

    [Fact]
    public void Channel_data_to_a_stream_client_is_padded()
    {
        var rig = new Rig();
        var c = rig.NewClient(transport: TurnTransport.Tls);
        c.Allocate();
        c.Bind(0x4001, "10.79.0.20", 3480);
        rig.Sink.Sent.Clear();
        rig.Core.OnPeerData(rig.Relays.Created[0], Peer, [5, 6, 7]);
        rig.Sink.Last.Should().Equal(0x40, 0x01, 0x00, 0x03, 5, 6, 7, 0);
    }

    [Theory]
    [InlineData(0x3FFF)]
    [InlineData(0x5000)]
    public void Channel_number_out_of_range_is_400(int number)
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Bind((ushort)number, "10.79.0.20", 3480).Code.Should().Be(400);
    }

    [Fact]
    public void Channel_and_peer_are_one_to_one()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Bind(0x4001, "10.79.0.20", 3480).IsSuccess.Should().BeTrue();
        c.Bind(0x4001, "10.79.0.20", 3480).IsSuccess.Should().BeTrue(); // refresh
        c.Bind(0x4001, "10.79.0.21", 3480).Code.Should().Be(400);
        c.Bind(0x4002, "10.79.0.20", 3480).Code.Should().Be(400);
        c.Bind(0x4002, "10.79.0.20", 3481).IsSuccess.Should().BeTrue(); // other port = other peer
    }

    [Fact]
    public void Expired_binding_is_reserved_for_five_minutes()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate((ref StunWriter w) => w.AddUInt32(StunAttr.Lifetime, 3600));
        c.Bind(0x4001, "10.79.0.20", 3480);
        rig.Time.Advance(TimeSpan.FromSeconds(601));
        rig.Core.Tick();
        c.Raw([0x40, 0x01, 0x00, 0x01, 1, 0, 0, 0]);
        rig.Relays.Created[0].Sent.Should().BeEmpty();
        c.Bind(0x4001, "10.79.0.21", 3480).Code.Should().Be(400);
        c.Bind(0x4002, "10.79.0.20", 3480).Code.Should().Be(400);
        c.Bind(0x4001, "10.79.0.20", 3480).IsSuccess.Should().BeTrue(); // same pairing may come back
        rig.Time.Advance(TimeSpan.FromSeconds(601 + 301));
        rig.Core.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(301));
        rig.Core.Tick();
        c.Refresh(null);
        c.Bind(0x4001, "10.79.0.21", 3480).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Channel_to_denied_peer_is_403()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Bind(0x4001, "169.254.169.254", 80).Code.Should().Be(403);
    }

    [Fact]
    public void Channel_data_for_unbound_channel_or_short_frame_is_dropped()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Bind(0x4001, "10.79.0.20", 3480);
        c.Raw([0x40, 0x02, 0x00, 0x01, 1]);
        c.Raw([0x40, 0x01, 0x00, 0x09, 1, 2, 3, 4]);
        rig.Relays.Created[0].Sent.Should().BeEmpty();
    }
}

public class RobustnessTests
{
    [Fact]
    public void Random_mutations_of_valid_requests_never_throw()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        c.Allocate();
        c.Permit("10.79.0.20");
        var seeds = new List<byte[]>();
        foreach (var method in new[] { StunMethod.Allocate, StunMethod.Refresh, StunMethod.CreatePermission, StunMethod.ChannelBind })
        {
            var buf = new byte[512];
            var w = new StunWriter(buf, method, StunClass.Request, Client.NewTid());
            w.AddUInt32(StunAttr.RequestedTransport, 17u << 24);
            w.AddUInt32(StunAttr.ChannelNumber, 0x40010000);
            w.AddXorAddress(StunAttr.XorPeerAddress, IPAddress.Parse("10.79.0.20"), 3480);
            w.AddString(StunAttr.Username, c.Username);
            w.AddString(StunAttr.Realm, "bluechat.test");
            w.AddString(StunAttr.Nonce, "AAAAAAAAAAAAAAAAAAAAAAAAAAA");
            w.AddMessageIntegrity(c.Key);
            w.AddFingerprint();
            seeds.Add(w.Written.ToArray());
        }

        seeds.Add([0x40, 0x01, 0x00, 0x04, 1, 2, 3, 4]);
        var rng = new Random(8656);
        for (var i = 0; i < 20000; i++)
        {
            var data = seeds[i % seeds.Count].ToArray();
            for (var f = 0; f < 1 + rng.Next(4); f++)
            {
                data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            }

            if (rng.Next(3) == 0)
            {
                data = data.AsSpan(0, rng.Next(data.Length)).ToArray();
            }

            rig.Core.OnClientData(c.Tuple, data);
            rig.Core.OnPeerData(rig.Relays.Created[0], new IPEndPoint(IPAddress.Parse("10.79.0.20"), 3480), data);
        }

        rig.Core.AllocationCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Oversized_client_message_is_ignored()
    {
        var rig = new Rig();
        var c = rig.NewClient();
        rig.Sink.Sent.Clear();
        var big = new byte[rig.Options.MaxMessageSize + 4];
        BinaryPrimitives.WriteUInt16BigEndian(big, 0x0001);
        c.Raw(big);
        rig.Sink.Sent.Should().BeEmpty();
    }
}
