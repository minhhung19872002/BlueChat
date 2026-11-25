using BlueChat.Media.Dtls;

namespace BlueChat.Media.Srtp;

/// <summary>
/// SRTP/SRTCP for one DTLS-SRTP transport: a protect context built from our master key and salt and an unprotect
/// context built from the peer's (RFC 5764 §4.2). Supported profiles: AEAD_AES_128_GCM (RFC 7714) and
/// AES_CM_128_HMAC_SHA1_80 (RFC 3711 / RFC 5764 §4.1.2). No MKI (WebRTC does not use one).
/// <para>
/// Thread-safety: not thread-safe. Protect calls must be serialized among themselves, and so must unprotect calls
/// (the two directions use separate contexts, so one sender thread and one receiver thread may run concurrently).
/// </para>
/// <para>
/// Buffers: everything is done in place. Protect needs room for <see cref="RtpOverhead"/> /
/// <see cref="RtcpOverhead"/> octets after the packet; unprotect shortens the packet. Steady-state calls do not allocate.
/// </para>
/// </summary>
public sealed class SrtpSession : IDisposable
{
    /// <summary>Largest RTP/RTCP packet accepted (ICE-TCP framing allows 64 KiB; RFC 4571 §2).</summary>
    public const int MaxPacketLength = 65535;

    /// <summary>Default limit of SSRCs per direction (each SSRC holds ROC and replay state).</summary>
    public const int DefaultMaxStreams = 64;

    private readonly SrtpContext _outbound;
    private readonly SrtpContext _inbound;

    public SrtpSession(SrtpKeyMaterial keys, int maxStreams = DefaultMaxStreams)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStreams, 1);
        Profile = keys.Profile;
        _outbound = SrtpContext.FromMaster(keys.Profile, keys.LocalMasterKey, keys.LocalMasterSalt, maxStreams, MaxPacketLength);
        _inbound = SrtpContext.FromMaster(keys.Profile, keys.RemoteMasterKey, keys.RemoteMasterSalt, maxStreams, MaxPacketLength);
    }

    internal SrtpSession(SrtpProfile profile, SrtpContext outbound, SrtpContext inbound)
    {
        Profile = profile;
        _outbound = outbound;
        _inbound = inbound;
    }

    public SrtpProfile Profile { get; }

    /// <summary>Octets added to an RTP packet: 16 (GCM tag) or 10 (HMAC-SHA1-80 tag).</summary>
    public int RtpOverhead => _outbound.RtpOverhead;

    /// <summary>Octets added to an RTCP packet: tag plus the 4-octet ESRTCP word.</summary>
    public int RtcpOverhead => _outbound.RtcpOverhead;

    internal SrtpContext Outbound => _outbound;

    internal SrtpContext Inbound => _inbound;

    /// <summary>Protects the RTP packet in <c>buffer[..length]</c>; the result is <c>buffer[..protectedLength]</c>.</summary>
    public SrtpResult ProtectRtp(Span<byte> buffer, int length, out int protectedLength) =>
        _outbound.ProtectRtp(buffer, length, out protectedLength);

    /// <summary>Verifies and decrypts an SRTP packet in place; the RTP packet is <c>packet[..plainLength]</c>.</summary>
    public SrtpResult UnprotectRtp(Span<byte> packet, out int plainLength) =>
        _inbound.UnprotectRtp(packet, out plainLength);

    /// <summary>Protects the (compound) RTCP packet in <c>buffer[..length]</c> with encryption (E = 1).</summary>
    public SrtpResult ProtectRtcp(Span<byte> buffer, int length, out int protectedLength) =>
        _outbound.ProtectRtcp(buffer, length, out protectedLength);

    /// <summary>Verifies and, when E = 1, decrypts an SRTCP packet in place; the RTCP packet is <c>packet[..plainLength]</c>.</summary>
    public SrtpResult UnprotectRtcp(Span<byte> packet, out int plainLength) =>
        _inbound.UnprotectRtcp(packet, out plainLength);

    public void Dispose()
    {
        _outbound.Dispose();
        _inbound.Dispose();
    }
}
