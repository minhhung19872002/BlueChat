using BlueChat.Media.Dtls;
using BlueChat.Media.Ice;
using BlueChat.Media.Srtp;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Transport;

public enum TransportState
{
    New,
    IceConnected,
    Connected,
    Failed,
    Closed,
}

/// <summary>Receives decrypted media. The span is valid only during the call; the handler may modify it in place.</summary>
public delegate void MediaPacketHandler(PeerTransport transport, Span<byte> packet);

/// <summary>
/// One browser's transport on a media node: ICE-lite agent → DTLS (OpenSSL) → SRTP (spec §2.3, §6.2–6.3). BUNDLE +
/// rtcp-mux, so one transport carries every track of the peer. Received SRTP/SRTCP is decrypted and handed to
/// <see cref="RtpReceived"/>/<see cref="RtcpReceived"/>; <see cref="SendRtp"/>/<see cref="SendRtcp"/> encrypt in place and
/// send on the ICE-selected path only while consent holds (RFC 7675).
/// </summary>
public sealed class PeerTransport : IDisposable
{
    private readonly object _sync = new();
    private readonly MediaNode _node;
    private readonly DtlsSession _dtls;
    private SrtpSession? _srtp;

    internal PeerTransport(MediaNode node, IceCredentials remote, string remoteFingerprint, TimeProvider clock)
    {
        _node = node;
        Ice = new IceLiteAgent(IceCredentials.Generate(), remote, clock);
        _dtls = new DtlsSession(node.Dtls, DtlsRole.Server, remoteFingerprint, node.Options.Mtu);
        _dtls.Start();
        Ice.SelectedChanged += _ => OnSelected();
    }

    public IceLiteAgent Ice { get; }

    public IceCredentials LocalIce => Ice.Local;

    public TransportState State { get; private set; } = TransportState.New;

    public string? FailureReason { get; private set; }

    public IceEndpoint? Selected => Ice.Selected;

    public SrtpProfile? Profile => _srtp?.Profile;

    public long RtpReceivedCount { get; private set; }

    public long SrtpRejectedCount { get; private set; }

    public event MediaPacketHandler? RtpReceived;

    public event MediaPacketHandler? RtcpReceived;

    public event Action<PeerTransport>? StateChanged;

    /// <summary>Bytes a caller must leave free after the RTP/RTCP packet in the buffer passed to Send*.</summary>
    public int SendOverhead => (_srtp?.RtcpOverhead ?? 20) + 4;

    /// <summary>
    /// RFC 8445 §9 ICE restart: new local credentials, new remote ones from the new offer; DTLS and SRTP continue
    /// (the fingerprint does not change). Returns the new local credentials for the answer.
    /// </summary>
    public IceCredentials RestartIce(IceCredentials newRemote)
    {
        var local = IceCredentials.Generate();
        lock (_sync)
        {
            Ice.Restart(local, newRemote);
        }

        _node.RegisterIce(this);
        return local;
    }

    /// <summary>Encrypts <paramref name="length"/> bytes of RTP in place and sends them. False when not sendable.</summary>
    public bool SendRtp(Span<byte> buffer, int length) => Send(buffer, length, rtcp: false);

    public bool SendRtcp(Span<byte> buffer, int length) => Send(buffer, length, rtcp: true);

    private bool Send(Span<byte> buffer, int length, bool rtcp)
    {
        IceEndpoint to;
        int protectedLength;
        lock (_sync)
        {
            if (_srtp is null || Ice.Selected is not { } selected || !Ice.MaySendTo(selected))
            {
                return false;
            }

            var r = rtcp ? _srtp.ProtectRtcp(buffer, length, out protectedLength) : _srtp.ProtectRtp(buffer, length, out protectedLength);
            if (r != SrtpResult.Ok)
            {
                return false;
            }

            to = selected;
        }

        _node.SendTo(to, buffer[..protectedLength]);
        return true;
    }

    internal void HandleStun(in StunMessage message, IceEndpoint from, Span<byte> scratch)
    {
        int n;
        CheckOutcome outcome;
        lock (_sync)
        {
            outcome = Ice.HandleRequest(message, from, scratch, out n);
        }

        if (outcome is CheckOutcome.Answered or CheckOutcome.Rejected)
        {
            _node.SendTo(from, scratch[..n]);
        }

        if (outcome == CheckOutcome.Answered)
        {
            FlushDtls();
        }
    }

    internal void HandleDtls(ReadOnlySpan<byte> datagram, IceEndpoint from)
    {
        // Only from an address that passed an authenticated check (the mux guarantees the endpoint is bound to us).
        bool connectedNow;
        lock (_sync)
        {
            var before = _dtls.State;
            _dtls.Receive(datagram);
            connectedNow = before != DtlsState.Connected && _dtls.State == DtlsState.Connected;
            if (connectedNow)
            {
                _srtp = new SrtpSession(_dtls.Keys!);
            }
        }

        FlushDtls(from);
        if (connectedNow)
        {
            SetState(TransportState.Connected);
        }
        else if (_dtls.State == DtlsState.Failed)
        {
            Fail("dtls: " + _dtls.FailureReason);
        }
    }

    internal void HandleSrtp(Span<byte> packet, bool rtcp)
    {
        int plain;
        lock (_sync)
        {
            if (_srtp is null)
            {
                return;
            }

            var r = rtcp ? _srtp.UnprotectRtcp(packet, out plain) : _srtp.UnprotectRtp(packet, out plain);
            if (r != SrtpResult.Ok)
            {
                SrtpRejectedCount++;
                return;
            }

            if (!rtcp)
            {
                RtpReceivedCount++;
            }
        }

        if (rtcp)
        {
            RtcpReceived?.Invoke(this, packet[..plain]);
        }
        else
        {
            RtpReceived?.Invoke(this, packet[..plain]);
        }
    }

    internal void Tick()
    {
        lock (_sync)
        {
            Ice.Tick();
            _dtls.HandleTimeout();
        }

        FlushDtls();
        if (Ice.State == IceState.Failed && State is not (TransportState.Failed or TransportState.Closed))
        {
            Fail("ice: consent expired");
        }
    }

    private void OnSelected()
    {
        if (State == TransportState.New)
        {
            SetState(TransportState.IceConnected);
        }
    }

    private void FlushDtls(IceEndpoint? to = null)
    {
        IReadOnlyList<byte[]> datagrams;
        lock (_sync)
        {
            datagrams = _dtls.TakeOutgoing();
        }

        var target = to ?? Ice.Selected;
        if (target is null)
        {
            return;
        }

        foreach (var d in datagrams)
        {
            _node.SendTo(target.Value, d);
        }
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        SetState(TransportState.Failed);
    }

    private void SetState(TransportState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this);
    }

    public void Dispose()
    {
        _node.Remove(this);
        lock (_sync)
        {
            Ice.Close();
            _dtls.Dispose();
            _srtp?.Dispose();
            _srtp = null;
        }

        SetState(TransportState.Closed);
    }
}
