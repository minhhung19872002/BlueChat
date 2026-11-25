using System.Buffers.Binary;
using System.Text.Json;
using BlueChat.Media.Dtls;

namespace BlueChat.Media.Srtp.Tests;

/// <summary>
/// Independent reference: libsrtp (pylibsrtp 1.1.0) protected the packets in Interop/libsrtp-vectors.json
/// (Interop/libsrtp_check.py generate). SRTP is deterministic, so our protect must produce the same bytes and our
/// unprotect must recover the plaintext. The reverse leg (libsrtp unprotecting our output) is
/// <see cref="OurProtect_dump_for_libsrtp_verify"/> + libsrtp_check.py verify.
/// </summary>
public class LibsrtpInteropTests
{
    public static TheoryData<string> Profiles => new() { "AeadAes128Gcm", "Aes128CmHmacSha1x80" };

    private static JsonElement Profile(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Interop", "libsrtp-vectors.json")));
        return doc.RootElement.GetProperty("profiles").EnumerateArray()
            .First(p => p.GetProperty("profile").GetString() == name).Clone();
    }

    private static SrtpSession Session(JsonElement p)
    {
        var profile = Enum.Parse<SrtpProfile>(p.GetProperty("profile").GetString()!);
        var key = Convert.FromHexString(p.GetProperty("masterKey").GetString()!);
        var salt = Convert.FromHexString(p.GetProperty("masterSalt").GetString()!);
        return new SrtpSession(new SrtpKeyMaterial(profile, key, salt, key, salt));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Our_RTP_protect_matches_libsrtp_byte_for_byte_across_the_sequence_wrap(string name)
    {
        var p = Profile(name);
        using var session = Session(p);
        var count = 0;
        foreach (var v in p.GetProperty("rtp").EnumerateArray())
        {
            var plain = Convert.FromHexString(v.GetProperty("plain").GetString()!);
            var expected = Convert.FromHexString(v.GetProperty("protected").GetString()!);
            var buffer = new byte[plain.Length + session.RtpOverhead];
            plain.CopyTo(buffer, 0);
            session.ProtectRtp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);
            buffer[..n].Should().Equal(expected, $"RTP vector {count}");
            count++;
        }

        count.Should().BeGreaterThan(20);
        session.Outbound.RtpState(0x11223344)!.Value.Roc.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Our_RTP_unprotect_opens_libsrtp_packets(string name)
    {
        var p = Profile(name);
        using var session = Session(p);
        foreach (var v in p.GetProperty("rtp").EnumerateArray())
        {
            var packet = Convert.FromHexString(v.GetProperty("protected").GetString()!);
            session.UnprotectRtp(packet, out var n).Should().Be(SrtpResult.Ok);
            packet[..n].Should().Equal(Convert.FromHexString(v.GetProperty("plain").GetString()!));
        }

        session.Inbound.RtpState(0x11223344)!.Value.Roc.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Our_RTCP_protect_matches_libsrtp_and_unprotect_opens_it(string name)
    {
        var p = Profile(name);
        using var tx = Session(p);
        using var rx = Session(p);
        uint? previousIndex = null;
        foreach (var v in p.GetProperty("rtcp").EnumerateArray())
        {
            var plain = Convert.FromHexString(v.GetProperty("plain").GetString()!);
            var expected = Convert.FromHexString(v.GetProperty("protected").GetString()!);
            var esrtcp = name == "AeadAes128Gcm"
                ? BinaryPrimitives.ReadUInt32BigEndian(expected.AsSpan(expected.Length - 4))
                : BinaryPrimitives.ReadUInt32BigEndian(expected.AsSpan(expected.Length - 14));
            (esrtcp & 0x80000000u).Should().NotBe(0u, "libsrtp encrypts SRTCP (E = 1)");
            var index = esrtcp & 0x7FFFFFFF;
            if (previousIndex is { } prev)
            {
                index.Should().Be(prev + 1, "SRTCP index counts per packet");
            }
            else
            {
                tx.Outbound.SetNextRtcpIndex(BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(4)), index);
            }

            previousIndex = index;
            var buffer = new byte[plain.Length + tx.RtcpOverhead];
            plain.CopyTo(buffer, 0);
            tx.ProtectRtcp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);
            buffer[..n].Should().Equal(expected);

            rx.UnprotectRtcp(expected, out var m).Should().Be(SrtpResult.Ok);
            expected[..m].Should().Equal(plain);
        }
    }

    /// <summary>
    /// Reverse leg. With BC_SRTP_DUMP=&lt;file&gt; this writes packets protected by our code (new keys, new packets) for
    /// <c>libsrtp_check.py verify &lt;file&gt;</c>; without it, it only checks our own round trip.
    /// </summary>
    [Fact]
    public void OurProtect_dump_for_libsrtp_verify()
    {
        var profiles = new List<object>();
        foreach (var (profile, saltLength) in new[] { (SrtpProfile.AeadAes128Gcm, 12), (SrtpProfile.Aes128CmHmacSha1x80, 14) })
        {
            var rng = new Random(20261125 + saltLength);
            var key = new byte[16];
            var salt = new byte[saltLength];
            rng.NextBytes(key);
            rng.NextBytes(salt);
            using var session = new SrtpSession(new SrtpKeyMaterial(profile, key, salt, key, salt));
            var rtp = new List<object>();
            ushort seq = 65500;
            for (var i = 0; i < 80; i++, seq++) // crosses 65535 → 0
            {
                var payload = new byte[rng.Next(1, 1100)];
                rng.NextBytes(payload);
                var plain = TestPackets.Rtp(seq, 0xCAFEBABE, payload);
                var buffer = new byte[plain.Length + session.RtpOverhead];
                plain.CopyTo(buffer, 0);
                session.ProtectRtp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);
                rtp.Add(new { plain = Convert.ToHexStringLower(plain), @protected = Convert.ToHexStringLower(buffer[..n]) });
            }

            var rtcp = new List<object>();
            for (var i = 0; i < 10; i++)
            {
                var plain = TestPackets.ReceiverReport(0xCAFEBABE, (uint)i);
                var buffer = new byte[plain.Length + session.RtcpOverhead];
                plain.CopyTo(buffer, 0);
                session.ProtectRtcp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);
                rtcp.Add(new { plain = Convert.ToHexStringLower(plain), @protected = Convert.ToHexStringLower(buffer[..n]) });
            }

            profiles.Add(new { profile = profile.ToString(), masterKey = Convert.ToHexStringLower(key), masterSalt = Convert.ToHexStringLower(salt), rtp, rtcp });
        }

        var path = Environment.GetEnvironmentVariable("BC_SRTP_DUMP");
        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { profiles }));
        }
    }
}
