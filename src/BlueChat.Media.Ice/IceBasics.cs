using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace BlueChat.Media.Ice;

public enum IceTransport
{
    Udp,
    Tcp,
}

/// <summary>Where a packet came from: transport, remote address, and for TCP the connection it arrived on.</summary>
public readonly record struct IceEndpoint(IceTransport Transport, IPEndPoint Remote, long ConnectionId = 0)
{
    public override string ToString() =>
        Transport == IceTransport.Udp ? $"udp {Remote}" : $"tcp {Remote} #{ConnectionId}";
}

/// <summary>ICE username fragment and password (RFC 8445 §5.3).</summary>
public sealed record IceCredentials(string Ufrag, string Pwd)
{
    private const string IceChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>
    /// §5.3: ufrag ≥ 24 bits of randomness and 4–256 ice-chars; pwd ≥ 128 bits and 22–256 ice-chars.
    /// We use 8 characters (48 bits) and 24 characters (144 bits), CSPRNG.
    /// </summary>
    public static IceCredentials Generate() => new(Random(8), Random(24));

    public static bool IsValidUfrag(string s) => s.Length is >= 4 and <= 256 && s.All(IceChars.Contains);

    public static bool IsValidPwd(string s) => s.Length is >= 22 and <= 256 && s.All(IceChars.Contains);

    private static string Random(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = IceChars[RandomNumberGenerator.GetInt32(IceChars.Length)];
            }
        });
}

/// <summary>Host candidate lines for the answer (RFC 8839 §5.1 grammar, RFC 8445 §5.1.2 priority, RFC 6544 TCP).</summary>
public static class IceCandidates
{
    private const int HostTypePreference = 126;

    /// <summary>RFC 8445 §5.1.2.1: (2^24)·type + (2^8)·local + (256 − component).</summary>
    public static uint Priority(int typePreference, int localPreference, int component) =>
        (uint)((typePreference << 24) + (localPreference << 8) + (256 - component));

    /// <summary>UDP host candidate (component 1: BUNDLE + rtcp-mux).</summary>
    public static string HostUdp(string foundation, IPAddress address, int port) =>
        string.Create(CultureInfo.InvariantCulture,
            $"candidate:{foundation} 1 udp {Priority(HostTypePreference, 65535, 1)} {Format(address)} {port} typ host");

    /// <summary>
    /// RFC 6544 §4.5 passive TCP host candidate. §4.2: local preference = 2^13·direction + other; passive host
    /// direction preference 4, so UDP (local preference 65535) stays preferred.
    /// </summary>
    public static string HostTcpPassive(string foundation, IPAddress address, int port) =>
        string.Create(CultureInfo.InvariantCulture,
            $"candidate:{foundation} 1 tcp {Priority(HostTypePreference, (4 << 13) + 8191, 1)} {Format(address)} {port} typ host tcptype passive");

    private static string Format(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 ? a.ToString() : a.MapToIPv4().ToString();
}

/// <summary>RFC 7983 §7 first-byte demultiplexing of everything that arrives on a media port.</summary>
public enum PacketKind
{
    Unknown,
    Stun,
    Zrtp,
    Dtls,
    TurnChannel,
    Rtp,
    Rtcp,
}

public static class PacketClassifier
{
    public static PacketKind Classify(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            return PacketKind.Unknown;
        }

        var b = packet[0];
        return b switch
        {
            <= 3 => PacketKind.Stun,
            >= 16 and <= 19 => PacketKind.Zrtp,
            >= 20 and <= 63 => PacketKind.Dtls,
            >= 64 and <= 79 => PacketKind.TurnChannel,
            >= 128 and <= 191 => packet.Length >= 2 && packet[1] is >= 192 and <= 223 ? PacketKind.Rtcp : PacketKind.Rtp,
            _ => PacketKind.Unknown,
        };
    }
}
