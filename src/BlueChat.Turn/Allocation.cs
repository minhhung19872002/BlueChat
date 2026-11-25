using System.Net;
using System.Net.Sockets;

namespace BlueChat.Turn;

public enum TurnTransport
{
    Udp,
    Tcp,
    Tls,
}

/// <summary>RFC 8656 §2: the 5-tuple (client address, server address, transport) that identifies an allocation.</summary>
public readonly record struct FiveTuple(TurnTransport Transport, IPEndPoint Client, IPEndPoint Server)
{
    /// <summary>ChannelData over TCP/TLS is padded to a multiple of 4 bytes (RFC 8656 §12.5).</summary>
    public bool IsStream => Transport != TurnTransport.Udp;
}

/// <summary>Where the protocol core sends bytes to clients. Implemented by the socket host (and by tests).</summary>
public interface ITurnSink
{
    void SendToClient(in FiveTuple tuple, ReadOnlySpan<byte> data);
}

/// <summary>One relayed transport address (RFC 8656 §2) — a UDP socket in the host, a fake in tests.</summary>
public interface IRelay : IDisposable
{
    IPEndPoint LocalEndPoint { get; }

    void Send(IPEndPoint peer, ReadOnlySpan<byte> data);
}

public delegate void PeerDataHandler(IRelay relay, IPEndPoint peer, ReadOnlySpan<byte> data);

public interface IRelayFactory
{
    bool Supports(AddressFamily family);

    /// <summary>A new relay socket, or null when no port is free (→ 508).</summary>
    IRelay? Create(AddressFamily family, bool evenPort);
}

/// <summary>State of one allocation (RFC 8656 §5). Owned and locked by <see cref="TurnCore"/>.</summary>
internal sealed class Allocation(FiveTuple tuple, string username, byte[] key, IRelay relay, IPEndPoint relayed,
    DateTimeOffset expires, TokenBucket bucket)
{
    public FiveTuple Tuple { get; } = tuple;

    public string Username { get; } = username;

    public byte[] Key { get; } = key;

    public IRelay Relay { get; } = relay;

    public IPEndPoint Relayed { get; } = relayed;

    public AddressFamily Family => Relayed.AddressFamily;

    public DateTimeOffset Expires { get; set; } = expires;

    public TokenBucket Bucket { get; } = bucket;

    /// <summary>RFC 8656 §9: permissions are per peer IP address (port ignored).</summary>
    public Dictionary<IPAddress, DateTimeOffset> Permissions { get; } = [];

    public Dictionary<ushort, ChannelBinding> ChannelsByNumber { get; } = [];

    public Dictionary<IPEndPoint, ushort> ChannelsByPeer { get; } = [];

    /// <summary>RFC 8656 §12: expired bindings, still reserved against a different pairing until the cooldown ends.</summary>
    public List<ChannelBinding> Cooldown { get; } = [];

    /// <summary>RFC 8656 §7.2: a retransmitted Allocate (same transaction id) gets the same success response.</summary>
    public byte[] AllocateTransactionId { get; set; } = [];

    public byte[] AllocateResponse { get; set; } = [];
}

internal sealed class ChannelBinding(ushort number, IPEndPoint peer, DateTimeOffset expires)
{
    public ushort Number { get; } = number;

    public IPEndPoint Peer { get; } = peer;

    public DateTimeOffset Expires { get; set; } = expires;
}
