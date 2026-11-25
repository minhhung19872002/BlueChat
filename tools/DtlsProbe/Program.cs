// Phase 0 / Z3: a browser (Chrome, Firefox) connects to a single UDP port, ICE-lite answers STUN, OpenSSL (P/Invoke)
// completes DTLS 1.2 as server, exports EXTRACTOR-dtls_srtp, and one SRTP packet from the browser is opened with
// the derived keys. Usage: dotnet run --project tools/DtlsProbe -- [--ip <candidate ip>] [--udp 50000] [--http 5199] [--bind 127.0.0.1]
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BlueChat.Media.Dtls;
using DtlsProbe;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = args.Select((v, i) => (v, i)).Where(x => x.v.StartsWith("--", StringComparison.Ordinal))
    .ToDictionary(x => x.v[2..], x => x.i + 1 < args.Length ? args[x.i + 1] : "");
var udpPort = int.Parse(opts.GetValueOrDefault("udp", "50000"), CultureInfo.InvariantCulture);
var httpPort = int.Parse(opts.GetValueOrDefault("http", "5199"), CultureInfo.InvariantCulture);
var httpBind = opts.GetValueOrDefault("bind", "127.0.0.1");
var candidateIp = opts.TryGetValue("ip", out var ipArg) && ipArg.Length > 0 ? IPAddress.Parse(ipArg) : PrimaryAddress();

var certificate = DtlsCertificate.Generate(TimeProvider.System, TimeSpan.FromDays(30));
using var context = new DtlsContext(certificate);
var udp = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
Probe? current = null;
var log = new ConcurrentQueue<string>();
void Log(string s)
{
    log.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
    Console.WriteLine(s);
}

Log($"OpenSSL 0x{DtlsContext.OpenSslVersion:X8}, candidate {candidateIp}:{udpPort}, node fingerprint {certificate.Fingerprint}");

_ = Task.Run(async () =>
{
    while (true)
    {
        var r = await udp.ReceiveAsync();
        var p = current;
        if (p is null || r.Buffer.Length == 0)
        {
            continue;
        }

        var first = r.Buffer[0];
        if (first < 4 && MiniStun.IsBindingRequest(r.Buffer))
        {
            // RFC 7983 demux: 0–3 STUN
            var answer = MiniStun.Answer(r.Buffer, p.Ufrag, p.Pwd, r.RemoteEndPoint);
            if (answer is not null)
            {
                if (p.Remote is null)
                {
                    Log($"ICE: binding request from {r.RemoteEndPoint} accepted");
                }

                p.Remote = r.RemoteEndPoint;
                await udp.SendAsync(answer, r.RemoteEndPoint);
            }
            else
            {
                Log("ICE: binding request rejected (username or integrity)");
            }
        }
        else if (first is >= 20 and <= 63 && r.RemoteEndPoint.Equals(p.Remote))
        {
            // 20–63 DTLS, only from the ICE-validated address
            p.Session.Receive(r.Buffer);
            await Flush(p);
            if (p.Session.State == DtlsState.Connected && !p.Reported)
            {
                p.Reported = true;
                Log($"DTLS: connected {p.Session.ProtocolVersion} {p.Session.CipherSuite}, SRTP {p.Session.Keys!.Profile}, peer fingerprint matches SDP");
            }
            else if (p.Session.State == DtlsState.Failed && !p.Reported)
            {
                p.Reported = true;
                Log("DTLS: failed " + p.Session.FailureReason);
            }
        }
        else if (first is >= 128 and <= 191 && p.Session.State == DtlsState.Connected && p.SrtpResult is null)
        {
            // 128–191 RTP/RTCP; RTCP has payload type 200–207 in byte 1 (rtcp-mux, RFC 5761 §4)
            var pt = r.Buffer[1] & 0x7F;
            if (pt is < 64 or > 95)
            {
                p.SrtpResult = SrtpCheck.TryOpen(p.Session.Keys!, r.Buffer);
                Log("SRTP: " + p.SrtpResult);
            }
        }
    }
});

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(50);
        if (current is { } p)
        {
            p.Session.HandleTimeout();
            await Flush(p);
        }
    }
});

async Task Flush(Probe p)
{
    if (p.Remote is null)
    {
        return;
    }

    foreach (var d in p.Session.TakeOutgoing())
    {
        await udp.SendAsync(d, p.Remote);
    }
}

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://{httpBind}:{httpPort}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
app.MapGet("/", () => Results.Content(Page.Html, "text/html; charset=utf-8"));
app.MapPost("/offer", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    var offer = await reader.ReadToEndAsync();
    var fingerprint = SdpLine(offer, "a=fingerprint:sha-256 ");
    var mid = SdpLine(offer, "a=mid:");
    var opus = offer.Split('\n').Select(l => l.Trim())
        .FirstOrDefault(l => l.StartsWith("a=rtpmap:", StringComparison.Ordinal) && l.Contains(" opus/48000", StringComparison.OrdinalIgnoreCase));
    if (fingerprint is null || mid is null || opus is null)
    {
        return Results.BadRequest("offer without sha-256 fingerprint, mid or opus");
    }

    var pt = opus["a=rtpmap:".Length..opus.IndexOf(' ', StringComparison.Ordinal)];
    var old = current;
    var p = new Probe(new DtlsSession(context, DtlsRole.Server, fingerprint),
        RandomToken(8), RandomToken(24));
    p.Session.Start();
    current = p;
    old?.Session.Dispose();
    Log($"SDP: offer from {req.Headers.UserAgent}, mid {mid}, opus pt {pt}");
    var answer = string.Join("\r\n",
        "v=0", $"o=- {RandomNumberGenerator.GetInt32(int.MaxValue)} 2 IN IP4 {candidateIp}", "s=-", "t=0 0",
        $"a=group:BUNDLE {mid}", "a=ice-lite", "a=msid-semantic: WMS",
        $"m=audio 9 UDP/TLS/RTP/SAVPF {pt}", "c=IN IP4 0.0.0.0", $"a=mid:{mid}",
        $"a=ice-ufrag:{p.Ufrag}", $"a=ice-pwd:{p.Pwd}", $"a=fingerprint:sha-256 {certificate.Fingerprint}",
        "a=setup:passive", "a=recvonly", "a=rtcp-mux", $"a=rtpmap:{pt} opus/48000/2",
        $"a=candidate:1 1 udp 2130706431 {candidateIp} {udpPort} typ host", "a=end-of-candidates", "");
    return Results.Text(answer, "application/sdp");
});
app.MapGet("/status", () =>
{
    var p = current;
    return Results.Json(new
    {
        openssl = $"0x{DtlsContext.OpenSslVersion:X8}",
        state = p?.Session.State.ToString(),
        failure = p?.Session.FailureReason,
        version = p?.Session.ProtocolVersion,
        cipher = p?.Session.CipherSuite,
        srtpProfile = p?.Session.Keys?.Profile.ToString(),
        // Keys exist only after the handshake finished and the peer certificate matched the SDP fingerprint.
        handshakeCompleted = p?.Session.Keys is not null,
        srtp = p?.SrtpResult,
        log = log.ToArray(),
    });
});
await app.RunAsync();

static string? SdpLine(string sdp, string prefix) =>
    sdp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

static string RandomToken(int length)
{
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return string.Create(length, 0, (span, _) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
    });
}

static IPAddress PrimaryAddress()
{
    // The address of the default route (no packet is sent by connecting a UDP socket).
    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    s.Connect("192.0.2.1", 9);
    return ((IPEndPoint)s.LocalEndPoint!).Address;
}

internal sealed class Probe(DtlsSession session, string ufrag, string pwd)
{
    public DtlsSession Session { get; } = session;
    public string Ufrag { get; } = ufrag;
    public string Pwd { get; } = pwd;
    public IPEndPoint? Remote { get; set; }
    public bool Reported { get; set; }
    public string? SrtpResult { get; set; }
}
