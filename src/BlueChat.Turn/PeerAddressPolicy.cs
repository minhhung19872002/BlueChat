using System.Net;
using System.Net.Sockets;

namespace BlueChat.Turn;

public enum PeerPolicyMode
{
    /// <summary>Only addresses inside the configured networks (BlueChat's own SFU nodes). BlueChat default.</summary>
    AllowList,

    /// <summary>Any public unicast address; private, shared, loopback and link-local ranges refused.</summary>
    DenyPrivate,
}

/// <summary>
/// Which peer addresses a client may create permissions/channels for or send to (RFC 8656 §21.3, spec §6.6:
/// the relay must never reach internal addresses). Some ranges are refused in every mode, even when listed:
/// unspecified, multicast, broadcast, reserved, and cloud metadata. In <see cref="PeerPolicyMode.AllowList"/> an
/// explicitly listed network may be private or loopback (an SFU node on the internal network).
/// </summary>
public sealed class PeerAddressPolicy
{
    private static readonly IPNetwork[] AlwaysForbidden =
    [
        IPNetwork.Parse("0.0.0.0/8"),          // "this network" (RFC 1122)
        IPNetwork.Parse("169.254.169.254/32"), // cloud instance metadata
        IPNetwork.Parse("224.0.0.0/4"),        // multicast
        IPNetwork.Parse("240.0.0.0/4"),        // reserved + 255.255.255.255 broadcast
        IPNetwork.Parse("::/128"),             // unspecified
        IPNetwork.Parse("ff00::/8"),           // IPv6 multicast
        IPNetwork.Parse("fd00:ec2::254/128"),  // AWS IPv6 metadata
    ];

    private static readonly IPNetwork[] Internal =
    [
        IPNetwork.Parse("10.0.0.0/8"),     // RFC 1918
        IPNetwork.Parse("172.16.0.0/12"),  // RFC 1918
        IPNetwork.Parse("192.168.0.0/16"), // RFC 1918
        IPNetwork.Parse("100.64.0.0/10"),  // CGNAT shared space (RFC 6598)
        IPNetwork.Parse("127.0.0.0/8"),    // loopback
        IPNetwork.Parse("169.254.0.0/16"), // link-local (RFC 3927)
        IPNetwork.Parse("192.0.0.0/24"),   // IETF protocol assignments (RFC 6890)
        IPNetwork.Parse("198.18.0.0/15"),  // benchmarking (RFC 2544)
        IPNetwork.Parse("::1/128"),        // IPv6 loopback
        IPNetwork.Parse("fe80::/10"),      // IPv6 link-local
        IPNetwork.Parse("fec0::/10"),      // deprecated site-local
        IPNetwork.Parse("fc00::/7"),       // unique local (RFC 4193)
    ];

    private readonly IPNetwork[] _allowed;

    private PeerAddressPolicy(PeerPolicyMode mode, IPNetwork[] allowed)
    {
        Mode = mode;
        _allowed = allowed;
    }

    public PeerPolicyMode Mode { get; }

    public IReadOnlyList<IPNetwork> Allowed => _allowed;

    public static PeerAddressPolicy AllowList(params IPNetwork[] networks) => new(PeerPolicyMode.AllowList, networks);

    public static PeerAddressPolicy AllowList(IEnumerable<string> cidrs) =>
        new(PeerPolicyMode.AllowList, cidrs.Select(IPNetwork.Parse).ToArray());

    public static PeerAddressPolicy DenyPrivate() => new(PeerPolicyMode.DenyPrivate, []);

    public bool IsAllowed(IPAddress address)
    {
        var a = Canonical(address);
        if (a is null || In(AlwaysForbidden, a))
        {
            return false;
        }

        return Mode == PeerPolicyMode.AllowList ? In(_allowed, a) : !In(Internal, a);
    }

    /// <summary>
    /// Reduces IPv6 forms that embed an IPv4 address (IPv4-mapped ::ffff:0:0/96, IPv4-compatible ::/96, NAT64
    /// 64:ff9b::/96, 6to4 2002::/16) to that IPv4 address, so the same ranges apply. Null for scoped/odd forms.
    /// </summary>
    internal static IPAddress? Canonical(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out _);
        var firstTwelveZero = b[..12].IndexOfAnyExcept((byte)0) < 0;
        if (firstTwelveZero && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
        {
            return new IPAddress(b[12..]); // IPv4-compatible (deprecated)
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return new IPAddress(b[12..]); // NAT64 well-known prefix (RFC 6052)
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b[2..6]); // 6to4 (RFC 3056)
        }

        return new IPAddress(b); // drop any scope id
    }

    private static bool In(IPNetwork[] networks, IPAddress a)
    {
        foreach (var n in networks)
        {
            if (n.BaseAddress.AddressFamily == a.AddressFamily && n.Contains(a))
            {
                return true;
            }
        }

        return false;
    }
}
