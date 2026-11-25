namespace BlueChat.Media.Sdp;

/// <summary>
/// Malformed or oversized SDP. <see cref="Code"/> is stable (for API errors and tests); <see cref="LineNumber"/> is
/// 1-based, 0 when the whole document is at fault.
/// </summary>
public sealed class SdpParseException(string code, int lineNumber, string message)
    : Exception($"SDP {code} at line {lineNumber}: {message}")
{
    public string Code { get; } = code;

    public int LineNumber { get; } = lineNumber;
}

/// <summary>Offer cannot be answered (missing fingerprint, no BUNDLE-able section…).</summary>
public sealed class SdpNegotiationException(string code, string message) : Exception($"SDP negotiation {code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>Input limits (spec principle 8). Browser offers are a few KB; the defaults leave ample room.</summary>
public sealed record SdpLimits
{
    public static readonly SdpLimits Default = new();

    public int MaxBytes { get; init; } = 64 * 1024;

    public int MaxLines { get; init; } = 4096;

    public int MaxLineLength { get; init; } = 4096;

    public int MaxMediaSections { get; init; } = 128;

    public int MaxAttributesPerSection { get; init; } = 1024;
}
