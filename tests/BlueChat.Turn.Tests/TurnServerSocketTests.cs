using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using BlueChat.Media.Stun;
using BlueChat.Turn;

namespace BlueChat.Turn.Tests;

/// <summary>Real sockets on loopback: Allocate → CreatePermission/ChannelBind → data both ways, over UDP, TCP and TLS.</summary>
public sealed class TurnServerSocketTests : IAsyncLifetime
{
    private static readonly byte[] Secret = "socket-secret"u8.ToArray();
    private TurnServer _server = null!;

    public Task InitializeAsync()
    {
        var options = new TurnOptions
        {
            Realm = "bluechat.test",
            SharedSecret = Secret,
            PeerPolicy = PeerAddressPolicy.AllowList(["127.0.0.1/32"]),
        };
        var listen = new TurnListenOptions
        {
            Udp = new IPEndPoint(IPAddress.Loopback, 0),
            Tcp = new IPEndPoint(IPAddress.Loopback, 0),
            Tls = new IPEndPoint(IPAddress.Loopback, 0),
            TlsCertificate = TurnServer.CreateSelfSignedCertificate("turn.bluechat.test", TimeProvider.System),
            RelayBindAddress = IPAddress.Loopback,
            RelayPortMin = 40000,
            RelayPortMax = 60000,
        };
        _server = new TurnServer(options, listen, TimeProvider.System);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Theory]
    [InlineData(TurnTransport.Udp, false)]
    [InlineData(TurnTransport.Udp, true)]
    [InlineData(TurnTransport.Tcp, false)]
    [InlineData(TurnTransport.Tcp, true)]
    [InlineData(TurnTransport.Tls, false)]
    [InlineData(TurnTransport.Tls, true)]
    public async Task Relays_both_ways(TurnTransport transport, bool useChannel)
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEp = (IPEndPoint)peer.Client.LocalEndPoint!;
        await using var client = await WireClient.ConnectAsync(transport, _server);
        var (user, pass) = TurnRestCredentials.Issue(Secret, "alice", DateTimeOffset.UtcNow.AddHours(1));

        // Allocate: 401 → authenticated retry
        var challenge = await client.RequestAsync(StunMethod.Allocate, null, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24));
        challenge.Code.Should().Be(401);
        var key = StunCredentials.LongTermKeyMd5(user, "bluechat.test", pass);
        var auth = new Auth(user, "bluechat.test", challenge.Nonce!, key);
        var alloc = await client.RequestAsync(StunMethod.Allocate, auth, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24));
        alloc.IsSuccess.Should().BeTrue();
        alloc.VerifiesWith(key).Should().BeTrue();
        var relayed = alloc.Relayed!;

        if (useChannel)
        {
            (await client.RequestAsync(StunMethod.ChannelBind, auth, (ref StunWriter w) =>
            {
                w.AddUInt32(StunAttr.ChannelNumber, 0x40000000);
                w.AddXorAddress(StunAttr.XorPeerAddress, peerEp.Address, peerEp.Port);
            })).IsSuccess.Should().BeTrue();
            await client.SendChannelDataAsync(0x4000, [1, 2, 3, 4, 5]);
        }
        else
        {
            (await client.RequestAsync(StunMethod.CreatePermission, auth,
                (ref StunWriter w) => w.AddXorAddress(StunAttr.XorPeerAddress, peerEp.Address, peerEp.Port))).IsSuccess.Should().BeTrue();
            await client.SendIndicationAsync(peerEp, [1, 2, 3, 4, 5]);
        }

        var atPeer = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        atPeer.Buffer.Should().Equal(1, 2, 3, 4, 5);
        atPeer.RemoteEndPoint.Port.Should().Be(relayed.Port);

        await peer.SendAsync(new byte[] { 9, 8, 7 }, relayed);
        var back = await client.ReceiveAsync();
        if (useChannel)
        {
            BinaryPrimitives.ReadUInt16BigEndian(back).Should().Be(0x4000);
            back.AsSpan(4, 3).ToArray().Should().Equal(9, 8, 7);
        }
        else
        {
            var data = new Response(back);
            data.Method.Should().Be(StunMethod.Data);
            data.Data.Should().Equal(9, 8, 7);
            data.Peer.Should().Be(peerEp);
        }
    }

    [Fact]
    public async Task Peer_outside_the_allow_list_is_forbidden()
    {
        await using var client = await WireClient.ConnectAsync(TurnTransport.Udp, _server);
        var (user, pass) = TurnRestCredentials.Issue(Secret, "bob", DateTimeOffset.UtcNow.AddHours(1));
        var challenge = await client.RequestAsync(StunMethod.Allocate, null, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24));
        var auth = new Auth(user, "bluechat.test", challenge.Nonce!, StunCredentials.LongTermKeyMd5(user, "bluechat.test", pass));
        (await client.RequestAsync(StunMethod.Allocate, auth, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24))).IsSuccess.Should().BeTrue();
        (await client.RequestAsync(StunMethod.CreatePermission, auth,
            (ref StunWriter w) => w.AddXorAddress(StunAttr.XorPeerAddress, IPAddress.Parse("127.0.0.2"), 9))).Code.Should().Be(403);
    }

    [Fact]
    public async Task Closing_the_tcp_connection_deletes_the_allocation()
    {
        var client = await WireClient.ConnectAsync(TurnTransport.Tcp, _server);
        var (user, pass) = TurnRestCredentials.Issue(Secret, "carol", DateTimeOffset.UtcNow.AddHours(1));
        var challenge = await client.RequestAsync(StunMethod.Allocate, null, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24));
        var auth = new Auth(user, "bluechat.test", challenge.Nonce!, StunCredentials.LongTermKeyMd5(user, "bluechat.test", pass));
        (await client.RequestAsync(StunMethod.Allocate, auth, (ref StunWriter w) => w.AddUInt32(StunAttr.RequestedTransport, 17u << 24))).IsSuccess.Should().BeTrue();
        var before = _server.Core.AllocationCount;
        await client.DisposeAsync();
        for (var i = 0; i < 50 && _server.Core.AllocationCount >= before; i++)
        {
            await Task.Delay(50);
        }

        _server.Core.AllocationCount.Should().Be(before - 1);
    }

    [Fact]
    public async Task Garbage_on_a_stream_closes_the_connection()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_server.TcpEndPoint!);
        var s = tcp.GetStream();
        await s.WriteAsync(new byte[] { 0xC0, 0, 0, 0 });
        var buf = new byte[1];
        var n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        n.Should().Be(0);
    }

    internal sealed record Auth(string User, string Realm, string Nonce, byte[] Key);

    internal delegate void Attrs(ref StunWriter w);

    private sealed class WireClient : IAsyncDisposable
    {
        private readonly UdpClient? _udp;
        private readonly TcpClient? _tcp;
        private readonly Stream? _stream;
        private readonly IPEndPoint _server;
        private readonly byte[] _readBuffer = new byte[70000];

        private WireClient(UdpClient? udp, TcpClient? tcp, Stream? stream, IPEndPoint server)
        {
            _udp = udp;
            _tcp = tcp;
            _stream = stream;
            _server = server;
        }

        public static async Task<WireClient> ConnectAsync(TurnTransport transport, TurnServer server)
        {
            if (transport == TurnTransport.Udp)
            {
                return new WireClient(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)), null, null, server.UdpEndPoint!);
            }

            var ep = transport == TurnTransport.Tcp ? server.TcpEndPoint! : server.TlsEndPoint!;
            var tcp = new TcpClient();
            await tcp.ConnectAsync(ep);
            Stream stream = tcp.GetStream();
            if (transport == TurnTransport.Tls)
            {
#pragma warning disable CA5359 // test client trusts the self-signed test certificate
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);
#pragma warning restore CA5359
                await ssl.AuthenticateAsClientAsync("turn.bluechat.test");
                stream = ssl;
            }

            return new WireClient(null, tcp, stream, ep);
        }

        public async Task<Response> RequestAsync(ushort method, Auth? auth, Attrs attrs)
        {
            var bytes = Build(method, StunClass.Request, auth, attrs);
            await SendAsync(bytes);
            return new Response(await ReceiveAsync());
        }

        public Task SendIndicationAsync(IPEndPoint peer, byte[] data) =>
            SendAsync(Build(StunMethod.Send, StunClass.Indication, null, (ref StunWriter w) =>
            {
                w.AddXorAddress(StunAttr.XorPeerAddress, peer.Address, peer.Port);
                w.Add(StunAttr.Data, data);
            }));

        public Task SendChannelDataAsync(ushort channel, byte[] data)
        {
            var padded = _stream is null ? data.Length : (data.Length + 3) & ~3;
            var frame = new byte[4 + padded];
            BinaryPrimitives.WriteUInt16BigEndian(frame, channel);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)data.Length);
            data.CopyTo(frame, 4);
            return SendAsync(frame);
        }

        public async Task<byte[]> ReceiveAsync()
        {
            if (_udp is not null)
            {
                return (await _udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer;
            }

            var n = await StreamFraming.ReadMessageAsync(_stream!, _readBuffer, _readBuffer.Length, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return _readBuffer.AsSpan(0, n).ToArray();
        }

        private static byte[] Build(ushort method, StunClass cls, Auth? auth, Attrs attrs)
        {
            var buf = new byte[2048];
            var tid = new byte[12];
            StunWriter.NewTransactionId(tid);
            var w = new StunWriter(buf, method, cls, tid);
            attrs(ref w);
            if (auth is not null)
            {
                w.AddString(StunAttr.Username, auth.User);
                w.AddString(StunAttr.Realm, auth.Realm);
                w.AddString(StunAttr.Nonce, auth.Nonce);
                w.AddMessageIntegrity(auth.Key);
            }

            w.AddFingerprint();
            return w.Written.ToArray();
        }

        private async Task SendAsync(byte[] bytes)
        {
            if (_udp is not null)
            {
                await _udp.SendAsync(bytes, _server);
            }
            else
            {
                await _stream!.WriteAsync(bytes);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _udp?.Dispose();
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }

            _tcp?.Dispose();
        }
    }
}
