namespace BlueChat.Media.Sdp;

/// <summary>A whole session description (RFC 8866 §5). Mutable on purpose: the answer builder fills one in.</summary>
public sealed class SdpSession
{
    public int Version { get; set; }

    public SdpOrigin Origin { get; set; } = new("-", "0", "0", "IN", "IP4", "127.0.0.1");

    public string SessionName { get; set; } = "-";

    /// <summary>The <c>t=</c> line value, e.g. <c>0 0</c>.</summary>
    public string Timing { get; set; } = "0 0";

    public string? Connection { get; set; }

    /// <summary>Raw <c>b=</c> values in order.</summary>
    public List<string> Bandwidths { get; } = [];

    /// <summary>Other session-level lines (i, u, e, p, r, z, k) kept in order for round-tripping.</summary>
    public List<SdpLine> ExtraLines { get; } = [];

    public List<SdpGroup> Groups { get; } = [];

    public bool IceLite { get; set; }

    public List<string> IceOptions { get; } = [];

    public string? IceUfrag { get; set; }

    public string? IcePwd { get; set; }

    public SdpFingerprint? Fingerprint { get; set; }

    public SdpSetup? Setup { get; set; }

    public bool ExtmapAllowMixed { get; set; }

    /// <summary>Raw value after <c>a=msid-semantic:</c>, e.g. <c> WMS *</c>.</summary>
    public string? MsidSemantic { get; set; }

    public List<SdpExtmap> Extmaps { get; } = [];

    public List<SdpAttribute> OtherAttributes { get; } = [];

    public List<SdpMedia> Media { get; } = [];

    /// <summary>Mids of the first <c>BUNDLE</c> group, empty when there is none.</summary>
    public IReadOnlyList<string> BundleMids =>
        Groups.FirstOrDefault(g => g.Semantics == "BUNDLE")?.Mids ?? [];

    public string Write() => SdpWriter.Write(this);

    public static SdpSession Parse(string sdp) => SdpParser.Parse(sdp);
}

/// <summary><c>o=</c> (RFC 8866 §5.2).</summary>
public sealed record SdpOrigin(string Username, string SessionId, string SessionVersion, string NetType, string AddrType, string Address);

/// <summary>Any line kept verbatim (type letter + value).</summary>
public sealed record SdpLine(char Type, string Value);

/// <summary>An attribute BlueChat does not model; kept verbatim. <see cref="Value"/> is null for a flag.</summary>
public sealed record SdpAttribute(string Name, string? Value);

/// <summary><c>a=group</c> (RFC 5888 §5).</summary>
public sealed record SdpGroup(string Semantics, List<string> Mids);

/// <summary><c>a=fingerprint</c> (RFC 8122 §5).</summary>
public sealed record SdpFingerprint(string HashFunction, string Value);

/// <summary><c>a=setup</c> (RFC 4145 §4).</summary>
public enum SdpSetup
{
    ActPass,
    Active,
    Passive,
    HoldConn,
}

/// <summary>Media direction attributes (RFC 8866 §6.7).</summary>
public enum SdpDirection
{
    SendRecv,
    SendOnly,
    RecvOnly,
    Inactive,
}

/// <summary>One <c>m=</c> section (RFC 8866 §5.14) with the attributes WebRTC uses.</summary>
public sealed class SdpMedia
{
    /// <summary><c>audio</c>, <c>video</c>, <c>application</c>, …</summary>
    public string Kind { get; set; } = "audio";

    public int Port { get; set; } = 9;

    public int? PortCount { get; set; }

    /// <summary>e.g. <c>UDP/TLS/RTP/SAVPF</c>.</summary>
    public string Protocol { get; set; } = "UDP/TLS/RTP/SAVPF";

    /// <summary>Payload types (RTP) or format tokens (e.g. <c>webrtc-datachannel</c>).</summary>
    public List<string> Formats { get; } = [];

    public string? Connection { get; set; }

    public List<string> Bandwidths { get; } = [];

    /// <summary>Other media-level lines (i, k) kept in order.</summary>
    public List<SdpLine> ExtraLines { get; } = [];

    public string? Mid { get; set; }

    public string? IceUfrag { get; set; }

    public string? IcePwd { get; set; }

    public List<string> IceOptions { get; } = [];

    public SdpFingerprint? Fingerprint { get; set; }

    public SdpSetup? Setup { get; set; }

    /// <summary>Null when the section has no direction attribute (RFC 8866 §6.7: then sendrecv).</summary>
    public SdpDirection? Direction { get; set; }

    public List<SdpMsid> Msids { get; } = [];

    public bool RtcpMux { get; set; }

    public bool RtcpRsize { get; set; }

    public List<SdpExtmap> Extmaps { get; } = [];

    public List<SdpRtpMap> RtpMaps { get; } = [];

    public List<SdpFmtp> Fmtps { get; } = [];

    public List<SdpRtcpFb> RtcpFbs { get; } = [];

    public List<SdpRid> Rids { get; } = [];

    public SdpSimulcast? Simulcast { get; set; }

    public List<SdpSsrcGroup> SsrcGroups { get; } = [];

    public List<SdpSsrc> Ssrcs { get; } = [];

    public int? SctpPort { get; set; }

    public long? MaxMessageSize { get; set; }

    public List<SdpCandidate> Candidates { get; } = [];

    public bool EndOfCandidates { get; set; }

    public List<SdpAttribute> OtherAttributes { get; } = [];

    /// <summary>A port of 0 rejects or disables the section (RFC 8866 §5.14, RFC 8829 §5.3.1).</summary>
    public bool IsRejected => Port == 0;

    public SdpDirection EffectiveDirection => Direction ?? SdpDirection.SendRecv;

    public SdpRtpMap? RtpMapOf(int payloadType) => RtpMaps.FirstOrDefault(r => r.PayloadType == payloadType);

    public string? FmtpOf(int payloadType) =>
        Fmtps.FirstOrDefault(f => f.Format == payloadType.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Parameters;
}

/// <summary><c>a=msid</c> (RFC 8830 §2).</summary>
public sealed record SdpMsid(string StreamId, string? TrackId);

/// <summary><c>a=extmap</c> (RFC 8285 §8).</summary>
public sealed record SdpExtmap(int Id, SdpDirection? Direction, string Uri, string? Attributes);

/// <summary><c>a=rtpmap</c> (RFC 8866 §6.6).</summary>
public sealed record SdpRtpMap(int PayloadType, string EncodingName, int ClockRate, int? Channels);

/// <summary><c>a=fmtp</c> (RFC 8866 §6.15).</summary>
public sealed record SdpFmtp(string Format, string Parameters)
{
    /// <summary><c>key=value;key=value</c> parameters as a case-insensitive map (last value wins).</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Parameters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                result[part] = "";
            }
            else
            {
                result[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }

        return result;
    }
}

/// <summary><c>a=rtcp-fb</c> (RFC 4585 §4.2). <see cref="Format"/> is a payload type or <c>*</c>.</summary>
public sealed record SdpRtcpFb(string Format, string Type, string? Parameter);

/// <summary><c>a=rid</c> (RFC 8851 §10).</summary>
public sealed record SdpRid(string Id, SdpRidDirection Direction, List<int> PayloadTypes, List<KeyValuePair<string, string?>> Restrictions);

public enum SdpRidDirection
{
    Send,
    Recv,
}

/// <summary><c>a=simulcast</c> (RFC 8853 §5.1). Each stream is a list of alternative RIDs.</summary>
public sealed record SdpSimulcast(List<List<SdpSimulcastRid>> Send, List<List<SdpSimulcastRid>> Recv, bool RecvFirst);

public sealed record SdpSimulcastRid(string Id, bool Paused);

/// <summary><c>a=ssrc-group</c> (RFC 5576 §4.2).</summary>
public sealed record SdpSsrcGroup(string Semantics, List<uint> Ssrcs);

/// <summary><c>a=ssrc</c> (RFC 5576 §4.1).</summary>
public sealed record SdpSsrc(uint Ssrc, string Attribute, string? Value);

/// <summary><c>a=candidate</c> (RFC 8839 §5.1).</summary>
public sealed record SdpCandidate(
    string Foundation,
    int Component,
    string Transport,
    uint Priority,
    string Address,
    int Port,
    string Type,
    string? RelatedAddress,
    int? RelatedPort,
    List<KeyValuePair<string, string>> Extensions)
{
    /// <summary>RFC 6544 §4.5: <c>active</c>, <c>passive</c> or <c>so</c> for TCP candidates.</summary>
    public string? TcpType => Extension("tcptype");

    public string? Generation => Extension("generation");

    public string? Ufrag => Extension("ufrag");

    public string? Extension(string name) =>
        Extensions.FirstOrDefault(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
