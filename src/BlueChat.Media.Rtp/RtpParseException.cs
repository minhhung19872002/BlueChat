namespace BlueChat.Media.Rtp;

/// <summary>
/// The one exception every reader in this library throws for bad input. <see cref="Code"/> is stable
/// (snake_case) so callers and logs can branch on it; the message is for humans.
/// </summary>
public sealed class RtpParseException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    internal static RtpParseException Truncated(string what) => new("truncated", what + ": packet too short");

    internal static RtpParseException Invalid(string code, string what) => new(code, what);
}
