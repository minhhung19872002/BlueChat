using System.Text.Json;
using System.Text.Json.Nodes;
using BlueChat.Media.Rtp.Rtcp;

namespace BlueChat.Media.Rtp.Tests;

/// <summary>
/// Builds packets with BlueChat writers, reads them back with our readers, and writes Vectors/ours.json for the Pion
/// verifier (<c>go run . verify &lt; ../ours.json</c>), which parses each packet with Pion and compares to "expect".
/// </summary>
public class WriterCrossCheckTests
{
    [Fact]
    public void Writers_round_trip_and_emit_vectors_for_Pion()
    {
        var vectors = new JsonArray();
        var buf = new byte[16384];

        // ---- RTP ----
        void Rtp(string name, RtpHeaderFields f, uint[] csrcs, bool twoByte, (byte Id, byte[] Data)[] exts, byte[] payload, byte padding)
        {
            var w = new RtpPacketWriter(buf, f, csrcs, twoByte);
            foreach (var (id, data) in exts)
            {
                w.AddExtension(id, data);
            }

            w.WritePayload(payload);
            var len = w.Finish(padding);
            var bytes = buf[..len];
            var h = RtpHeader.Parse(bytes);
            h.Marker.Should().Be(f.Marker);
            h.SequenceNumber.Should().Be(f.SequenceNumber);
            h.Payload(bytes).ToArray().Should().Equal(payload);
            h.PaddingLength.Should().Be(padding);
            var read = new List<(byte, byte[])>();
            foreach (var x in new HeaderExtensionReader(bytes, h))
            {
                read.Add((x.Id, bytes.AsSpan(x.Offset, x.Length).ToArray()));
            }

            read.Select(r => (r.Item1, Convert.ToHexString(r.Item2))).Should().Equal(exts.Select(x => (x.Id, Convert.ToHexString(x.Data))));
            var extJson = new JsonObject();
            foreach (var (id, data) in exts)
            {
                extJson[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Convert.ToHexStringLower(data);
            }

            vectors.Add(Vector("rtp", name, bytes, new JsonObject
            {
                ["marker"] = f.Marker, ["pt"] = f.PayloadType, ["seq"] = f.SequenceNumber, ["ts"] = f.Timestamp, ["ssrc"] = f.Ssrc,
                ["payload"] = Convert.ToHexStringLower(payload), ["padding"] = padding, ["extensions"] = extJson,
            }));
        }

        var abs = new byte[3];
        RtpExtensions.WriteAbsSendTime(abs, 0xABCDEF);
        var lvl = new byte[1];
        RtpExtensions.WriteAudioLevel(lvl, true, 30);
        var tcc = new byte[2];
        RtpExtensions.WriteTransportSequenceNumber(tcc, 65535);
        var dd = new byte[3];
        RtpExtensions.WriteDependencyDescriptor(dd, new DependencyDescriptorMandatory(true, false, 5, 1000));
        Rtp("plain", new RtpHeaderFields(false, 111, 0, 0, 1), [], false, [], [1, 2, 3], 0);
        Rtp("csrc-marker", new RtpHeaderFields(true, 96, 65535, uint.MaxValue, 0xDEADBEEF), [1, 2, 3, 4], false, [], new byte[300], 0);
        Rtp("one-byte", new RtpHeaderFields(false, 111, 42, 960, 7), [], false,
            [(1, "a1"u8.ToArray()), (2, abs), (3, lvl), (4, tcc), (11, dd), (14, new byte[16])], [0xF8, 0xFF, 0xFE], 0);
        Rtp("two-byte", new RtpHeaderFields(false, 96, 1, 2, 3), [9], true,
            [(1, "m"u8.ToArray()), (17, new byte[40]), (255, [1]), (3, [])], [5, 6], 0);
        Rtp("padding", new RtpHeaderFields(true, 96, 9, 9, 9), [], false, [(5, tcc)], [7, 7, 7], 13);

        // ---- RTCP ----
        void Rtcp(string name, int len, JsonObject expect) => vectors.Add(Vector("rtcp", name, buf[..len], expect));

        var block = new ReportBlock(0x11, 64, -5, 0x2FFFF, 77, 0x12345678, 0x1000);
        var n = new SenderReport(0xA1, 0xE5F0_1234_5678_9ABC, 90000, 12, 3456, 1).Write(buf, [block]);
        SenderReport.GetReportBlock(buf.AsSpan(0, n), 0).Should().Be(block);
        Rtcp("sr", n, new JsonObject { ["types"] = Arr(200), ["ssrc"] = 0xA1, ["ntp"] = 0xE5F0_1234_5678_9ABCUL.ToString(System.Globalization.CultureInfo.InvariantCulture), ["packets"] = 12, ["blocks"] = 1 });

        n = ReceiverReport.Write(buf, 0xB2, [block, block with { Ssrc = 0x12 }]);
        ReceiverReport.Read(buf.AsSpan(0, n)).ReportCount.Should().Be(2);
        Rtcp("rr", n, new JsonObject { ["types"] = Arr(201), ["ssrc"] = 0xB2, ["blocks"] = 2, ["fraction"] = 64, ["jitter"] = 77 });

        n = SdesReader.WriteCname(buf, 0xC3, "user@bluechat");
        Rtcp("sdes", n, new JsonObject { ["types"] = Arr(202), ["cname"] = "user@bluechat" });

        n = Goodbye.Write(buf, [1, 2, 3], "bye now");
        Goodbye.SourceCount(buf.AsSpan(0, n)).Should().Be(3);
        Rtcp("bye", n, new JsonObject { ["types"] = Arr(203), ["sources"] = Arr(1, 2, 3), ["reason"] = "bye now" });

        ushort[] lost = [65530, 65531, 65535, 0, 9, 10, 26, 27, 400];
        n = Nack.Write(buf, 1, 2, lost);
        var back = new ushort[64];
        var count = Nack.ReadLost(FeedbackHeader.Read(buf.AsSpan(0, n)).Fci(buf.AsSpan(0, n)), back);
        back[..count].Should().Equal(lost);
        Rtcp("nack", n, new JsonObject { ["types"] = Arr(205), ["lost"] = Arr(lost.Select(x => (long)x).ToArray()) });

        n = Pli.Write(buf, 1, 0x77);
        Rtcp("pli", n, new JsonObject { ["types"] = Arr(206), ["media"] = 0x77 });

        n = Fir.Write(buf, 1, [new FirEntry(9, 1), new FirEntry(10, 255)]);
        Rtcp("fir", n, new JsonObject
        {
            ["types"] = Arr(206), ["entries"] = new JsonArray(Arr(9, 1), Arr(10, 255)),
        });

        n = Remb.Write(buf, 1, 2_000_000, [5, 6, 7]);
        Remb.Read(FeedbackHeader.Read(buf.AsSpan(0, n)).Fci(buf.AsSpan(0, n))).Should().Be((2_000_000UL, 3));
        Rtcp("remb", n, new JsonObject { ["types"] = Arr(206), ["bitrate"] = 2_000_000, ["ssrcs"] = Arr(5, 6, 7) });

        // Compound RR + SDES + NACK + PLI built by us.
        var c = ReceiverReport.Write(buf, 0xB2, [block]);
        c += SdesReader.WriteCname(buf.AsSpan(c), 0xB2, "cname");
        c += Nack.Write(buf.AsSpan(c), 0xB2, 2, [100, 102]);
        c += Pli.Write(buf.AsSpan(c), 0xB2, 2);
        RtcpCompoundReader.Validate(buf.AsSpan(0, c), allowReducedSize: false).Should().Be(4);
        Rtcp("compound", c, new JsonObject
        {
            ["types"] = Arr(201, 202, 205, 206), ["ssrc"] = 0xB2, ["blocks"] = 1, ["fraction"] = 64, ["jitter"] = 77,
            ["cname"] = "cname", ["lost"] = Arr(100, 102), ["media"] = 2,
        });

        // ---- transport-cc ----
        void Twcc(string name, ushort baseSeq, TwccPacketStatus[] statuses)
        {
            var len = TransportCc.Write(buf, 0x10, 0x20, baseSeq, 0x123456, 9, statuses);
            var span = buf.AsSpan(0, len);
            RtcpCompoundReader.Validate(span, allowReducedSize: true).Should().Be(1);
            var readBack = new TwccPacketStatus[statuses.Length];
            var fb = TransportCc.Read(FeedbackHeader.Read(span).Fci(span), readBack);
            fb.StatusCount.Should().Be(statuses.Length);
            readBack.Should().Equal(statuses);
            vectors.Add(Vector("twcc", name, span.ToArray(), new JsonObject
            {
                ["sender"] = 0x10, ["media"] = 0x20, ["base"] = baseSeq, ["count"] = statuses.Length, ["refTime"] = 0x123456,
                ["fbCount"] = 9, ["symbols"] = Arr(statuses.Select(s => (long)s.Symbol).ToArray()),
                ["deltas"] = Arr(statuses.Select(s => (long)s.DeltaTicks).ToArray()),
            }));
        }

        Twcc("all-small", 65530, Statuses(65530, Enumerable.Range(0, 30).Select(i => (true, 20 + i % 5)).ToArray()));
        Twcc("long-run-lost", 100, Statuses(100, Enumerable.Range(0, 40).Select(i => (i is < 5 or >= 35, 4)).ToArray()));
        Twcc("large-and-negative", 7, Statuses(7, [(true, 0), (true, -40), (false, 0), (true, 1000), (true, 255), (true, 256),
            (false, 0), (true, -32768), (true, 32767), (true, 3)]));
        var rng = new Random(11);
        Twcc("random-200", 5000, Statuses(5000, Enumerable.Range(0, 200)
            .Select(_ => (rng.Next(10) > 1, rng.Next(10) == 0 ? rng.Next(-500, 2000) : rng.Next(0, 80))).ToArray()));
        Twcc("single", 1, Statuses(1, [(true, 1)]));
        Twcc("run-8191-plus", 0, Statuses(0, Enumerable.Range(0, 8200).Select(_ => (true, 2)).ToArray()));

        var json = vectors.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(Path.Combine(Vectors.SourceFolder(), "ours.json"), json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static TwccPacketStatus[] Statuses(ushort baseSeq, (bool Received, int Delta)[] items) =>
        items.Select((x, i) => new TwccPacketStatus((ushort)(baseSeq + i),
            !x.Received ? TwccSymbol.NotReceived : x.Delta is >= 0 and <= 255 ? TwccSymbol.SmallDelta : TwccSymbol.LargeOrNegativeDelta,
            x.Received ? x.Delta : 0)).ToArray();

    private static JsonArray Arr(params long[] values) => new(values.Select(v => (JsonNode)v).ToArray());

    private static JsonObject Vector(string kind, string name, byte[] bytes, JsonObject expect) => new()
    {
        ["kind"] = kind, ["name"] = name, ["hex"] = Convert.ToHexStringLower(bytes), ["expect"] = expect,
    };
}
