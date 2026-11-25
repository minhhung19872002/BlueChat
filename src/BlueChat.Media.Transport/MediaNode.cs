using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using BlueChat.Media.Dtls;
using BlueChat.Media.Ice;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Transport;

public sealed record MediaNodeOptions
{
    /// <summary>Address written into host candidates (the node's public IP).</summary>
    public required IPAddress AdvertisedAddress { get; init; }

    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>The one UDP port shared by every session (spec §2.3).</summary>
    public int UdpPort { get; init; } = 50000;

    /// <summary>Passive ICE-TCP port (RFC 6544); 0 disables ICE-TCP.</summary>
    public int TcpPort { get; init; } = 50000;

    public int Mtu { get; init; } = 1200;

    /// <summary>Largest RFC 4571 frame accepted on ICE-TCP.</summary>
    public int MaxTcpFrame { get; init; } = 4096;

    public int MaxTcpConnections { get; init; } = 10_000;

    public TimeSpan CertificateLifetime { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Socket side of a media node: one shared UDP port and one passive TCP port, routing every packet through the
/// shared-port mux to its <see cref="PeerTransport"/> (RFC 7983 demux, ICE-lite, DTLS, SRTP).
/// </summary>
public sealed class MediaNode : IAsyncDisposable
{
    private const int MaxDatagram = 2048;

    private readonly SharedPortMux<PeerTransport> _mux = new();
    private readonly ConcurrentDictionary<PeerTransport, byte> _transports = new();
    private readonly ConcurrentDictionary<long, TcpConnection> _tcp = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _clock;
    private readonly Socket _udp;
    private readonly TcpListener? _tcpListener;
    private readonly List<Task> _loops = [];
    private long _nextConnectionId;
    private long _tcpAccepted;
    private long _tcpFrames;
    private long _tcpClosedByProtocolError;
    private long _stunUnrouted;

    public MediaNode(MediaNodeOptions options, TimeProvider clock)
    {
        Options = options;
        _clock = clock;
        Certificate = DtlsCertificate.Generate(clock, options.CertificateLifetime);
        Dtls = new DtlsContext(Certificate);
        _udp = new Socket(options.BindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // SIO_UDP_CONNRESET off: an ICMP port-unreachable from one peer must not fail the shared socket's receives.
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            _udp.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        _udp.Bind(new IPEndPoint(options.BindAddress, options.UdpPort));
        if (options.TcpPort > 0)
        {
            _tcpListener = new TcpListener(options.BindAddress, options.TcpPort);
        }
    }

    public MediaNodeOptions Options { get; }

    public DtlsCertificate Certificate { get; }

    internal DtlsContext Dtls { get; }

    public int UdpPort => ((IPEndPoint)_udp.LocalEndPoint!).Port;

    public int TcpPort => ((IPEndPoint?)_tcpListener?.LocalEndpoint)?.Port ?? 0;

    public int TransportCount => _transports.Count;

    /// <summary>Counters for operations and diagnostics.</summary>
    public MediaNodeCounters Counters => new(
        Interlocked.Read(ref _tcpAccepted), _tcp.Count, Interlocked.Read(ref _tcpFrames),
        Interlocked.Read(ref _tcpClosedByProtocolError), Interlocked.Read(ref _stunUnrouted));

    /// <summary>Host candidate lines for the answer: UDP, then passive TCP (RFC 6544).</summary>
    public IReadOnlyList<string> Candidates
    {
        get
        {
            var list = new List<string> { IceCandidates.HostUdp("1", Options.AdvertisedAddress, UdpPort) };
            if (_tcpListener is not null)
            {
                list.Add(IceCandidates.HostTcpPassive("2", Options.AdvertisedAddress, TcpPort));
            }

            return list;
        }
    }

    public void Start()
    {
        _tcpListener?.Start(512);
        _loops.Add(Task.Run(UdpLoop));
        if (_tcpListener is not null)
        {
            _loops.Add(Task.Run(AcceptLoop));
        }

        _loops.Add(Task.Run(TimerLoop));
    }

    /// <summary>Creates a transport for a peer whose offer carried these ICE credentials and DTLS fingerprint.</summary>
    public PeerTransport CreateTransport(IceCredentials remoteIce, string remoteFingerprint)
    {
        var t = new PeerTransport(this, remoteIce, remoteFingerprint, _clock);
        _transports[t] = 0;
        RegisterIce(t);
        return t;
    }

    internal void RegisterIce(PeerTransport t) => _mux.Register(t, t.Ice);

    internal void Remove(PeerTransport t)
    {
        _transports.TryRemove(t, out _);
        _mux.Unregister(t);
    }

    internal void SendTo(IceEndpoint to, ReadOnlySpan<byte> packet)
    {
        try
        {
            if (to.Transport == IceTransport.Udp)
            {
                _udp.SendTo(packet, SocketFlags.None, to.Remote);
            }
            else if (_tcp.TryGetValue(to.ConnectionId, out var c))
            {
                c.Send(packet);
            }
        }
        catch (SocketException)
        {
            // Unreachable peer or closed connection: consent freshness handles the transport's fate.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task UdpLoop()
    {
        var buffer = GC.AllocateUninitializedArray<byte>(MaxDatagram, pinned: true);
        var scratch = new byte[MaxDatagram];
        EndPoint any = new IPEndPoint(Options.BindAddress.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // ICMP port unreachable surfaces here on Windows
            }

            var from = new IceEndpoint(IceTransport.Udp, (IPEndPoint)r.RemoteEndPoint);
            Dispatch(buffer.AsSpan(0, r.ReceivedBytes), from, scratch);
        }
    }

    /// <summary>RFC 7983 §7 demux and routing; everything unauthenticated from an unknown address is dropped.</summary>
    private void Dispatch(Span<byte> packet, IceEndpoint from, Span<byte> scratch)
    {
        switch (PacketClassifier.Classify(packet))
        {
            case PacketKind.Stun:
                if (StunMessage.TryParse(packet, out var message) != StunParseError.None)
                {
                    return;
                }

                var known = _mux.Lookup(from);
                if (known is not null && known.Ice.Local.Ufrag == IceLiteAgent.LocalUfragOf(message))
                {
                    known.HandleStun(message, from, scratch);
                    return;
                }

                var session = _mux.RouteCheck(message, from, scratch, out var n, out var outcome);
                if (session is null)
                {
                    Interlocked.Increment(ref _stunUnrouted);
                }
                if (session is not null && outcome is CheckOutcome.Answered or CheckOutcome.Rejected)
                {
                    SendTo(from, scratch[..n]);
                }

                return;
            case PacketKind.Dtls:
                _mux.Lookup(from)?.HandleDtls(packet, from);
                return;
            case PacketKind.Rtp:
                _mux.Lookup(from)?.HandleSrtp(packet, rtcp: false);
                return;
            case PacketKind.Rtcp:
                _mux.Lookup(from)?.HandleSrtp(packet, rtcp: true);
                return;
            default:
                return;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket s;
            try
            {
                s = await _tcpListener!.AcceptSocketAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (_tcp.Count >= Options.MaxTcpConnections)
            {
                s.Dispose();
                continue;
            }

            s.NoDelay = true;
            Interlocked.Increment(ref _tcpAccepted);
            var id = Interlocked.Increment(ref _nextConnectionId);
            var connection = new TcpConnection(s);
            _tcp[id] = connection;
            _ = Task.Run(() => TcpLoop(id, connection));
        }
    }

    /// <summary>RFC 6544 §3 / RFC 4571 framed packets on one passive ICE-TCP connection.</summary>
    private async Task TcpLoop(long id, TcpConnection connection)
    {
        var from = new IceEndpoint(IceTransport.Tcp, (IPEndPoint)connection.Socket.RemoteEndPoint!, id);
        var reader = PipeReader.Create(new NetworkStream(connection.Socket, ownsSocket: false));
        var frame = new byte[Options.MaxTcpFrame];
        var scratch = new byte[MaxDatagram];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(_stop.Token);
                var buffer = result.Buffer;
                while (true)
                {
                    var r = Rfc4571Framer.TryRead(ref buffer, Options.MaxTcpFrame, out var f);
                    if (r == FrameResult.Invalid)
                    {
                        Interlocked.Increment(ref _tcpClosedByProtocolError);
                        return; // protocol error: close the connection
                    }

                    if (r == FrameResult.NeedMore)
                    {
                        break;
                    }

                    Interlocked.Increment(ref _tcpFrames);
                    var length = (int)f.Length;
                    f.CopyTo(frame);
                    Dispatch(frame.AsSpan(0, length), from, scratch);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
        }
        finally
        {
            await reader.CompleteAsync();
            _tcp.TryRemove(id, out _);
            _mux.Forget(from);
            connection.Dispose();
        }
    }

    private async Task TimerLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                foreach (var t in _transports.Keys)
                {
                    t.Tick();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _udp.Dispose();
        _tcpListener?.Stop();
        foreach (var c in _tcp.Values)
        {
            c.Dispose();
        }

        try
        {
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var t in _transports.Keys)
        {
            t.Dispose();
        }

        Dtls.Dispose();
        _stop.Dispose();
    }

    private sealed class TcpConnection(Socket socket) : IDisposable
    {
        private readonly object _write = new();

        public Socket Socket { get; } = socket;

        public void Send(ReadOnlySpan<byte> packet)
        {
            var frame = ArrayPool<byte>.Shared.Rent(packet.Length + 2);
            try
            {
                var n = Rfc4571Framer.Write(packet, frame);
                lock (_write)
                {
                    Socket.Send(frame.AsSpan(0, n));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }

        public void Dispose() => Socket.Dispose();
    }
}

public readonly record struct MediaNodeCounters(
    long TcpAccepted, int TcpOpen, long TcpFrames, long TcpClosedByProtocolError, long StunUnrouted);
