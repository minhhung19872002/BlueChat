using System.Buffers.Binary;
using System.Security.Cryptography;
using BlueChat.Media.Rtp;
using BlueChat.Media.Rtp.Payload;
using BlueChat.Media.Rtp.Rtcp;
using BlueChat.Media.Sdp;
using BlueChat.Media.Transport;

namespace MediaLoopback;

/// <summary>
/// Sends every RTP packet of the browser back to it under the server's own SSRCs (declared in the answer), so the
/// browser decodes its own audio and video. Keyframe requests and NACKs from the browser's receiver are turned into
/// requests to the browser's sender (the SFU will do the same between different people in Phase 6).
/// </summary>
internal sealed class EchoSession
{
    private readonly object _sync = new();
    private readonly byte[] _send = new byte[2048];
    private DateTimeOffset _lastPli;

    public EchoSession(PeerTransport transport)
    {
        Transport = transport;
        Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        AudioOut = NewSsrc();
        VideoOut = NewSsrc();
        RtxOut = NewSsrc();
        transport.RtpReceived += OnRtp;
        transport.RtcpReceived += OnRtcp;
    }

    public string Id { get; }

    public PeerTransport Transport { get; }

    public uint AudioOut { get; }

    public uint VideoOut { get; }

    public uint RtxOut { get; }

    public int AudioPt { get; set; } = -1;

    public int VideoPt { get; set; } = -1;

    public int RtxPt { get; set; } = -1;

    public uint? VideoIn { get; private set; }

    public long Echoed { get; private set; }

    public long PliForwarded { get; private set; }

    public long NackForwarded { get; private set; }

    public long KeyFramesSeen { get; private set; }

    /// <summary>Payload types negotiated in our answer: Opus, the first video codec and its RTX (apt).</summary>
    public void LearnPayloadTypes(SdpSession answer)
    {
        foreach (var m in answer.Media.Where(m => !m.IsRejected))
        {
            if (m.Kind == "audio")
            {
                AudioPt = m.RtpMaps.First(r => r.EncodingName.Equals("opus", StringComparison.OrdinalIgnoreCase)).PayloadType;
            }
            else if (m.Kind == "video")
            {
                var codec = m.RtpMaps.First(r => !r.EncodingName.Equals("rtx", StringComparison.OrdinalIgnoreCase));
                VideoPt = codec.PayloadType;
                var rtx = m.RtpMaps.FirstOrDefault(r => r.EncodingName.Equals("rtx", StringComparison.OrdinalIgnoreCase)
                                                        && m.FmtpOf(r.PayloadType)?.Contains($"apt={VideoPt}", StringComparison.Ordinal) == true);
                RtxPt = rtx?.PayloadType ?? -1;
            }
        }
    }

    private void OnRtp(PeerTransport t, Span<byte> packet)
    {
        if (!RtpHeader.TryParse(packet, out var h, out _))
        {
            return;
        }

        uint ssrc;
        if (h.PayloadType == AudioPt)
        {
            ssrc = AudioOut;
        }
        else if (h.PayloadType == VideoPt)
        {
            ssrc = VideoOut;
            VideoIn = h.Ssrc;
            if (Vp8.ReadFrameInfo(packet[h.PayloadOffset..(h.PayloadOffset + h.PayloadLength)]).IsKeyFrame)
            {
                KeyFramesSeen++;
            }
        }
        else if (h.PayloadType == RtxPt)
        {
            ssrc = RtxOut;
        }
        else
        {
            return;
        }

        lock (_sync)
        {
            packet.CopyTo(_send);
            RtpHeader.WriteSsrc(_send, ssrc);
            if (t.SendRtp(_send, packet.Length))
            {
                Echoed++;
            }
        }
    }

    private void OnRtcp(PeerTransport t, Span<byte> packet)
    {
        if (VideoIn is not { } videoIn)
        {
            return;
        }

        Span<ushort> lost = stackalloc ushort[Nack.MaxPairs * 17];
        try
        {
            foreach (var p in new RtcpCompoundReader(packet))
            {
                var bytes = p.Slice(packet);
                if (p.PacketType is not (RtcpPacketType.PayloadFeedback or RtcpPacketType.TransportFeedback))
                {
                    continue;
                }

                var fb = FeedbackHeader.Read(bytes);
                if (fb.MediaSsrc != VideoOut && !(fb.IsFir && Fir.Count(fb.Fci(bytes)) > 0 && Fir.Get(fb.Fci(bytes), 0).Ssrc == VideoOut))
                {
                    continue;
                }

                if (fb.IsPli || fb.IsFir)
                {
                    SendPli(t, videoIn);
                }
                else if (fb.IsNack)
                {
                    var n = Nack.ReadLost(fb.Fci(bytes), lost);
                    SendNack(t, videoIn, lost[..n]);
                }
            }
        }
        catch (RtpParseException)
        {
            // Malformed RTCP from the browser: ignore the rest of this compound packet.
        }
    }

    private void SendPli(PeerTransport t, uint mediaSsrc)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPli < TimeSpan.FromMilliseconds(300))
            {
                return; // spec §6.5: coalesce and rate-limit keyframe requests
            }

            _lastPli = now;
            var n = ReceiverReportHeader(_send);
            n += Pli.Write(_send.AsSpan(n), AudioOut, mediaSsrc);
            if (t.SendRtcp(_send, n))
            {
                PliForwarded++;
            }
        }
    }

    private void SendNack(PeerTransport t, uint mediaSsrc, ReadOnlySpan<ushort> lost)
    {
        if (lost.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            var n = ReceiverReportHeader(_send);
            n += Nack.Write(_send.AsSpan(n), AudioOut, mediaSsrc, lost);
            if (t.SendRtcp(_send, n))
            {
                NackForwarded++;
            }
        }
    }

    /// <summary>RFC 3550 §6.1: a compound packet starts with SR or RR; an empty RR (RC = 0) from our SSRC.</summary>
    private int ReceiverReportHeader(Span<byte> dest)
    {
        dest[0] = 0x80;
        dest[1] = RtcpPacketType.ReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(dest[2..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], AudioOut);
        return 8;
    }

    private static uint NewSsrc() => BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)) | 1;
}
