using System.Globalization;
using System.Security.Cryptography;

namespace BlueChat.Media.Sdp;

/// <summary>A codec the server accepts, matched against the offer's rtpmap/fmtp (RFC 8829 §5.3.1).</summary>
public sealed record SdpCodecPreference(
    string Kind,
    string Name,
    int ClockRate,
    int? Channels,
    Func<IReadOnlyDictionary<string, string>, bool>? FmtpFilter = null)
{
    /// <summary>Opus (RFC 7587 §7): <c>opus/48000/2</c>.</summary>
    public static SdpCodecPreference Opus { get; } = new("audio", "opus", 48000, 2);

    /// <summary>VP8 (RFC 7741).</summary>
    public static SdpCodecPreference Vp8 { get; } = new("video", "VP8", 90000, null);

    /// <summary>VP9 profile 0 (RFC 9628 §6): profile-id absent or 0.</summary>
    public static SdpCodecPreference Vp9 { get; } = new("video", "VP9", 90000, null,
        f => !f.TryGetValue("profile-id", out var p) || p == "0");

    /// <summary>
    /// H.264 constrained baseline, packetization-mode=1 (RFC 6184 §8.1, RFC 7742 §6.2): profile_idc 0x42 with
    /// constraint_set1_flag (0x40), or profile_idc 0x4D with constraint_set0 and set1 (0xC0).
    /// </summary>
    public static SdpCodecPreference H264ConstrainedBaseline { get; } = new("video", "H264", 90000, null, f =>
    {
        if (!f.TryGetValue("packetization-mode", out var mode) || mode != "1"
            || !f.TryGetValue("profile-level-id", out var pli) || pli.Length != 6
            || !byte.TryParse(pli.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var profile)
            || !byte.TryParse(pli.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var iop))
        {
            return false;
        }

        return (profile == 0x42 && (iop & 0x40) != 0) || (profile == 0x4D && (iop & 0xC0) == 0xC0);
    });

    /// <summary>AV1 (AOMedia RTP payload spec §7.2), profile 0 (absent or 0).</summary>
    public static SdpCodecPreference Av1 { get; } = new("video", "AV1", 90000, null,
        f => !f.TryGetValue("profile", out var p) || p == "0");

    public static IReadOnlyList<SdpCodecPreference> Defaults { get; } = [Opus, Vp8, Vp9, H264ConstrainedBaseline];
}

/// <summary>A stream the server will send in a section (loopback echo, later the SFU).</summary>
public sealed record SdpSendStream(uint Ssrc, uint? RtxSsrc, string Cname, string StreamId, string TrackId);

public sealed record SdpAnswerOptions
{
    public required string IceUfrag { get; init; }

    public required string IcePwd { get; init; }

    public required SdpFingerprint Fingerprint { get; init; }

    public IReadOnlyList<SdpCandidate> Candidates { get; init; } = [];

    /// <summary>Preference order: the answer lists codecs in this order.</summary>
    public IReadOnlyList<SdpCodecPreference> Codecs { get; init; } = SdpCodecPreference.Defaults;

    /// <summary>Header extension URIs kept from the offer (RFC 8285 §6: the answerer keeps the offered id).</summary>
    public IReadOnlySet<string> HeaderExtensions { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "urn:ietf:params:rtp-hdrext:sdes:mid",
        "urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id",
        "urn:ietf:params:rtp-hdrext:sdes:repaired-rtp-stream-id",
        "urn:ietf:params:rtp-hdrext:ssrc-audio-level",
        "http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time",
        "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01",
        "https://aomediacodec.github.io/av1-rtp-spec/#dependency-descriptor-rtp-header-extension",
    };

    /// <summary>RTCP feedback kept (RFC 4585, RFC 5104, transport-cc, REMB); key = "type" or "type param".</summary>
    public IReadOnlySet<string> RtcpFeedback { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "nack", "nack pli", "ccm fir", "transport-cc", "goog-remb",
    };

    public bool AcceptDataChannel { get; init; }

    /// <summary>Mids where the server only receives even if the offer would allow sending.</summary>
    public IReadOnlySet<string> RecvOnlyMids { get; init; } = new HashSet<string>();

    /// <summary>Streams the server sends, by mid.</summary>
    public IReadOnlyDictionary<string, SdpSendStream> SendStreams { get; init; } = new Dictionary<string, SdpSendStream>();

    public string? SessionId { get; init; }

    public string OriginAddress { get; init; } = "127.0.0.1";
}

/// <summary>
/// Builds the answer of an ICE-lite, DTLS-passive media server to a browser offer (RFC 8829 §5.3.1 answerer rules,
/// RFC 8843 BUNDLE, RFC 8842 setup, RFC 8853 simulcast). The result keeps the offer's mids and m= order.
/// </summary>
public static class JsepAnswerer
{
    public static SdpSession CreateAnswer(SdpSession offer, SdpAnswerOptions options)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(options);
        if (offer.Media.Count == 0)
        {
            throw new SdpNegotiationException("no_media", "offer has no m= section");
        }

        var answer = new SdpSession
        {
            Origin = new SdpOrigin("-", options.SessionId ?? RandomSessionId(), "2", "IN", "IP4", options.OriginAddress),
            SessionName = "-",
            Timing = "0 0",
            IceLite = true,
            MsidSemantic = "WMS *",
            ExtmapAllowMixed = offer.ExtmapAllowMixed,
        };

        var accepted = new List<string>();
        foreach (var m in offer.Media)
        {
            var mid = m.Mid ?? throw new SdpNegotiationException("missing_mid", "every offered m= section needs a=mid (RFC 8829 §5.2.1)");
            var setup = m.Setup ?? offer.Setup;
            if (setup is SdpSetup.Passive or SdpSetup.HoldConn)
            {
                throw new SdpNegotiationException("unsupported_setup", $"mid {mid}: offer setup must be actpass or active");
            }

            if ((m.Fingerprint ?? offer.Fingerprint) is null && !m.IsRejected)
            {
                throw new SdpNegotiationException("missing_fingerprint", $"mid {mid}: no a=fingerprint (RFC 8122)");
            }

            var section = m.IsRejected ? null : Answer(offer, m, options);
            if (section is null)
            {
                answer.Media.Add(Rejected(m));
                continue;
            }

            accepted.Add(mid);
            answer.Media.Add(section);
        }

        if (accepted.Count > 0)
        {
            answer.Groups.Add(new SdpGroup("BUNDLE", accepted));
            // Candidates go into the BUNDLE-tag section, which carries the shared transport (RFC 8843 §7.3.1).
            var tag = answer.Media.First(x => x.Mid == accepted[0]);
            tag.Candidates.AddRange(options.Candidates);
            tag.EndOfCandidates = true;
        }

        return answer;
    }

    private static SdpMedia? Answer(SdpSession offer, SdpMedia m, SdpAnswerOptions o)
    {
        var isRtp = m.Protocol.Contains("RTP/SAVPF", StringComparison.Ordinal) && m.Kind is "audio" or "video";
        var isData = m.Kind == "application" && m.Protocol.Contains("SCTP", StringComparison.Ordinal);
        if (isData)
        {
            if (!o.AcceptDataChannel)
            {
                return null;
            }

            var dc = Base(m, o);
            dc.Formats.AddRange(m.Formats);
            dc.SctpPort = 5000;
            dc.MaxMessageSize = 262144;
            return dc;
        }

        // The server demultiplexes RTP and RTCP on one port; an offer without rtcp-mux is not supported (RFC 8858).
        if (!isRtp || !m.RtcpMux)
        {
            return null;
        }

        var primaries = new List<int>();
        foreach (var pref in o.Codecs.Where(c => c.Kind == m.Kind))
        {
            foreach (var f in m.Formats)
            {
                if (!int.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var pt) || primaries.Contains(pt))
                {
                    continue;
                }

                var map = m.RtpMapOf(pt);
                if (map is null || !string.Equals(map.EncodingName, pref.Name, StringComparison.OrdinalIgnoreCase)
                    || map.ClockRate != pref.ClockRate
                    || (pref.Channels is { } ch && (map.Channels ?? 1) != ch))
                {
                    continue;
                }

                var fmtp = new SdpFmtp(f, m.FmtpOf(pt) ?? "").ToDictionary();
                if (pref.FmtpFilter is null || pref.FmtpFilter(fmtp))
                {
                    primaries.Add(pt);
                    break; // one payload type per preferred codec keeps the SFU's mapping simple
                }
            }
        }

        if (primaries.Count == 0)
        {
            return null;
        }

        var s = Base(m, o);
        s.RtcpMux = true;
        s.RtcpRsize = m.RtcpRsize;
        foreach (var pt in primaries)
        {
            Copy(m, s, pt, o, primary: true);
            // RFC 4588 §8.6: rtx with apt=<pt>.
            foreach (var r in m.RtpMaps.Where(r => string.Equals(r.EncodingName, "rtx", StringComparison.OrdinalIgnoreCase)))
            {
                var fmtp = new SdpFmtp("", m.FmtpOf(r.PayloadType) ?? "").ToDictionary();
                if (fmtp.TryGetValue("apt", out var apt) && apt == pt.ToString(CultureInfo.InvariantCulture))
                {
                    Copy(m, s, r.PayloadType, o, primary: false);
                    break;
                }
            }
        }

        foreach (var e in m.Extmaps.Where(e => o.HeaderExtensions.Contains(e.Uri)))
        {
            s.Extmaps.Add(new SdpExtmap(e.Id, null, e.Uri, null));
        }

        var offered = m.EffectiveDirection;
        var recvOnly = o.RecvOnlyMids.Contains(s.Mid!);
        s.Direction = offered switch
        {
            SdpDirection.SendRecv => recvOnly ? SdpDirection.RecvOnly : SdpDirection.SendRecv,
            SdpDirection.SendOnly => SdpDirection.RecvOnly,
            SdpDirection.RecvOnly => recvOnly ? SdpDirection.Inactive : SdpDirection.SendOnly,
            _ => SdpDirection.Inactive,
        };

        // RFC 8853 §5.3: offered send simulcast → answered recv simulcast with the same RIDs (RFC 8851 §7.2).
        if (m.Simulcast is { Send.Count: > 0 } sim && s.Direction is SdpDirection.SendRecv or SdpDirection.RecvOnly)
        {
            foreach (var rid in m.Rids.Where(r => r.Direction == SdpRidDirection.Send))
            {
                var pts = rid.PayloadTypes.Where(primaries.Contains).ToList();
                if (rid.PayloadTypes.Count > 0 && pts.Count == 0)
                {
                    continue;
                }

                s.Rids.Add(new SdpRid(rid.Id, SdpRidDirection.Recv, pts, []));
            }

            var known = s.Rids.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var recv = sim.Send.Select(alt => alt.Where(r => known.Contains(r.Id)).ToList()).Where(alt => alt.Count > 0).ToList();
            if (recv.Count > 0)
            {
                s.Simulcast = new SdpSimulcast([], recv, RecvFirst: true);
            }
        }

        if (s.Direction is SdpDirection.SendRecv or SdpDirection.SendOnly && o.SendStreams.TryGetValue(s.Mid!, out var send))
        {
            AddSendStream(s, send);
        }

        return s;
    }

    private static void AddSendStream(SdpMedia s, SdpSendStream send)
    {
        // RFC 8830 §2 msid, RFC 5576 §4.1 ssrc attributes, §4.2 FID group for RTX (RFC 4588 §8.6).
        s.Msids.Add(new SdpMsid(send.StreamId, send.TrackId));
        var hasRtx = s.RtpMaps.Any(r => string.Equals(r.EncodingName, "rtx", StringComparison.OrdinalIgnoreCase));
        if (send.RtxSsrc is { } rtx && hasRtx)
        {
            s.SsrcGroups.Add(new SdpSsrcGroup("FID", [send.Ssrc, rtx]));
        }

        foreach (var ssrc in send.RtxSsrc is { } r && hasRtx ? new[] { send.Ssrc, r } : [send.Ssrc])
        {
            s.Ssrcs.Add(new SdpSsrc(ssrc, "cname", send.Cname));
            s.Ssrcs.Add(new SdpSsrc(ssrc, "msid", send.StreamId + " " + send.TrackId));
        }

    }

    private static void Copy(SdpMedia from, SdpMedia to, int pt, SdpAnswerOptions o, bool primary)
    {
        var f = pt.ToString(CultureInfo.InvariantCulture);
        to.Formats.Add(f);
        to.RtpMaps.Add(from.RtpMapOf(pt)!);
        if (from.FmtpOf(pt) is { } fmtp)
        {
            to.Fmtps.Add(new SdpFmtp(f, fmtp));
        }

        if (primary)
        {
            foreach (var fb in from.RtcpFbs.Where(x => x.Format == f || x.Format == "*"))
            {
                var key = fb.Parameter is null ? fb.Type : fb.Type + " " + fb.Parameter;
                if (o.RtcpFeedback.Contains(key))
                {
                    to.RtcpFbs.Add(new SdpRtcpFb(f, fb.Type, fb.Parameter));
                }
            }
        }
    }

    private static SdpMedia Base(SdpMedia m, SdpAnswerOptions o) => new()
    {
        Kind = m.Kind,
        Port = 9,
        Protocol = m.Protocol,
        Connection = "IN IP4 0.0.0.0",
        Mid = m.Mid,
        IceUfrag = o.IceUfrag,
        IcePwd = o.IcePwd,
        Fingerprint = o.Fingerprint,
        Setup = SdpSetup.Passive, // RFC 8842 §5.3: we are the DTLS server
    };

    /// <summary>RFC 8829 §5.3.1: a rejected section keeps media type, protocol and at least one format, port 0.</summary>
    private static SdpMedia Rejected(SdpMedia m)
    {
        var r = new SdpMedia { Kind = m.Kind, Port = 0, Protocol = m.Protocol, Connection = "IN IP4 0.0.0.0", Mid = m.Mid, Direction = SdpDirection.Inactive };
        r.Formats.Add(m.Formats[0]);
        return r;
    }

    private static string RandomSessionId() =>
        // RFC 8866 §5.2: numeric; JSEP (RFC 8829 §5.2.1) asks for a 64-bit random value below 2^63.
        ((ulong)RandomNumberGenerator.GetInt32(int.MaxValue) << 31 | (uint)RandomNumberGenerator.GetInt32(int.MaxValue)).ToString(CultureInfo.InvariantCulture);
}
