using System.Net;
using System.Net.Sockets;
using BlueChat.Media.Stun;
using BlueChat.Turn;

namespace BlueChat.Turn.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeRelay(IPEndPoint local) : IRelay
{
    public IPEndPoint LocalEndPoint { get; } = local;

    public List<(IPEndPoint Peer, byte[] Data)> Sent { get; } = [];

    public bool Disposed { get; private set; }

    public void Send(IPEndPoint peer, ReadOnlySpan<byte> data) => Sent.Add((peer, data.ToArray()));

    public void Dispose() => Disposed = true;
}

internal sealed class FakeRelayFactory : IRelayFactory
{
    private int _port = 50000;

    public List<FakeRelay> Created { get; } = [];

    public int Capacity { get; set; } = int.MaxValue;

    public bool SupportsIPv6 { get; set; }

    public bool Supports(AddressFamily family) => family == AddressFamily.InterNetwork || SupportsIPv6;

    public IRelay? Create(AddressFamily family, bool evenPort)
    {
        if (Created.Count(r => !r.Disposed) >= Capacity)
        {
            return null;
        }

        _port += evenPort && _port % 2 == 1 ? 1 : 0;
        var address = family == AddressFamily.InterNetwork ? IPAddress.Parse("10.79.0.10") : IPAddress.Parse("2001:db8::10");
        var relay = new FakeRelay(new IPEndPoint(address, _port++));
        Created.Add(relay);
        return relay;
    }
}

internal sealed class CaptureSink : ITurnSink
{
    public List<(FiveTuple Tuple, byte[] Data)> Sent { get; } = [];

    public void SendToClient(in FiveTuple tuple, ReadOnlySpan<byte> data) => Sent.Add((tuple, data.ToArray()));

    public byte[] Last => Sent[^1].Data;

    public byte[] TakeLast()
    {
        var d = Sent[^1].Data;
        Sent.Clear();
        return d;
    }
}

/// <summary>A TURN client written against the STUN library, driving <see cref="TurnCore"/> directly.</summary>
internal sealed class Rig
{
    public static readonly byte[] Secret = "test-secret"u8.ToArray();
    public static readonly IPEndPoint ServerEp = new(IPAddress.Parse("10.79.0.10"), 3478);

    public Rig(Action<TurnOptions>? configure = null)
    {
        Time = new ManualTime(DateTimeOffset.Parse("2026-11-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Options = new TurnOptions
        {
            Realm = "bluechat.test",
            SharedSecret = Secret,
            PeerPolicy = PeerAddressPolicy.AllowList(IPNetwork.Parse("10.79.0.0/24"), IPNetwork.Parse("203.0.113.0/24")),
        };
        configure?.Invoke(Options);
        Core = new TurnCore(Options, Time, Relays, Sink);
    }

    public ManualTime Time { get; }

    public TurnOptions Options { get; }

    public FakeRelayFactory Relays { get; } = new();

    public CaptureSink Sink { get; } = new();

    public TurnCore Core { get; }

    public Client NewClient(string user = "alice", string clientIp = "198.51.100.7", int port = 40000,
        TurnTransport transport = TurnTransport.Udp) =>
        new(this, user, new FiveTuple(transport, new IPEndPoint(IPAddress.Parse(clientIp), port), ServerEp));
}

internal sealed class Client
{
    private readonly Rig _rig;
    private string? _nonce;

    public Client(Rig rig, string user, FiveTuple tuple)
    {
        _rig = rig;
        Tuple = tuple;
        (Username, Password) = TurnRestCredentials.Issue(Rig.Secret, user, rig.Time.GetUtcNow().AddHours(1));
    }

    public FiveTuple Tuple { get; }

    public string Username { get; set; }

    public string Password { get; set; }

    public byte[] Key => StunCredentials.LongTermKeyMd5(Username, _rig.Options.Realm, Password);

    public delegate void Attrs(ref StunWriter w);

    /// <summary>Sends an authenticated request (fetching a nonce first if needed); returns the response bytes.</summary>
    public Response Request(ushort method, Attrs? attrs = null, byte[]? transactionId = null, bool sign = true)
    {
        if (sign && _nonce is null)
        {
            var challenge = Send(method, attrs, transactionId, sign: false);
            challenge.Code.Should().Be(401);
            _nonce = challenge.Nonce;
        }

        var r = Send(method, attrs, transactionId, sign);
        if (r.Code == 438)
        {
            _nonce = r.Nonce;
            r = Send(method, attrs, transactionId, sign);
        }

        return r;
    }

    public Response Send(ushort method, Attrs? attrs, byte[]? transactionId, bool sign)
    {
        var buf = new byte[2048];
        var tid = transactionId ?? NewTid();
        var w = new StunWriter(buf, method, StunClass.Request, tid);
        attrs?.Invoke(ref w);
        if (sign)
        {
            w.AddString(StunAttr.Username, Username);
            w.AddString(StunAttr.Realm, _rig.Options.Realm);
            w.AddString(StunAttr.Nonce, _nonce!);
            w.AddMessageIntegrity(Key);
        }

        w.AddFingerprint();
        _rig.Sink.Sent.Clear();
        _rig.Core.OnClientData(Tuple, w.Written);
        return _rig.Sink.Sent.Count == 0 ? Response.None : new Response(_rig.Sink.TakeLast());
    }

    public void Raw(ReadOnlySpan<byte> data) => _rig.Core.OnClientData(Tuple, data);

    public Response Allocate(Attrs? extra = null) => Request(StunMethod.Allocate, (ref StunWriter w) =>
    {
        w.AddUInt32(StunAttr.RequestedTransport, 17u << 24);
        extra?.Invoke(ref w);
    });

    public Response Permit(params string[] peers) => Request(StunMethod.CreatePermission, (ref StunWriter w) =>
    {
        foreach (var p in peers)
        {
            w.AddXorAddress(StunAttr.XorPeerAddress, IPAddress.Parse(p), 9);
        }
    });

    public Response Bind(ushort channel, string peer, int port) => Request(StunMethod.ChannelBind, (ref StunWriter w) =>
    {
        w.AddUInt32(StunAttr.ChannelNumber, (uint)channel << 16);
        w.AddXorAddress(StunAttr.XorPeerAddress, IPAddress.Parse(peer), port);
    });

    public Response Refresh(uint? lifetime) => Request(StunMethod.Refresh, (ref StunWriter w) =>
    {
        if (lifetime is { } l)
        {
            w.AddUInt32(StunAttr.Lifetime, l);
        }
    });

    public void SendIndication(string peer, int port, byte[] data)
    {
        var buf = new byte[2048];
        var w = new StunWriter(buf, StunMethod.Send, StunClass.Indication, NewTid());
        w.AddXorAddress(StunAttr.XorPeerAddress, IPAddress.Parse(peer), port);
        w.Add(StunAttr.Data, data);
        _rig.Core.OnClientData(Tuple, w.Written);
    }

    public static byte[] NewTid()
    {
        var t = new byte[12];
        StunWriter.NewTransactionId(t);
        return t;
    }
}

/// <summary>Parsed copy of a response (StunMessage is a ref struct, so fields are extracted eagerly).</summary>
internal sealed class Response
{
    public static readonly Response None = new();

    private Response()
    {
        Bytes = [];
    }

    public Response(byte[] bytes)
    {
        Bytes = bytes;
        StunMessage.TryParse(bytes, out var m).Should().Be(StunParseError.None);
        Class = m.Class;
        Method = m.Method;
        if (m.TryGetErrorCode(out var code, out _))
        {
            Code = code;
        }

        Nonce = m.GetString(StunAttr.Nonce);
        Realm = m.GetString(StunAttr.Realm);
        if (m.TryGetUInt32(StunAttr.Lifetime, out var lifetime))
        {
            Lifetime = lifetime;
        }

        if (m.TryGetXorAddress(StunAttr.XorRelayedAddress, out var relayed))
        {
            Relayed = relayed;
        }

        if (m.TryGetXorAddress(StunAttr.XorMappedAddress, out var mapped))
        {
            Mapped = mapped;
        }

        if (m.TryGetXorAddress(StunAttr.XorPeerAddress, out var peer))
        {
            Peer = peer;
        }

        if (m.TryGetAttribute(StunAttr.Data, out var data))
        {
            Data = data.ToArray();
        }

        if (m.TryGetAttribute(StunAttr.UnknownAttributes, out var unknownSpan))
        {
            var unknown = unknownSpan.ToArray();
            Unknown = Enumerable.Range(0, unknown.Length / 2)
                .Select(i => (ushort)((unknown[2 * i] << 8) | unknown[(2 * i) + 1])).ToArray();
        }

        HasIntegrity = m.Has(StunAttr.MessageIntegrity);
        FingerprintOk = StunIntegrity.VerifyFingerprint(m) == IntegrityResult.Ok;
    }

    public byte[] Bytes { get; }

    public StunClass Class { get; }

    public ushort Method { get; }

    public int Code { get; }

    public string? Nonce { get; }

    public string? Realm { get; }

    public uint? Lifetime { get; }

    public IPEndPoint? Relayed { get; }

    public IPEndPoint? Mapped { get; }

    public IPEndPoint? Peer { get; }

    public byte[]? Data { get; }

    public ushort[] Unknown { get; } = [];

    public bool HasIntegrity { get; }

    public bool FingerprintOk { get; }

    public bool IsSuccess => Class == StunClass.SuccessResponse;

    public bool VerifiesWith(byte[] key)
    {
        StunMessage.TryParse(Bytes, out var m);
        return StunIntegrity.VerifySha1(m, key) == IntegrityResult.Ok;
    }
}
