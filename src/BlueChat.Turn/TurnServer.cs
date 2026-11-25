using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace BlueChat.Turn;

/// <summary>Socket endpoints of a <see cref="TurnServer"/>. Null disables a transport.</summary>
public sealed class TurnListenOptions
{
    public IPEndPoint? Udp { get; set; } = new(IPAddress.Any, 3478);

    public IPEndPoint? Tcp { get; set; } = new(IPAddress.Any, 3478);

    /// <summary>TURN over TLS (RFC 8656 §3.1); 443 on a dedicated address in production (docs/00 Z2), 5349 by default.</summary>
    public IPEndPoint? Tls { get; set; }

    public X509Certificate2? TlsCertificate { get; set; }

    /// <summary>Address relay sockets bind to.</summary>
    public IPAddress RelayBindAddress { get; set; } = IPAddress.Any;

    public int RelayPortMin { get; set; } = 49152;

    public int RelayPortMax { get; set; } = 65535;

    public int MaxStreamConnections { get; set; } = 10_000;

    public int MaxStreamConnectionsPerIp { get; set; } = 100;

    public TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Socket host around <see cref="TurnCore"/>: UDP, TCP and TLS listeners, one UDP socket per relayed address,
/// a 1-second tick for expiry. Writes to stream clients go through a bounded queue per connection so that a slow
/// client cannot stall the core.
/// </summary>
public sealed class TurnServer : IAsyncDisposable
{
    private readonly TurnListenOptions _listen;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly ConcurrentDictionary<FiveTuple, Channel<byte[]>> _streams = new();
    private readonly ConcurrentDictionary<IPAddress, int> _streamsPerIp = new();
    private readonly UdpRelayFactory _relays;
    private readonly Sink _sink;
    private Socket? _udp;
    private TcpListener? _tcp;
    private TcpListener? _tls;

    public TurnServer(TurnOptions options, TurnListenOptions listen, TimeProvider time, Action<string>? log = null)
    {
        _listen = listen;
        _time = time;
        _log = log ?? (_ => { });
        _relays = new UdpRelayFactory(listen.RelayBindAddress, listen.RelayPortMin, listen.RelayPortMax, _stop.Token);
        _sink = new Sink(this);
        Core = new TurnCore(options, time, _relays, _sink);
        _relays.OnPeerData = Core.OnPeerData;
    }

    public TurnCore Core { get; }

    public IPEndPoint? UdpEndPoint => (IPEndPoint?)_udp?.LocalEndPoint;

    public IPEndPoint? TcpEndPoint => (IPEndPoint?)_tcp?.LocalEndpoint;

    public IPEndPoint? TlsEndPoint => (IPEndPoint?)_tls?.LocalEndpoint;

    public void Start()
    {
        if (_listen.Udp is { } udpEp)
        {
            _udp = new Socket(udpEp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _udp.Bind(udpEp);
            _loops.Add(Task.Run(UdpLoopAsync));
        }

        if (_listen.Tcp is { } tcpEp)
        {
            _tcp = new TcpListener(tcpEp);
            _tcp.Start(512);
            _loops.Add(Task.Run(() => AcceptLoopAsync(_tcp, TurnTransport.Tcp)));
        }

        if (_listen.Tls is { } tlsEp)
        {
            if (_listen.TlsCertificate is null)
            {
                throw new InvalidOperationException("TLS listener needs a certificate");
            }

            _tls = new TcpListener(tlsEp);
            _tls.Start(512);
            _loops.Add(Task.Run(() => AcceptLoopAsync(_tls, TurnTransport.Tls)));
        }

        _loops.Add(Task.Run(TickLoopAsync));
    }

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[65536];
        var local = (IPEndPoint)_udp!.LocalEndPoint!;
        var any = new IPEndPoint(local.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // ICMP port unreachable surfaces here on Windows
            }

            var tuple = new FiveTuple(TurnTransport.Udp, (IPEndPoint)r.RemoteEndPoint, local);
            Core.OnClientData(tuple, buffer.AsSpan(0, r.ReceivedBytes));
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, TurnTransport transport)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => ServeStreamAsync(client, transport));
        }
    }

    private async Task ServeStreamAsync(TcpClient client, TurnTransport transport)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var ip = remote.Address;
        if (_streams.Count >= _listen.MaxStreamConnections
            || _streamsPerIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > _listen.MaxStreamConnectionsPerIp)
        {
            Release(ip);
            client.Dispose();
            return;
        }

        var tuple = new FiveTuple(transport, remote, local);
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Stream? stream = null;
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();
            if (transport == TurnTransport.Tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = ssl;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                handshake.CancelAfter(_listen.TlsHandshakeTimeout);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _listen.TlsCertificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, handshake.Token).ConfigureAwait(false);
            }

            _streams[tuple] = queue;
            var writer = WriteLoopAsync(stream, queue.Reader, cts.Token);
            var buffer = new byte[Math.Max(4096, 4 + 65535 + 3)];
            while (!cts.IsCancellationRequested)
            {
                var n = await StreamFraming.ReadMessageAsync(stream, buffer, buffer.Length, cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                Core.OnClientData(tuple, buffer.AsSpan(0, n));
            }

            await cts.CancelAsync().ConfigureAwait(false);
            await writer.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException
                                      or OperationCanceledException or SocketException or EndOfStreamException)
        {
            _log($"{transport} {remote}: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            _streams.TryRemove(tuple, out _);
            Core.OnClientDisconnected(tuple);
            queue.Writer.TryComplete();
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            client.Dispose();
            Release(ip);
        }
    }

    private static async Task WriteLoopAsync(Stream stream, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var data in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // connection is going away; the read loop cleans up
        }
    }

    private void Release(IPAddress ip)
    {
        if (_streamsPerIp.AddOrUpdate(ip, 0, (_, n) => n - 1) <= 0)
        {
            _streamsPerIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }

    private async Task TickLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Core.Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _udp?.Dispose();
        _tcp?.Stop();
        _tls?.Stop();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Core.Dispose();
        _stop.Dispose();
    }

    /// <summary>Self-signed ECDSA P-256 certificate for TURN over TLS in development and tests.</summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string subject, TimeProvider time)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + subject, key, HashAlgorithmName.SHA256);
        var now = time.GetUtcNow();
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        // Round-trip through PKCS#12 so SslStream on Windows can use the private key.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private sealed class Sink(TurnServer server) : ITurnSink
    {
        public void SendToClient(in FiveTuple tuple, ReadOnlySpan<byte> data)
        {
            if (tuple.Transport == TurnTransport.Udp)
            {
                try
                {
                    server._udp?.SendTo(data, SocketFlags.None, tuple.Client);
                }
                catch (SocketException)
                {
                }

                return;
            }

            if (server._streams.TryGetValue(tuple, out var queue))
            {
                queue.Writer.TryWrite(data.ToArray()); // full queue → dropped (media is loss tolerant)
            }
        }
    }
}

/// <summary>UDP relay sockets in a configured port range (RFC 8656 §7.2: any port; EVEN-PORT honoured).</summary>
internal sealed class UdpRelayFactory(IPAddress bindAddress, int portMin, int portMax, CancellationToken stop) : IRelayFactory
{
    private const int Attempts = 64;

    public PeerDataHandler? OnPeerData { get; set; }

    public bool Supports(AddressFamily family) =>
        family == bindAddress.AddressFamily
        || (family == AddressFamily.InterNetwork && bindAddress.Equals(IPAddress.IPv6Any));

    public IRelay? Create(AddressFamily family, bool evenPort)
    {
        var address = family == AddressFamily.InterNetwork && bindAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.Any
            : bindAddress;
        for (var i = 0; i < Attempts; i++)
        {
            var port = Random.Shared.Next(portMin, portMax + 1);
            if (evenPort)
            {
                port &= ~1;
                if (port < portMin)
                {
                    continue;
                }
            }

            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(address, port));
            }
            catch (SocketException)
            {
                socket.Dispose();
                continue;
            }

            var relay = new UdpRelay(socket, this);
            relay.Start(stop);
            return relay;
        }

        return null;
    }

    private sealed class UdpRelay(Socket socket, UdpRelayFactory owner) : IRelay
    {
        private readonly CancellationTokenSource _cts = new();

        public IPEndPoint LocalEndPoint { get; } = (IPEndPoint)socket.LocalEndPoint!;

        public void Start(CancellationToken stop)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _cts.Token);
            _ = Task.Run(async () =>
            {
                using (linked)
                {
                    var buffer = new byte[65536];
                    var any = new IPEndPoint(LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    while (!linked.IsCancellationRequested)
                    {
                        SocketReceiveFromResult r;
                        try
                        {
                            r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, linked.Token).ConfigureAwait(false);
                        }
                        catch (SocketException)
                        {
                            continue;
                        }
                        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
                        {
                            return;
                        }

                        owner.OnPeerData?.Invoke(this, (IPEndPoint)r.RemoteEndPoint, buffer.AsSpan(0, r.ReceivedBytes));
                    }
                }
            }, CancellationToken.None);
        }

        public void Send(IPEndPoint peer, ReadOnlySpan<byte> data)
        {
            try
            {
                socket.SendTo(data, SocketFlags.None, peer);
            }
            catch (SocketException)
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            socket.Dispose();
        }
    }
}
