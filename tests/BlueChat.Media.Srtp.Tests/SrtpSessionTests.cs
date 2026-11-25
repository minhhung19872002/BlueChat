using System.Buffers.Binary;
using BlueChat.Media.Dtls;

namespace BlueChat.Media.Srtp.Tests;

public class SrtpSessionTests
{
    public static TheoryData<SrtpProfile> Profiles => new() { SrtpProfile.AeadAes128Gcm, SrtpProfile.Aes128CmHmacSha1x80 };

    private static (SrtpSession A, SrtpSession B) Pair(SrtpProfile profile, int maxStreams = SrtpSession.DefaultMaxStreams)
    {
        var keys = TestPackets.Keys(profile);
        return (new SrtpSession(keys, maxStreams), new SrtpSession(TestPackets.Mirror(keys), maxStreams));
    }

    private static byte[] Protect(SrtpSession s, byte[] plain)
    {
        var buffer = new byte[plain.Length + s.RtpOverhead];
        plain.CopyTo(buffer, 0);
        s.ProtectRtp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);
        return buffer[..n];
    }

    private static byte[] ProtectRtcp(SrtpSession s, byte[] plain)
    {
        var buffer = new byte[plain.Length + s.RtcpOverhead];
        plain.CopyTo(buffer, 0);
        s.ProtectRtcp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);
        return buffer[..n];
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Round_trip_RTP_and_RTCP_both_directions(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            var plain = TestPackets.Rtp(100, 0xAABBCCDD, "hello media"u8);
            var wire = Protect(a, plain);
            wire.Length.Should().Be(plain.Length + (profile == SrtpProfile.AeadAes128Gcm ? 16 : 10));
            wire.AsSpan(12, 11).ToArray().Should().NotEqual("hello media"u8.ToArray(), "the payload is encrypted");
            b.UnprotectRtp(wire, out var n).Should().Be(SrtpResult.Ok);
            wire[..n].Should().Equal(plain);

            var back = Protect(b, plain);
            a.UnprotectRtp(back, out n).Should().Be(SrtpResult.Ok);
            back[..n].Should().Equal(plain);

            var rr = TestPackets.ReceiverReport(0xAABBCCDD, 100);
            var srtcp = ProtectRtcp(a, rr);
            b.UnprotectRtcp(srtcp, out n).Should().Be(SrtpResult.Ok);
            srtcp[..n].Should().Equal(rr);
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Sender_and_receiver_roll_the_ROC_over_at_sequence_wrap(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            foreach (var seq in new ushort[] { 65533, 65534, 65535, 0, 1, 2 })
            {
                var plain = TestPackets.Rtp(seq, 1, new byte[] { (byte)seq });
                var wire = Protect(a, plain);
                b.UnprotectRtp(wire, out var n).Should().Be(SrtpResult.Ok, $"seq {seq}");
                wire[..n].Should().Equal(plain);
            }

            a.Outbound.RtpState(1).Should().Be((1u, (ushort)2));
            b.Inbound.RtpState(1).Should().Be((1u, (ushort)2));
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Late_packet_from_before_the_wrap_uses_the_previous_ROC(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            var w65534 = Protect(a, TestPackets.Rtp(65534, 1, [0]));
            var w65535 = Protect(a, TestPackets.Rtp(65535, 1, [1]));
            var w0 = Protect(a, TestPackets.Rtp(0, 1, [2]));
            var w1 = Protect(a, TestPackets.Rtp(1, 1, [3]));

            b.UnprotectRtp(w65534, out _).Should().Be(SrtpResult.Ok);
            b.UnprotectRtp(w0, out _).Should().Be(SrtpResult.Ok);
            b.UnprotectRtp(w1, out _).Should().Be(SrtpResult.Ok);
            b.UnprotectRtp(w65535, out var n).Should().Be(SrtpResult.Ok, "reordered across the wrap, ROC estimated as ROC-1");
            w65535[n - 1].Should().Be(1);
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Reordering_inside_the_window_is_accepted_and_replays_are_rejected(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            var wires = Enumerable.Range(1, 200).Select(i => Protect(a, TestPackets.Rtp((ushort)i, 7, [(byte)i]))).ToList();

            b.UnprotectRtp(wires[199].ToArray(), out _).Should().Be(SrtpResult.Ok); // seq 200
            b.UnprotectRtp(wires[149].ToArray(), out _).Should().Be(SrtpResult.Ok); // seq 150: 50 behind, in window
            b.UnprotectRtp(wires[149].ToArray(), out _).Should().Be(SrtpResult.Replayed);
            b.UnprotectRtp(wires[199].ToArray(), out _).Should().Be(SrtpResult.Replayed);
            b.UnprotectRtp(wires[72].ToArray(), out _).Should().Be(SrtpResult.Ok, "seq 73 is 127 behind, the oldest slot of the window");
            b.UnprotectRtp(wires[71].ToArray(), out _).Should().Be(SrtpResult.Replayed, "seq 72 is 128 behind, outside the 128-packet window");
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Tampered_packets_fail_and_do_not_move_the_state(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            var good = Protect(a, TestPackets.Rtp(10, 9, "payload"u8));
            foreach (var position in new[] { 1, 13, good.Length - 1 }) // header (AAD), payload, tag
            {
                var bad = good.ToArray();
                bad[position] ^= 0x40;
                b.UnprotectRtp(bad, out _).Should().Be(SrtpResult.AuthenticationFailed, $"flip at {position}");
            }

            // A forged packet far ahead must not have advanced the window or ROC.
            var forged = Protect(a, TestPackets.Rtp(30010, 9, "x"u8)); // 30000 ahead: far outside the window, same ROC
            forged[^1] ^= 1;
            b.UnprotectRtp(forged, out _).Should().Be(SrtpResult.AuthenticationFailed);
            b.UnprotectRtp(good, out _).Should().Be(SrtpResult.Ok);

            var rtcp = ProtectRtcp(a, TestPackets.ReceiverReport(9, 1));
            rtcp[10] ^= 1;
            b.UnprotectRtcp(rtcp, out _).Should().Be(SrtpResult.AuthenticationFailed);
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Wrong_keys_fail_authentication(SrtpProfile profile)
    {
        using var a = new SrtpSession(TestPackets.Keys(profile, 1));
        using var stranger = new SrtpSession(TestPackets.Mirror(TestPackets.Keys(profile, 2)));
        var wire = Protect(a, TestPackets.Rtp(1, 1, "secret"u8));
        stranger.UnprotectRtp(wire, out _).Should().Be(SrtpResult.AuthenticationFailed);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Short_malformed_and_oversized_input_is_reported_not_thrown(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            b.UnprotectRtp(new byte[5], out _).Should().Be(SrtpResult.TooShort);
            b.UnprotectRtp(new byte[30], out _).Should().Be(SrtpResult.Malformed, "version 0");
            b.UnprotectRtcp(new byte[10], out _).Should().Be(SrtpResult.TooShort);

            // CSRC count claims 15 entries in a short packet; extension length past the end.
            var csrc = new byte[40];
            csrc[0] = 0x8F;
            b.UnprotectRtp(csrc, out _).Should().Be(SrtpResult.Malformed);
            var ext = TestPackets.Rtp(1, 1, new byte[30]);
            ext[0] |= 0x10;
            BinaryPrimitives.WriteUInt16BigEndian(ext.AsSpan(14), 0xFFFF);
            b.UnprotectRtp(ext, out _).Should().Be(SrtpResult.Malformed);

            var plain = TestPackets.Rtp(1, 1, "abc"u8);
            a.ProtectRtp(plain.ToArray(), plain.Length, out _).Should().Be(SrtpResult.BufferTooSmall);
            a.ProtectRtp(new byte[8], 8, out _).Should().Be(SrtpResult.TooShort);
            var huge = new byte[SrtpSession.MaxPacketLength + 100];
            a.ProtectRtp(huge, SrtpSession.MaxPacketLength + 1, out _).Should().Be(SrtpResult.TooLarge);

            var rnd = new Random(5);
            for (var i = 0; i < 2000; i++)
            {
                var junk = new byte[rnd.Next(0, 200)];
                rnd.NextBytes(junk);
                b.UnprotectRtp(junk.ToArray(), out _).Should().NotBe(SrtpResult.Ok);
                b.UnprotectRtcp(junk, out _).Should().NotBe(SrtpResult.Ok);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void SRTCP_sets_the_E_flag_counts_the_index_and_rejects_replays(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            var tagLength = profile == SrtpProfile.AeadAes128Gcm ? 16 : 10;
            for (uint i = 0; i < 3; i++)
            {
                var wire = ProtectRtcp(a, TestPackets.ReceiverReport(42, i));
                var esrtcp = profile == SrtpProfile.AeadAes128Gcm
                    ? BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(wire.Length - 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(wire.Length - tagLength - 4));
                esrtcp.Should().Be(0x80000000u | i);
                var copy = wire.ToArray();
                b.UnprotectRtcp(wire, out _).Should().Be(SrtpResult.Ok);
                b.UnprotectRtcp(copy, out _).Should().Be(SrtpResult.Replayed);
            }

            a.Outbound.SetNextRtcpIndex(42, 0x80000000);
            var buffer = new byte[64];
            TestPackets.ReceiverReport(42, 0).CopyTo(buffer, 0);
            a.ProtectRtcp(buffer, 32, out _).Should().Be(SrtpResult.IndexExhausted);
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Stream_limit_is_enforced(SrtpProfile profile)
    {
        var (a, b) = Pair(profile, maxStreams: 2);
        using (a)
        using (b)
        {
            var w1 = Protect(a, TestPackets.Rtp(1, 1, [1]));
            var w2 = Protect(a, TestPackets.Rtp(1, 2, [1]));
            var buffer = new byte[64];
            TestPackets.Rtp(1, 3, [1]).CopyTo(buffer, 0);
            a.ProtectRtp(buffer, 13, out _).Should().Be(SrtpResult.TooManyStreams);

            b.UnprotectRtp(w1, out _).Should().Be(SrtpResult.Ok);
            b.UnprotectRtp(w2, out _).Should().Be(SrtpResult.Ok);
            using var other = new SrtpSession(TestPackets.Keys(profile));
            var w3 = Protect(other, TestPackets.Rtp(1, 3, [1]));
            b.UnprotectRtp(w3, out _).Should().Be(SrtpResult.TooManyStreams);
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Steady_state_protect_and_unprotect_do_not_allocate(SrtpProfile profile)
    {
        var (a, b) = Pair(profile);
        using (a)
        using (b)
        {
            var plain = TestPackets.Rtp(0, 5, new byte[1000]);
            var rr = TestPackets.ReceiverReport(5, 0);
            var buffer = new byte[1100];
            var rtcpBuffer = new byte[100];

            void Once(ushort seq)
            {
                plain.CopyTo(buffer, 0);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), seq);
                a.ProtectRtp(buffer, plain.Length, out var n);
                b.UnprotectRtp(buffer.AsSpan(0, n), out _);
                rr.CopyTo(rtcpBuffer, 0);
                a.ProtectRtcp(rtcpBuffer, rr.Length, out n);
                b.UnprotectRtcp(rtcpBuffer.AsSpan(0, n), out _);
            }

            for (ushort s = 0; s < 50; s++)
            {
                Once(s); // warm-up: streams, scratch buffers, JIT
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (ushort s = 50; s < 1050; s++)
            {
                Once(s);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        }
    }
}
