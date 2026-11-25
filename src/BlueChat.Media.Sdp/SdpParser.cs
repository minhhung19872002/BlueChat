using System.Globalization;
using System.Text;

namespace BlueChat.Media.Sdp;

/// <summary>
/// SDP parser (RFC 8866) with the WebRTC attribute set (RFC 8829 JSEP, RFC 8843 BUNDLE, RFC 8839 ICE, RFC 8122,
/// RFC 8285, RFC 8851, RFC 8853, RFC 5576, RFC 4585, RFC 8841). Fails loudly: every defect throws
/// <see cref="SdpParseException"/> with a stable code and the line number.
/// </summary>
public static class SdpParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Parses raw bytes; invalid UTF-8 is an error (RFC 8866 §5: text is UTF-8 in WebRTC).</summary>
    public static SdpSession Parse(ReadOnlySpan<byte> utf8, SdpLimits? limits = null)
    {
        limits ??= SdpLimits.Default;
        if (utf8.Length > limits.MaxBytes)
        {
            throw new SdpParseException("too_large", 0, $"{utf8.Length} bytes > {limits.MaxBytes}");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new SdpParseException("bad_encoding", 0, "not valid UTF-8");
        }

        return Parse(text, limits);
    }

    /// <summary>RFC 8866 §5: <c>&lt;type&gt;=&lt;value&gt;</c> lines; CRLF (LF tolerated, as many stacks emit it).</summary>
    public static SdpSession Parse(string sdp, SdpLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        limits ??= SdpLimits.Default;
        if (Encoding.UTF8.GetByteCount(sdp) > limits.MaxBytes)
        {
            throw new SdpParseException("too_large", 0, $"more than {limits.MaxBytes} bytes");
        }

        var lines = sdp.Split('\n');
        var count = lines.Length;
        while (count > 0 && lines[count - 1].TrimEnd('\r').Length == 0)
        {
            count--; // trailing line breaks
        }

        if (count == 0)
        {
            throw new SdpParseException("empty", 0, "no lines");
        }

        if (count > limits.MaxLines)
        {
            throw new SdpParseException("too_many_lines", 0, $"{count} lines > {limits.MaxLines}");
        }

        var state = new State(limits);
        for (var i = 0; i < count; i++)
        {
            var raw = lines[i];
            if (raw.EndsWith('\r'))
            {
                raw = raw[..^1];
            }

            state.Line(raw, i + 1);
        }

        return state.Finish(count);
    }

    private sealed class State(SdpLimits limits)
    {
        private readonly SdpSession _session = new();
        private SdpMedia? _media;
        private bool _sawOrigin;
        private bool _sawName;
        private bool _sawTiming;
        private int _attributes;

        public void Line(string line, int n)
        {
            CheckLine(line, n);
            var type = line[0];
            var value = line[2..];
            if (n == 1)
            {
                // RFC 8866 §5.1: the description starts with v=0.
                if (type != 'v' || value != "0")
                {
                    throw new SdpParseException("bad_version", n, "first line must be v=0");
                }

                return;
            }

            switch (type)
            {
                case 'v':
                    throw new SdpParseException("duplicate_version", n, "second v= line");
                case 'o':
                    Origin(value, n);
                    break;
                case 's':
                    if (_media is not null || _sawName)
                    {
                        throw new SdpParseException("misplaced_line", n, "s= must appear once at session level");
                    }

                    _session.SessionName = value;
                    _sawName = true;
                    break;
                case 't':
                    if (_media is not null)
                    {
                        throw new SdpParseException("misplaced_line", n, "t= inside a media section");
                    }

                    _session.Timing = value;
                    _sawTiming = true;
                    break;
                case 'c':
                    if (_media is null)
                    {
                        _session.Connection = value;
                    }
                    else
                    {
                        _media.Connection = value;
                    }

                    break;
                case 'b':
                    (_media?.Bandwidths ?? _session.Bandwidths).Add(value);
                    break;
                case 'm':
                    Media(value, n);
                    break;
                case 'a':
                    if (++_attributes > limits.MaxAttributesPerSection)
                    {
                        throw new SdpParseException("too_many_attributes", n, $"more than {limits.MaxAttributesPerSection} in one section");
                    }

                    Attribute(value, n);
                    break;
                case 'i' or 'k':
                    (_media?.ExtraLines ?? _session.ExtraLines).Add(new SdpLine(type, value));
                    break;
                case 'u' or 'e' or 'p' or 'r' or 'z':
                    if (_media is not null)
                    {
                        throw new SdpParseException("misplaced_line", n, $"{type}= inside a media section");
                    }

                    _session.ExtraLines.Add(new SdpLine(type, value));
                    break;
                default:
                    throw new SdpParseException("unknown_line_type", n, $"line type '{type}'");
            }
        }

        public SdpSession Finish(int lineCount)
        {
            if (!_sawOrigin)
            {
                throw new SdpParseException("missing_origin", lineCount, "no o= line");
            }

            if (!_sawName)
            {
                throw new SdpParseException("missing_session_name", lineCount, "no s= line");
            }

            if (!_sawTiming)
            {
                throw new SdpParseException("missing_timing", lineCount, "no t= line");
            }

            return _session;
        }

        private void CheckLine(string line, int n)
        {
            if (line.Length > limits.MaxLineLength)
            {
                throw new SdpParseException("line_too_long", n, $"{line.Length} chars > {limits.MaxLineLength}");
            }

            if (line.Length < 2 || line[1] != '=' || line[0] is < 'a' or > 'z')
            {
                throw new SdpParseException("bad_line", n, "expected <letter>=<value>");
            }

            foreach (var c in line)
            {
                // RFC 8866 §9: no NUL, CR or LF inside a line; other control characters are never valid in WebRTC SDP.
                if (c < 0x20 && c != '\t' || c == 0x7F)
                {
                    throw new SdpParseException("control_character", n, $"U+{(int)c:X4}");
                }
            }
        }

        /// <summary>RFC 8866 §5.2: <c>o=&lt;username&gt; &lt;sess-id&gt; &lt;sess-version&gt; &lt;nettype&gt; &lt;addrtype&gt; &lt;unicast-address&gt;</c>.</summary>
        private void Origin(string value, int n)
        {
            if (_media is not null || _sawOrigin)
            {
                throw new SdpParseException("misplaced_line", n, "o= must appear once at session level");
            }

            var p = value.Split(' ');
            if (p.Length != 6 || p.Any(string.IsNullOrEmpty))
            {
                throw new SdpParseException("bad_origin", n, "o= needs 6 fields");
            }

            _session.Origin = new SdpOrigin(p[0], p[1], p[2], p[3], p[4], p[5]);
            _sawOrigin = true;
        }

        /// <summary>RFC 8866 §5.14: <c>m=&lt;media&gt; &lt;port&gt;[/&lt;number of ports&gt;] &lt;proto&gt; &lt;fmt&gt; …</c>.</summary>
        private void Media(string value, int n)
        {
            if (!_sawOrigin || !_sawName || !_sawTiming)
            {
                throw new SdpParseException("misplaced_line", n, "m= before o=, s= and t=");
            }

            if (_session.Media.Count >= limits.MaxMediaSections)
            {
                throw new SdpParseException("too_many_media", n, $"more than {limits.MaxMediaSections} m= sections");
            }

            var p = value.Split(' ');
            if (p.Length < 4 || p.Take(3).Any(string.IsNullOrEmpty))
            {
                throw new SdpParseException("bad_media", n, "m= needs media, port, proto and at least one format");
            }

            var media = new SdpMedia { Kind = Token(p[0], n, "bad_media"), Protocol = Token(p[2], n, "bad_media") };
            var portPart = p[1];
            var slash = portPart.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                media.PortCount = Int(portPart[(slash + 1)..], 1, 65535, "bad_media", n);
                portPart = portPart[..slash];
            }

            media.Port = Int(portPart, 0, 65535, "bad_media", n);
            for (var i = 3; i < p.Length; i++)
            {
                media.Formats.Add(Token(p[i], n, "bad_media"));
            }

            _session.Media.Add(media);
            _media = media;
            _attributes = 0;
        }

        /// <summary>RFC 8866 §5.13: <c>a=&lt;attribute-name&gt;[:&lt;attribute-value&gt;]</c>.</summary>
        private void Attribute(string value, int n)
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            var name = colon < 0 ? value : value[..colon];
            var arg = colon < 0 ? null : value[(colon + 1)..];
            if (name.Length == 0 || name.Any(c => c == ' '))
            {
                throw new SdpParseException("bad_attribute", n, "empty or spaced attribute name");
            }

            var m = _media;
            switch (name)
            {
                case "group" when m is null:
                    _session.Groups.Add(Group(Need(arg, n), n));
                    return;
                case "ice-lite" when m is null:
                    _session.IceLite = true;
                    return;
                case "extmap-allow-mixed" when m is null:
                    _session.ExtmapAllowMixed = true;
                    return;
                case "msid-semantic" when m is null:
                    _session.MsidSemantic = arg ?? "";
                    return;
                case "ice-ufrag":
                    SetIceUfrag(m, Need(arg, n), n);
                    return;
                case "ice-pwd":
                    SetIcePwd(m, Need(arg, n), n);
                    return;
                case "ice-options":
                    (m?.IceOptions ?? _session.IceOptions).AddRange(Need(arg, n).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    return;
                case "fingerprint":
                    var fp = Fingerprint(Need(arg, n), n);
                    if (m is null)
                    {
                        _session.Fingerprint = fp;
                    }
                    else
                    {
                        m.Fingerprint = fp;
                    }

                    return;
                case "setup":
                    var setup = Setup(Need(arg, n), n);
                    if (m is null)
                    {
                        _session.Setup = setup;
                    }
                    else
                    {
                        m.Setup = setup;
                    }

                    return;
                case "extmap":
                    (m?.Extmaps ?? _session.Extmaps).Add(Extmap(Need(arg, n), n));
                    return;
            }

            if (m is null)
            {
                _session.OtherAttributes.Add(new SdpAttribute(name, arg));
                return;
            }

            switch (name)
            {
                case "mid":
                    if (m.Mid is not null)
                    {
                        throw new SdpParseException("duplicate_attribute", n, "a=mid twice");
                    }

                    m.Mid = Token(Need(arg, n), n, "bad_mid");
                    break;
                case "sendrecv" or "sendonly" or "recvonly" or "inactive":
                    if (m.Direction is not null)
                    {
                        throw new SdpParseException("duplicate_attribute", n, "two direction attributes");
                    }

                    m.Direction = DirectionOf(name);
                    break;
                case "msid":
                    m.Msids.Add(Msid(Need(arg, n), n));
                    break;
                case "rtcp-mux":
                    m.RtcpMux = true;
                    break;
                case "rtcp-rsize":
                    m.RtcpRsize = true;
                    break;
                case "rtpmap":
                    m.RtpMaps.Add(RtpMap(Need(arg, n), n));
                    break;
                case "fmtp":
                    m.Fmtps.Add(Fmtp(Need(arg, n), n));
                    break;
                case "rtcp-fb":
                    m.RtcpFbs.Add(RtcpFb(Need(arg, n), n));
                    break;
                case "rid":
                    m.Rids.Add(Rid(Need(arg, n), n));
                    break;
                case "simulcast":
                    if (m.Simulcast is not null)
                    {
                        throw new SdpParseException("duplicate_attribute", n, "a=simulcast twice");
                    }

                    m.Simulcast = Simulcast(Need(arg, n), n);
                    break;
                case "ssrc-group":
                    m.SsrcGroups.Add(SsrcGroup(Need(arg, n), n));
                    break;
                case "ssrc":
                    m.Ssrcs.Add(Ssrc(Need(arg, n), n));
                    break;
                case "candidate":
                    m.Candidates.Add(ParseCandidate(Need(arg, n), n));
                    break;
                case "end-of-candidates":
                    m.EndOfCandidates = true;
                    break;
                case "sctp-port":
                    m.SctpPort = Int(Need(arg, n), 0, 65535, "bad_sctp_port", n);
                    break;
                case "max-message-size":
                    m.MaxMessageSize = Long(Need(arg, n), "bad_max_message_size", n);
                    break;
                default:
                    m.OtherAttributes.Add(new SdpAttribute(name, arg));
                    break;
            }
        }

        private void SetIceUfrag(SdpMedia? m, string v, int n)
        {
            // RFC 8839 §5.4: ice-char = ALPHA / DIGIT / "+" / "/", 4–256 characters.
            IceChars(v, 4, 256, "bad_ice_ufrag", n);
            if (m is null)
            {
                _session.IceUfrag = v;
            }
            else
            {
                m.IceUfrag = v;
            }
        }

        private void SetIcePwd(SdpMedia? m, string v, int n)
        {
            // RFC 8839 §5.4: 22–256 ice-chars.
            IceChars(v, 22, 256, "bad_ice_pwd", n);
            if (m is null)
            {
                _session.IcePwd = v;
            }
            else
            {
                m.IcePwd = v;
            }
        }
    }

    /// <summary>RFC 8839 §5.1 candidate-attribute, including RFC 6544 tcptype and the generation/ufrag extensions.</summary>
    public static SdpCandidate ParseCandidate(string value, int lineNumber = 0)
    {
        var n = lineNumber;
        if (value.StartsWith("candidate:", StringComparison.Ordinal))
        {
            value = value["candidate:".Length..];
        }

        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 8 || p[6] != "typ")
        {
            throw new SdpParseException("bad_candidate", n, "expected <foundation> <component> <transport> <priority> <address> <port> typ <type>");
        }

        if (p[0].Length is 0 or > 32)
        {
            throw new SdpParseException("bad_candidate", n, "foundation must be 1–32 characters");
        }

        var component = Int(p[1], 1, 256, "bad_candidate", n);
        var priority = UInt(p[3], "bad_candidate", n);
        var port = Int(p[5], 0, 65535, "bad_candidate", n);
        string? raddr = null;
        int? rport = null;
        var extensions = new List<KeyValuePair<string, string>>();
        var i = 8;
        while (i < p.Length)
        {
            if (i + 1 >= p.Length)
            {
                throw new SdpParseException("bad_candidate", n, $"extension '{p[i]}' without a value");
            }

            switch (p[i])
            {
                case "raddr":
                    raddr = p[i + 1];
                    break;
                case "rport":
                    rport = Int(p[i + 1], 0, 65535, "bad_candidate", n);
                    break;
                default:
                    extensions.Add(new(p[i], p[i + 1]));
                    break;
            }

            i += 2;
        }

        return new SdpCandidate(p[0], component, p[2], priority, p[4], port, p[7], raddr, rport, extensions);
    }

    /// <summary>RFC 5888 §5: <c>a=group:&lt;semantics&gt; *(SP &lt;mid&gt;)</c>.</summary>
    private static SdpGroup Group(string value, int n)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0)
        {
            throw new SdpParseException("bad_group", n, "no semantics");
        }

        return new SdpGroup(p[0], p.Skip(1).ToList());
    }

    /// <summary>RFC 8122 §5: <c>a=fingerprint:&lt;hash-func&gt; &lt;hex:hex:…&gt;</c>.</summary>
    private static SdpFingerprint Fingerprint(string value, int n)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 2 || p[1].Split(':').Any(h => h.Length != 2 || !h.All(Uri.IsHexDigit)))
        {
            throw new SdpParseException("bad_fingerprint", n, "expected <hash> <hex pairs separated by ':'>");
        }

        return new SdpFingerprint(p[0], p[1]);
    }

    /// <summary>RFC 4145 §4: setup role.</summary>
    private static SdpSetup Setup(string value, int n) => value switch
    {
        "actpass" => SdpSetup.ActPass,
        "active" => SdpSetup.Active,
        "passive" => SdpSetup.Passive,
        "holdconn" => SdpSetup.HoldConn,
        _ => throw new SdpParseException("bad_setup", n, value),
    };

    internal static string SetupText(SdpSetup s) => s switch
    {
        SdpSetup.ActPass => "actpass",
        SdpSetup.Active => "active",
        SdpSetup.Passive => "passive",
        _ => "holdconn",
    };

    private static SdpDirection DirectionOf(string s) => s switch
    {
        "sendrecv" => SdpDirection.SendRecv,
        "sendonly" => SdpDirection.SendOnly,
        "recvonly" => SdpDirection.RecvOnly,
        _ => SdpDirection.Inactive,
    };

    internal static string DirectionText(SdpDirection d) => d switch
    {
        SdpDirection.SendRecv => "sendrecv",
        SdpDirection.SendOnly => "sendonly",
        SdpDirection.RecvOnly => "recvonly",
        _ => "inactive",
    };

    /// <summary>RFC 8830 §2: <c>a=msid:&lt;stream id&gt; [&lt;track id&gt;]</c>.</summary>
    private static SdpMsid Msid(string value, int n)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length is 0 or > 2)
        {
            throw new SdpParseException("bad_msid", n, "expected 1 or 2 identifiers");
        }

        return new SdpMsid(p[0], p.Length == 2 ? p[1] : null);
    }

    /// <summary>RFC 8285 §8: <c>a=extmap:&lt;id&gt;[/&lt;direction&gt;] &lt;URI&gt; [&lt;attributes&gt;]</c>.</summary>
    private static SdpExtmap Extmap(string value, int n)
    {
        var sp = value.IndexOf(' ', StringComparison.Ordinal);
        if (sp <= 0)
        {
            throw new SdpParseException("bad_extmap", n, "expected <id> <uri>");
        }

        var head = value[..sp];
        SdpDirection? direction = null;
        var slash = head.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            var d = head[(slash + 1)..];
            if (d is not ("sendrecv" or "sendonly" or "recvonly" or "inactive"))
            {
                throw new SdpParseException("bad_extmap", n, "bad direction " + d);
            }

            direction = DirectionOf(d);
            head = head[..slash];
        }

        // One-byte ids 1–14, two-byte 1–255 (RFC 8285 §4.2, §4.3).
        var id = Int(head, 1, 255, "bad_extmap", n);
        var rest = value[(sp + 1)..];
        var sp2 = rest.IndexOf(' ', StringComparison.Ordinal);
        var uri = sp2 < 0 ? rest : rest[..sp2];
        if (uri.Length == 0)
        {
            throw new SdpParseException("bad_extmap", n, "empty URI");
        }

        return new SdpExtmap(id, direction, uri, sp2 < 0 ? null : rest[(sp2 + 1)..]);
    }

    /// <summary>RFC 8866 §6.6: <c>a=rtpmap:&lt;pt&gt; &lt;encoding&gt;/&lt;clock&gt;[/&lt;channels&gt;]</c>.</summary>
    private static SdpRtpMap RtpMap(string value, int n)
    {
        var sp = value.IndexOf(' ', StringComparison.Ordinal);
        if (sp <= 0)
        {
            throw new SdpParseException("bad_rtpmap", n, "expected <pt> <encoding>/<clock>");
        }

        var pt = Int(value[..sp], 0, 127, "bad_rtpmap", n);
        var p = value[(sp + 1)..].Split('/');
        if (p.Length is < 2 or > 3 || p[0].Length == 0)
        {
            throw new SdpParseException("bad_rtpmap", n, "expected <encoding>/<clock>[/<channels>]");
        }

        var clock = Int(p[1], 1, 10_000_000, "bad_rtpmap", n);
        int? channels = p.Length == 3 ? Int(p[2], 1, 255, "bad_rtpmap", n) : null;
        return new SdpRtpMap(pt, p[0], clock, channels);
    }

    /// <summary>RFC 8866 §6.15: <c>a=fmtp:&lt;format&gt; &lt;format specific parameters&gt;</c>.</summary>
    private static SdpFmtp Fmtp(string value, int n)
    {
        var sp = value.IndexOf(' ', StringComparison.Ordinal);
        if (sp <= 0 || sp == value.Length - 1)
        {
            throw new SdpParseException("bad_fmtp", n, "expected <format> <parameters>");
        }

        return new SdpFmtp(value[..sp], value[(sp + 1)..]);
    }

    /// <summary>RFC 4585 §4.2: <c>a=rtcp-fb:&lt;pt|*&gt; &lt;type&gt; [&lt;parameters&gt;]</c>.</summary>
    private static SdpRtcpFb RtcpFb(string value, int n)
    {
        var p = value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2)
        {
            throw new SdpParseException("bad_rtcp_fb", n, "expected <pt> <type>");
        }

        if (p[0] != "*")
        {
            Int(p[0], 0, 127, "bad_rtcp_fb", n);
        }

        return new SdpRtcpFb(p[0], p[1], p.Length == 3 ? p[2] : null);
    }

    /// <summary>RFC 8851 §10: <c>a=rid:&lt;id&gt; &lt;send|recv&gt; [pt=…;restriction=…]</c>.</summary>
    private static SdpRid Rid(string value, int n)
    {
        var p = value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2)
        {
            throw new SdpParseException("bad_rid", n, "expected <id> <send|recv>");
        }

        RidId(p[0], n);
        var direction = p[1] switch
        {
            "send" => SdpRidDirection.Send,
            "recv" => SdpRidDirection.Recv,
            _ => throw new SdpParseException("bad_rid", n, "direction must be send or recv"),
        };
        var pts = new List<int>();
        var restrictions = new List<KeyValuePair<string, string?>>();
        if (p.Length == 3)
        {
            foreach (var part in p[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                var key = eq < 0 ? part : part[..eq];
                var val = eq < 0 ? null : part[(eq + 1)..];
                if (key.Length == 0)
                {
                    throw new SdpParseException("bad_rid", n, "empty restriction name");
                }

                if (key == "pt")
                {
                    foreach (var t in (val ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        pts.Add(Int(t, 0, 127, "bad_rid", n));
                    }
                }
                else
                {
                    restrictions.Add(new(key, val));
                }
            }
        }

        return new SdpRid(p[0], direction, pts, restrictions);
    }

    /// <summary>RFC 8851 §10: rid-id = 1*(alpha-numeric / "-" / "_"), at most 255 (RFC 8852 limits RtpStreamId to 255 octets).</summary>
    private static void RidId(string id, int n)
    {
        if (id.Length is 0 or > 255 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new SdpParseException("bad_rid", n, "rid-id must be 1–255 of [A-Za-z0-9_-]");
        }
    }

    /// <summary>RFC 8853 §5.1: <c>a=simulcast:&lt;send|recv&gt; &lt;list&gt; [&lt;recv|send&gt; &lt;list&gt;]</c>; list = alternatives separated by ',', streams by ';', '~' = paused.</summary>
    private static SdpSimulcast Simulcast(string value, int n)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length is not (2 or 4))
        {
            throw new SdpParseException("bad_simulcast", n, "expected <dir> <list> [<dir> <list>]");
        }

        List<List<SdpSimulcastRid>>? send = null, recv = null;
        for (var i = 0; i < p.Length; i += 2)
        {
            var list = SimulcastList(p[i + 1], n);
            switch (p[i])
            {
                case "send" when send is null:
                    send = list;
                    break;
                case "recv" when recv is null:
                    recv = list;
                    break;
                default:
                    throw new SdpParseException("bad_simulcast", n, "direction must be send or recv, each at most once");
            }
        }

        return new SdpSimulcast(send ?? [], recv ?? [], p[0] == "recv");
    }

    private static List<List<SdpSimulcastRid>> SimulcastList(string value, int n)
    {
        var streams = new List<List<SdpSimulcastRid>>();
        foreach (var stream in value.Split(';'))
        {
            var alternatives = new List<SdpSimulcastRid>();
            foreach (var alt in stream.Split(','))
            {
                var paused = alt.StartsWith('~');
                var id = paused ? alt[1..] : alt;
                RidId(id, n);
                alternatives.Add(new SdpSimulcastRid(id, paused));
            }

            streams.Add(alternatives);
        }

        return streams;
    }

    /// <summary>RFC 5576 §4.2: <c>a=ssrc-group:&lt;semantics&gt; &lt;ssrc-id&gt; …</c>.</summary>
    private static SdpSsrcGroup SsrcGroup(string value, int n)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2)
        {
            throw new SdpParseException("bad_ssrc_group", n, "expected <semantics> <ssrc>…");
        }

        return new SdpSsrcGroup(p[0], p.Skip(1).Select(s => UInt(s, "bad_ssrc_group", n)).ToList());
    }

    /// <summary>RFC 5576 §4.1: <c>a=ssrc:&lt;ssrc-id&gt; &lt;attribute&gt;[:&lt;value&gt;]</c>.</summary>
    private static SdpSsrc Ssrc(string value, int n)
    {
        var sp = value.IndexOf(' ', StringComparison.Ordinal);
        if (sp <= 0 || sp == value.Length - 1)
        {
            throw new SdpParseException("bad_ssrc", n, "expected <ssrc> <attribute>");
        }

        var ssrc = UInt(value[..sp], "bad_ssrc", n);
        var rest = value[(sp + 1)..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? new SdpSsrc(ssrc, rest, null) : new SdpSsrc(ssrc, rest[..colon], rest[(colon + 1)..]);
    }

    private static string Need(string? arg, int n) =>
        string.IsNullOrEmpty(arg) ? throw new SdpParseException("missing_value", n, "attribute needs a value") : arg;

    private static string Token(string s, int n, string code)
    {
        if (s.Length == 0 || s.Any(c => c <= ' '))
        {
            throw new SdpParseException(code, n, "empty or invalid token");
        }

        return s;
    }

    private static void IceChars(string v, int min, int max, string code, int n)
    {
        if (v.Length < min || v.Length > max || !v.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))
        {
            throw new SdpParseException(code, n, $"{min}–{max} of [A-Za-z0-9+/]");
        }
    }

    private static int Int(string s, int min, int max, string code, int n)
    {
        if (s.Length is 0 or > 10 || !s.All(char.IsAsciiDigit)
            || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
        {
            throw new SdpParseException(code, n, $"'{s}' is not an integer in {min}..{max}");
        }

        return v;
    }

    private static uint UInt(string s, string code, int n)
    {
        if (s.Length is 0 or > 10 || !s.All(char.IsAsciiDigit) || !uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            throw new SdpParseException(code, n, $"'{s}' is not a 32-bit unsigned integer");
        }

        return v;
    }

    private static long Long(string s, string code, int n)
    {
        if (s.Length is 0 or > 18 || !s.All(char.IsAsciiDigit) || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            throw new SdpParseException(code, n, $"'{s}' is not a non-negative integer");
        }

        return v;
    }
}
