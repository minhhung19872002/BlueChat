// Runs BlueChat.Turn on real sockets.
// Usage: dotnet run --project tools/TurnRunner -- --listen 0.0.0.0 --relay-ip 10.79.0.10 --secret <s>
//          [--realm bluechat.test] [--udp 3478] [--tcp 3478] [--tls 5349] [--allow 10.79.0.20/32,...] [--deny-private]
//          [--ports 49152-49352]
using System.Globalization;
using System.Net;
using System.Text;
using BlueChat.Turn;

var opts = args.Select((v, i) => (v, i)).Where(x => x.v.StartsWith("--", StringComparison.Ordinal))
    .ToDictionary(x => x.v[2..], x => x.i + 1 < args.Length && !args[x.i + 1].StartsWith("--", StringComparison.Ordinal) ? args[x.i + 1] : "");
string Get(string name, string fallback) => opts.TryGetValue(name, out var v) && v.Length > 0 ? v : fallback;
int Port(string name, string fallback) => int.Parse(Get(name, fallback), CultureInfo.InvariantCulture);

var listen = IPAddress.Parse(Get("listen", "0.0.0.0"));
var relayIp = IPAddress.Parse(Get("relay-ip", "127.0.0.1"));
var secret = Get("secret", "");
if (secret.Length == 0)
{
    Console.Error.WriteLine("--secret is required");
    return 2;
}

var ports = Get("ports", "49152-49352").Split('-');
var options = new TurnOptions
{
    Realm = Get("realm", "bluechat.test"),
    SharedSecret = Encoding.UTF8.GetBytes(secret),
    RelayAdvertisedAddress = relayIp,
    PeerPolicy = opts.ContainsKey("deny-private")
        ? PeerAddressPolicy.DenyPrivate()
        : PeerAddressPolicy.AllowList(Get("allow", "").Split(',', StringSplitOptions.RemoveEmptyEntries)),
};
var listenOptions = new TurnListenOptions
{
    Udp = new IPEndPoint(listen, Port("udp", "3478")),
    Tcp = new IPEndPoint(listen, Port("tcp", "3478")),
    Tls = new IPEndPoint(listen, Port("tls", "5349")),
    TlsCertificate = TurnServer.CreateSelfSignedCertificate("turn.bluechat.test", TimeProvider.System),
    RelayBindAddress = relayIp,
    RelayPortMin = int.Parse(ports[0], CultureInfo.InvariantCulture),
    RelayPortMax = int.Parse(ports[1], CultureInfo.InvariantCulture),
};

await using var server = new TurnServer(options, listenOptions, TimeProvider.System, Console.WriteLine);
server.Start();
Console.WriteLine($"TURN ready udp={server.UdpEndPoint} tcp={server.TcpEndPoint} tls={server.TlsEndPoint} relay={relayIp} " +
                  $"policy={options.PeerPolicy.Mode} [{string.Join(", ", options.PeerPolicy.Allowed)}]");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException)
{
}

return 0;
