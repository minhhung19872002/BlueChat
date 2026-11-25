namespace BlueChat.Media.Stun;

/// <summary>STUN message classes (RFC 8489 §5): encoded in bits C1 (0x0100) and C0 (0x0010) of the type.</summary>
public enum StunClass
{
    Request = 0,
    Indication = 1,
    SuccessResponse = 2,
    ErrorResponse = 3,
}

/// <summary>Methods used by BlueChat (RFC 8489 §18.2, RFC 8656 §17).</summary>
public static class StunMethod
{
    public const ushort Binding = 0x001;
    public const ushort Allocate = 0x003;
    public const ushort Refresh = 0x004;
    public const ushort Send = 0x006;
    public const ushort Data = 0x007;
    public const ushort CreatePermission = 0x008;
    public const ushort ChannelBind = 0x009;
}

/// <summary>Attribute types (RFC 8489 §18.3, RFC 8445 §16.1, RFC 8656 §18).</summary>
public static class StunAttr
{
    // RFC 8489 comprehension-required
    public const ushort MappedAddress = 0x0001;
    public const ushort Username = 0x0006;
    public const ushort MessageIntegrity = 0x0008;
    public const ushort ErrorCode = 0x0009;
    public const ushort UnknownAttributes = 0x000A;
    public const ushort Realm = 0x0014;
    public const ushort Nonce = 0x0015;
    public const ushort MessageIntegritySha256 = 0x001C;
    public const ushort PasswordAlgorithm = 0x001D;
    public const ushort UserHash = 0x001E;
    public const ushort XorMappedAddress = 0x0020;

    // RFC 8656 (TURN) comprehension-required
    public const ushort ChannelNumber = 0x000C;
    public const ushort Lifetime = 0x000D;
    public const ushort XorPeerAddress = 0x0012;
    public const ushort Data = 0x0013;
    public const ushort XorRelayedAddress = 0x0016;
    public const ushort RequestedAddressFamily = 0x0017;
    public const ushort EvenPort = 0x0018;
    public const ushort RequestedTransport = 0x0019;
    public const ushort DontFragment = 0x001A;
    public const ushort ReservationToken = 0x0022;

    // RFC 8445 (ICE) comprehension-required
    public const ushort Priority = 0x0024;
    public const ushort UseCandidate = 0x0025;

    // comprehension-optional (0x8000–0xFFFF)
    public const ushort PasswordAlgorithms = 0x8002;
    public const ushort AlternateDomain = 0x8003;
    public const ushort Software = 0x8022;
    public const ushort AlternateServer = 0x8023;
    public const ushort Fingerprint = 0x8028;
    public const ushort IceControlled = 0x8029;
    public const ushort IceControlling = 0x802A;
    public const ushort AdditionalAddressFamily = 0x8000;
    public const ushort AddressErrorCode = 0x8001;
    public const ushort Icmp = 0x8004;

    /// <summary>RFC 8489 §15: 0x0000–0x7FFF must be understood, or the request is rejected with 420.</summary>
    public static bool IsComprehensionRequired(ushort type) => type < 0x8000;
}

/// <summary>Stable codes for malformed input (spec principle 6: protocols fail loudly).</summary>
public enum StunParseError
{
    None,
    TooShort,
    TooLong,
    NotStun,
    BadMagicCookie,
    LengthMismatch,
    LengthNotAligned,
    AttributeOverflow,
    TooManyAttributes,
    FingerprintNotLast,
}
