using System.Globalization;
using System.Text;

namespace BlueChat.Media.Sdp;

/// <summary>
/// Serialises a <see cref="SdpSession"/> (RFC 8866 §5 line order, CRLF). Attribute order is fixed, so writing a parsed
/// document twice gives identical text; attributes BlueChat does not model come last, in their original order.
/// </summary>
public static class SdpWriter
{
    public static string Write(SdpSession s)
    {
        var b = new StringBuilder(2048);
        Line(b, 'v', s.Version.ToString(CultureInfo.InvariantCulture));
        var o = s.Origin;
        Line(b, 'o', $"{o.Username} {o.SessionId} {o.SessionVersion} {o.NetType} {o.AddrType} {o.Address}");
        Line(b, 's', s.SessionName);
        foreach (var l in s.ExtraLines.Where(l => l.Type is 'i' or 'u' or 'e' or 'p'))
        {
            Line(b, l.Type, l.Value);
        }

        if (s.Connection is not null)
        {
            Line(b, 'c', s.Connection);
        }

        foreach (var bw in s.Bandwidths)
        {
            Line(b, 'b', bw);
        }

        Line(b, 't', s.Timing);
        foreach (var l in s.ExtraLines.Where(l => l.Type is 'r' or 'z' or 'k'))
        {
            Line(b, l.Type, l.Value);
        }

        foreach (var g in s.Groups)
        {
            Attr(b, "group", g.Mids.Count == 0 ? g.Semantics : g.Semantics + " " + string.Join(' ', g.Mids));
        }

        if (s.IceLite)
        {
            Attr(b, "ice-lite", null);
        }

        if (s.IceOptions.Count > 0)
        {
            Attr(b, "ice-options", string.Join(' ', s.IceOptions));
        }

        if (s.IceUfrag is not null)
        {
            Attr(b, "ice-ufrag", s.IceUfrag);
        }

        if (s.IcePwd is not null)
        {
            Attr(b, "ice-pwd", s.IcePwd);
        }

        if (s.Fingerprint is { } fp)
        {
            Attr(b, "fingerprint", fp.HashFunction + " " + fp.Value);
        }

        if (s.Setup is { } setup)
        {
            Attr(b, "setup", SdpParser.SetupText(setup));
        }

        if (s.ExtmapAllowMixed)
        {
            Attr(b, "extmap-allow-mixed", null);
        }

        if (s.MsidSemantic is not null)
        {
            Attr(b, "msid-semantic", s.MsidSemantic);
        }

        foreach (var e in s.Extmaps)
        {
            Attr(b, "extmap", Extmap(e));
        }

        foreach (var a in s.OtherAttributes)
        {
            Attr(b, a.Name, a.Value);
        }

        foreach (var m in s.Media)
        {
            Media(b, m);
        }

        return b.ToString();
    }

    private static void Media(StringBuilder b, SdpMedia m)
    {
        var port = m.PortCount is { } count
            ? m.Port.ToString(CultureInfo.InvariantCulture) + "/" + count.ToString(CultureInfo.InvariantCulture)
            : m.Port.ToString(CultureInfo.InvariantCulture);
        Line(b, 'm', $"{m.Kind} {port} {m.Protocol} {string.Join(' ', m.Formats)}");
        foreach (var l in m.ExtraLines.Where(l => l.Type == 'i'))
        {
            Line(b, 'i', l.Value);
        }

        if (m.Connection is not null)
        {
            Line(b, 'c', m.Connection);
        }

        foreach (var bw in m.Bandwidths)
        {
            Line(b, 'b', bw);
        }

        foreach (var l in m.ExtraLines.Where(l => l.Type == 'k'))
        {
            Line(b, 'k', l.Value);
        }

        if (m.Mid is not null)
        {
            Attr(b, "mid", m.Mid);
        }

        if (m.IceUfrag is not null)
        {
            Attr(b, "ice-ufrag", m.IceUfrag);
        }

        if (m.IcePwd is not null)
        {
            Attr(b, "ice-pwd", m.IcePwd);
        }

        if (m.IceOptions.Count > 0)
        {
            Attr(b, "ice-options", string.Join(' ', m.IceOptions));
        }

        if (m.Fingerprint is { } fp)
        {
            Attr(b, "fingerprint", fp.HashFunction + " " + fp.Value);
        }

        if (m.Setup is { } setup)
        {
            Attr(b, "setup", SdpParser.SetupText(setup));
        }

        foreach (var e in m.Extmaps)
        {
            Attr(b, "extmap", Extmap(e));
        }

        if (m.Direction is { } d)
        {
            Attr(b, SdpParser.DirectionText(d), null);
        }

        foreach (var msid in m.Msids)
        {
            Attr(b, "msid", msid.TrackId is null ? msid.StreamId : msid.StreamId + " " + msid.TrackId);
        }

        if (m.RtcpMux)
        {
            Attr(b, "rtcp-mux", null);
        }

        if (m.RtcpRsize)
        {
            Attr(b, "rtcp-rsize", null);
        }

        // Codec attributes grouped per payload type in m= order, then anything referring to other formats.
        var written = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var f in m.Formats.Distinct(StringComparer.Ordinal))
        {
            foreach (var r in m.RtpMaps.Where(r => r.PayloadType.ToString(CultureInfo.InvariantCulture) == f))
            {
                RtpMap(b, r);
                written.Add(r);
            }

            foreach (var fb in m.RtcpFbs.Where(x => x.Format == f))
            {
                RtcpFb(b, fb);
                written.Add(fb);
            }

            foreach (var fmtp in m.Fmtps.Where(x => x.Format == f))
            {
                Attr(b, "fmtp", fmtp.Format + " " + fmtp.Parameters);
                written.Add(fmtp);
            }
        }

        foreach (var r in m.RtpMaps.Where(r => !written.Contains(r)))
        {
            RtpMap(b, r);
        }

        foreach (var fb in m.RtcpFbs.Where(x => !written.Contains(x)))
        {
            RtcpFb(b, fb);
        }

        foreach (var fmtp in m.Fmtps.Where(x => !written.Contains(x)))
        {
            Attr(b, "fmtp", fmtp.Format + " " + fmtp.Parameters);
        }

        foreach (var rid in m.Rids)
        {
            Attr(b, "rid", Rid(rid));
        }

        if (m.Simulcast is { } sim)
        {
            Attr(b, "simulcast", Simulcast(sim));
        }

        foreach (var g in m.SsrcGroups)
        {
            Attr(b, "ssrc-group", g.Semantics + " " + string.Join(' ', g.Ssrcs.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }

        foreach (var s in m.Ssrcs)
        {
            Attr(b, "ssrc", s.Ssrc.ToString(CultureInfo.InvariantCulture) + " " + (s.Value is null ? s.Attribute : s.Attribute + ":" + s.Value));
        }

        if (m.SctpPort is { } sctp)
        {
            Attr(b, "sctp-port", sctp.ToString(CultureInfo.InvariantCulture));
        }

        if (m.MaxMessageSize is { } mms)
        {
            Attr(b, "max-message-size", mms.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var c in m.Candidates)
        {
            Attr(b, "candidate", Candidate(c));
        }

        if (m.EndOfCandidates)
        {
            Attr(b, "end-of-candidates", null);
        }

        foreach (var a in m.OtherAttributes)
        {
            Attr(b, a.Name, a.Value);
        }
    }

    /// <summary>RFC 8839 §5.1 candidate-attribute value (without the <c>candidate:</c> prefix).</summary>
    public static string Candidate(SdpCandidate c)
    {
        var b = new StringBuilder();
        b.Append(c.Foundation).Append(' ').Append(c.Component.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(c.Transport).Append(' ').Append(c.Priority.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(c.Address).Append(' ').Append(c.Port.ToString(CultureInfo.InvariantCulture)).Append(" typ ").Append(c.Type);
        if (c.RelatedAddress is not null)
        {
            b.Append(" raddr ").Append(c.RelatedAddress);
        }

        if (c.RelatedPort is { } rp)
        {
            b.Append(" rport ").Append(rp.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var e in c.Extensions)
        {
            b.Append(' ').Append(e.Key).Append(' ').Append(e.Value);
        }

        return b.ToString();
    }

    private static string Extmap(SdpExtmap e)
    {
        var id = e.Id.ToString(CultureInfo.InvariantCulture);
        if (e.Direction is { } d)
        {
            id += "/" + SdpParser.DirectionText(d);
        }

        return e.Attributes is null ? $"{id} {e.Uri}" : $"{id} {e.Uri} {e.Attributes}";
    }

    private static void RtpMap(StringBuilder b, SdpRtpMap r)
    {
        var v = $"{r.PayloadType.ToString(CultureInfo.InvariantCulture)} {r.EncodingName}/{r.ClockRate.ToString(CultureInfo.InvariantCulture)}";
        if (r.Channels is { } ch)
        {
            v += "/" + ch.ToString(CultureInfo.InvariantCulture);
        }

        Attr(b, "rtpmap", v);
    }

    private static void RtcpFb(StringBuilder b, SdpRtcpFb fb) =>
        Attr(b, "rtcp-fb", fb.Parameter is null ? $"{fb.Format} {fb.Type}" : $"{fb.Format} {fb.Type} {fb.Parameter}");

    private static string Rid(SdpRid rid)
    {
        var parts = new List<string>();
        if (rid.PayloadTypes.Count > 0)
        {
            parts.Add("pt=" + string.Join(',', rid.PayloadTypes.Select(p => p.ToString(CultureInfo.InvariantCulture))));
        }

        parts.AddRange(rid.Restrictions.Select(r => r.Value is null ? r.Key : r.Key + "=" + r.Value));
        var head = rid.Id + " " + (rid.Direction == SdpRidDirection.Send ? "send" : "recv");
        return parts.Count == 0 ? head : head + " " + string.Join(';', parts);
    }

    private static string Simulcast(SdpSimulcast s)
    {
        static string List(List<List<SdpSimulcastRid>> l) =>
            string.Join(';', l.Select(alt => string.Join(',', alt.Select(r => (r.Paused ? "~" : "") + r.Id))));

        var parts = new List<string>();
        void Add(string dir, List<List<SdpSimulcastRid>> l)
        {
            if (l.Count > 0)
            {
                parts.Add(dir + " " + List(l));
            }
        }

        if (s.RecvFirst)
        {
            Add("recv", s.Recv);
            Add("send", s.Send);
        }
        else
        {
            Add("send", s.Send);
            Add("recv", s.Recv);
        }

        return string.Join(' ', parts);
    }

    private static void Line(StringBuilder b, char type, string value) => b.Append(type).Append('=').Append(value).Append("\r\n");

    private static void Attr(StringBuilder b, string name, string? value)
    {
        b.Append("a=").Append(name);
        if (value is not null)
        {
            b.Append(':').Append(value);
        }

        b.Append("\r\n");
    }
}
