// Phase 5 acceptance (spec §14): a browser connects to a media node built from the BlueChat media libraries and
// receives its own audio and video back. Usage:
//   dotnet run --project tools/MediaLoopback -- [--ip <advertised ip>] [--udp 50000] [--tcp 50001] [--http 5299] [--bind 127.0.0.1]
// POST /offer?mode=udp|tcp|both[&session=<id>] (body = SDP offer) → answer; header X-Session carries the id.
// Sending the session id again with a new offer performs an ICE restart on the same transport.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlueChat.Media.Ice;
using BlueChat.Media.Sdp;
using BlueChat.Media.Transport;
using MediaLoopback;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = args.Select((v, i) => (v, i)).Where(x => x.v.StartsWith("--", StringComparison.Ordinal))
    .ToDictionary(x => x.v[2..], x => x.i + 1 < args.Length ? args[x.i + 1] : "");
int Int(string key, int d) => opts.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : d;

var advertised = opts.TryGetValue("ip", out var ip) && ip.Length > 0 ? IPAddress.Parse(ip) : PrimaryAddress();
await using var node = new MediaNode(new MediaNodeOptions
{
    AdvertisedAddress = advertised,
    UdpPort = Int("udp", 50000),
    TcpPort = Int("tcp", 50001),
}, TimeProvider.System);
node.Start();
Console.WriteLine($"media node {advertised} udp {node.UdpPort} tcp {node.TcpPort} fingerprint {node.Certificate.Fingerprint}");

var sessions = new ConcurrentDictionary<string, EchoSession>();
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://{opts.GetValueOrDefault("bind", "127.0.0.1")}:{Int("http", 5299)}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.MapGet("/", () => Results.Content(LoopbackPage.Html, "text/html; charset=utf-8"));
app.MapPost("/offer", async (HttpRequest req, HttpResponse res) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    SdpSession offer;
    try
    {
        offer = SdpParser.Parse(await reader.ReadToEndAsync());
    }
    catch (SdpParseException e)
    {
        return Results.BadRequest($"{e.Code} line {e.LineNumber}");
    }

    var first = offer.Media.FirstOrDefault(m => !m.IsRejected);
    var ufrag = first?.IceUfrag ?? offer.IceUfrag;
    var pwd = first?.IcePwd ?? offer.IcePwd;
    var fingerprint = first?.Fingerprint ?? offer.Fingerprint;
    if (ufrag is null || pwd is null || fingerprint is null || fingerprint.HashFunction != "sha-256")
    {
        return Results.BadRequest("offer without ICE credentials or sha-256 fingerprint");
    }

    var remoteIce = new IceCredentials(ufrag, pwd);
    EchoSession session;
    IceCredentials localIce;
    if (req.Query["session"].FirstOrDefault() is { } id && sessions.TryGetValue(id, out var existing))
    {
        session = existing;
        localIce = session.Transport.RestartIce(remoteIce); // RFC 8445 §9: same DTLS/SRTP, new ICE credentials
    }
    else
    {
        var transport = node.CreateTransport(remoteIce, fingerprint.Value);
        session = new EchoSession(transport);
        sessions[session.Id] = session;
        localIce = transport.LocalIce;
    }

    var mode = req.Query["mode"].FirstOrDefault() ?? "both";
    var candidates = node.Candidates
        .Where(c => mode == "both" || (mode == "tcp" ? c.Contains(" tcp ", StringComparison.Ordinal) : c.Contains(" udp ", StringComparison.Ordinal)))
        .Select(c => SdpParser.ParseCandidate(c))
        .ToList();

    var sendStreams = new Dictionary<string, SdpSendStream>();
    foreach (var m in offer.Media.Where(m => m.Mid is not null))
    {
        sendStreams[m.Mid!] = m.Kind == "video"
            ? new SdpSendStream(session.VideoOut, session.RtxOut, "loopback", "loopback", "video-echo")
            : new SdpSendStream(session.AudioOut, null, "loopback", "loopback", "audio-echo");
    }

    SdpSession answer;
    try
    {
        answer = JsepAnswerer.CreateAnswer(offer, new SdpAnswerOptions
        {
            IceUfrag = localIce.Ufrag,
            IcePwd = localIce.Pwd,
            Fingerprint = new SdpFingerprint("sha-256", node.Certificate.Fingerprint),
            Candidates = candidates,
            Codecs = [SdpCodecPreference.Opus, SdpCodecPreference.Vp8],
            // Echoing the browser's own header extensions back would confuse its receiver; none are negotiated.
            HeaderExtensions = new HashSet<string>(StringComparer.Ordinal),
            RtcpFeedback = new HashSet<string>(StringComparer.Ordinal) { "nack", "nack pli", "ccm fir" },
            SendStreams = sendStreams,
        });
    }
    catch (SdpNegotiationException e)
    {
        return Results.BadRequest(e.Code);
    }

    session.LearnPayloadTypes(answer);
    res.Headers["X-Session"] = session.Id;
    return Results.Text(answer.Write(), "application/sdp");
});

app.MapGet("/node", () => Results.Json(node.Counters));
app.MapGet("/status", (string? session) =>
    Results.Json(sessions.Values.Where(s => session is null || s.Id == session).Select(s => new
    {
        id = s.Id,
        state = s.Transport.State.ToString(),
        ice = s.Transport.Ice.State.ToString(),
        failure = s.Transport.FailureReason,
        selected = s.Transport.Selected?.ToString(),
        srtp = s.Transport.Profile?.ToString(),
        received = s.Transport.RtpReceivedCount,
        srtpRejected = s.Transport.SrtpRejectedCount,
        echoed = s.Echoed,
        keyFrames = s.KeyFramesSeen,
        pliForwarded = s.PliForwarded,
        nackForwarded = s.NackForwarded,
    })));

await app.RunAsync();

static IPAddress PrimaryAddress()
{
    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    s.Connect("192.0.2.1", 9); // no packet is sent
    return ((IPEndPoint)s.LocalEndPoint!).Address;
}
