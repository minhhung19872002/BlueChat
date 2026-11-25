using BlueChat.Media.Rtp.Payload;
using BlueChat.Media.Rtp.Rtcp;

namespace BlueChat.Media.Rtp.Tests;

/// <summary>
/// Mutation fuzzing of every reader with the Pion vectors as seeds (spec §0 rule 8): flips, truncations, insertions
/// and random bytes. Only <see cref="RtpParseException"/> may escape.
/// </summary>
public class FuzzTests
{
    private static readonly ushort[] NackBuffer = new ushort[Nack.MaxPairs * 17];
    private static readonly TwccPacketStatus[] TwccBuffer = new TwccPacketStatus[65536];

    [Fact]
    public void Mutated_inputs_only_raise_RtpParseException()
    {
        var rng = new Random(20261125);
        var seeds = Vectors.Pion.Select(v => v.Bytes).ToList();
        var runs = 0;
        foreach (var seed in seeds)
        {
            for (var i = 0; i < 1500; i++)
            {
                var data = Mutate(seed, rng);
                Run(data);
                runs++;
            }
        }

        for (var i = 0; i < 5000; i++)
        {
            var data = new byte[rng.Next(0, 64)];
            rng.NextBytes(data);
            Run(data);
            runs++;
        }

        runs.Should().BeGreaterThan(70_000);
    }

    private static byte[] Mutate(byte[] seed, Random rng)
    {
        var d = seed.ToList();
        var ops = rng.Next(1, 6);
        for (var k = 0; k < ops && d.Count > 0; k++)
        {
            switch (rng.Next(5))
            {
                case 0:
                    d[rng.Next(d.Count)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1:
                    d[rng.Next(d.Count)] = (byte)rng.Next(256);
                    break;
                case 2:
                    d.RemoveRange(rng.Next(d.Count), 1);
                    break;
                case 3:
                    d.Insert(rng.Next(d.Count + 1), (byte)rng.Next(256));
                    break;
                default:
                {
                    var cut = rng.Next(d.Count);
                    d.RemoveRange(cut, d.Count - cut);
                    break;
                }
            }
        }

        return d.ToArray();
    }

    private static void Run(byte[] data)
    {
        Try(() => Rtp(data));
        Try(() => Rtcp(data));
        Try(() => Vp8.ReadFrameInfo(data));
        Try(() => Vp9.ReadFrameInfo(data));
        Try(() => H264.Read(data));
        Try(() => Av1.Read(data));
        Try(() => OpusToc.Read(data));
        Try(() => RtpExtensions.ReadDependencyDescriptor(data));
        Try(() => RtpExtensions.ReadStreamId(data));
        if (data.Length >= 8)
        {
            Try(() => TransportCc.Read(data, TwccBuffer));
            Try(() => Nack.ReadLost(data, NackBuffer));
            Try(() => Remb.Read(data));
        }
    }

    private static void Try(Action a)
    {
        try
        {
            a();
        }
        catch (RtpParseException)
        {
        }
    }

    private static void Rtp(byte[] data)
    {
        if (!RtpHeader.TryParse(data, out var h, out _))
        {
            return;
        }

        foreach (var e in new HeaderExtensionReader(data, h))
        {
            _ = data.AsSpan(e.Offset, e.Length).Length;
        }

        for (var i = 0; i < h.CsrcCount; i++)
        {
            _ = h.GetCsrc(data, i);
        }

        var payload = h.Payload(data).ToArray();
        Try(() => Vp8.ReadFrameInfo(payload));
        Try(() => H264.Read(payload));
    }

    private static void Rtcp(byte[] data)
    {
        foreach (var p in new RtcpCompoundReader(data))
        {
            var pkt = data.AsSpan(p.Offset, p.Length).ToArray();
            switch (p.PacketType)
            {
                case RtcpPacketType.SenderReport:
                {
                    var sr = SenderReport.Read(pkt);
                    for (var i = 0; i < sr.ReportCount; i++)
                    {
                        _ = SenderReport.GetReportBlock(pkt, i);
                    }

                    break;
                }

                case RtcpPacketType.ReceiverReport:
                {
                    var rr = ReceiverReport.Read(pkt);
                    for (var i = 0; i < rr.ReportCount; i++)
                    {
                        _ = ReceiverReport.GetReportBlock(pkt, i);
                    }

                    break;
                }

                case RtcpPacketType.SourceDescription:
                    foreach (var item in new SdesReader(pkt))
                    {
                        _ = pkt.AsSpan(item.Offset, item.Length).Length;
                    }

                    break;
                case RtcpPacketType.Goodbye:
                    for (var i = 0; i < Goodbye.SourceCount(pkt); i++)
                    {
                        _ = Goodbye.GetSource(pkt, i);
                    }

                    _ = Goodbye.Reason(pkt).Length;
                    break;
                case RtcpPacketType.Application:
                {
                    var app = ApplicationPacket.Read(pkt);
                    _ = pkt.AsSpan(app.DataOffset, app.DataLength).Length;
                    break;
                }

                case RtcpPacketType.TransportFeedback or RtcpPacketType.PayloadFeedback:
                {
                    var f = FeedbackHeader.Read(pkt);
                    var fci = pkt.AsSpan(f.FciOffset, f.FciLength).ToArray();
                    if (f.IsNack)
                    {
                        Nack.ReadLost(fci, NackBuffer);
                    }
                    else if (f.IsTransportCc)
                    {
                        TransportCc.Read(fci, TwccBuffer);
                    }
                    else if (f.IsFir)
                    {
                        for (var i = 0; i < Fir.Count(fci); i++)
                        {
                            _ = Fir.Get(fci, i);
                        }
                    }
                    else if (f.IsApplication && Remb.IsRemb(fci))
                    {
                        var (_, count) = Remb.Read(fci);
                        for (var i = 0; i < count; i++)
                        {
                            _ = Remb.GetSsrc(fci, i);
                        }
                    }

                    break;
                }
            }
        }
    }
}
