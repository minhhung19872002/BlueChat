using System.Net;

namespace BlueChat.Turn;

/// <summary>Configuration of one TURN server (RFC 8656). Every input the server accepts is bounded by these values.</summary>
public sealed class TurnOptions
{
    /// <summary>REALM sent in 401/438 (RFC 8489 §9.2).</summary>
    public string Realm { get; set; } = "bluechat";

    /// <summary>
    /// Shared secret of the time-limited credential scheme (draft-uberti-behave-turn-rest-00 §2.2): username
    /// "&lt;expiry unix seconds&gt;:&lt;user id&gt;", password base64(HMAC-SHA1(secret, username)). The Api issues these
    /// with the meeting ticket; the TURN server stores no user list.
    /// </summary>
    public byte[] SharedSecret { get; set; } = [];

    /// <summary>Key for signing nonces (stateless nonces). Random per process when left empty.</summary>
    public byte[] NonceKey { get; set; } = [];

    public TimeSpan NonceLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>RFC 8656 §7.2: default allocation lifetime 600 s.</summary>
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary>RFC 8656 §7.2: maximum allocation lifetime (3600 s recommended).</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromSeconds(3600);

    /// <summary>RFC 8656 §9: permission lifetime 300 s (not negotiable).</summary>
    public TimeSpan PermissionLifetime { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>RFC 8656 §12: channel binding lifetime 600 s.</summary>
    public TimeSpan ChannelLifetime { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary>RFC 8656 §12: after a binding expires, the number and the peer are not rebound differently for 5 min.</summary>
    public TimeSpan ChannelCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Address put in XOR-RELAYED-ADDRESS (public address of the relay); the relay socket's own address if null.</summary>
    public IPAddress? RelayAdvertisedAddress { get; set; }

    /// <summary>Which peers may be reached. Default: allow-list with no entries (nothing is relayed until configured).</summary>
    public PeerAddressPolicy PeerPolicy { get; set; } = PeerAddressPolicy.AllowList();

    public int MaxAllocations { get; set; } = 10_000;

    public int MaxAllocationsPerUser { get; set; } = 10;

    public int MaxAllocationsPerClientIp { get; set; } = 50;

    public int MaxPermissionsPerAllocation { get; set; } = 32;

    public int MaxChannelsPerAllocation { get; set; } = 64;

    /// <summary>Relayed bytes per second per allocation, both directions together (token bucket).</summary>
    public long BandwidthBytesPerSecond { get; set; } = 512 * 1024;

    public long BandwidthBurstBytes { get; set; } = 256 * 1024;

    /// <summary>Largest STUN message or ChannelData message accepted from a client.</summary>
    public int MaxMessageSize { get; set; } = 4096;

    /// <summary>Largest UDP payload relayed from a peer; larger datagrams are dropped.</summary>
    public int MaxPeerPayload { get; set; } = 2048;

    /// <summary>Longest USERNAME accepted (RFC 8489 §14.3: &lt; 509 bytes).</summary>
    public int MaxUsernameLength { get; set; } = 508;

    public string Software { get; set; } = "BlueChat TURN";
}
