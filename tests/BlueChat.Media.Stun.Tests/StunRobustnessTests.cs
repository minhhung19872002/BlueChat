using System.Net;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Stun.Tests;

public class StunRobustnessTests
{
    [Theory]
    [InlineData(StunMethod.Binding, StunClass.Request, 0x0001)]
    [InlineData(StunMethod.Binding, StunClass.Indication, 0x0011)]
    [InlineData(StunMethod.Binding, StunClass.SuccessResponse, 0x0101)]
    [InlineData(StunMethod.Binding, StunClass.ErrorResponse, 0x0111)]
    [InlineData(StunMethod.Allocate, StunClass.ErrorResponse, 0x0113)]
    [InlineData(StunMethod.ChannelBind, StunClass.SuccessResponse, 0x0109)]
    [InlineData(StunMethod.Send, StunClass.Indication, 0x0016)]
    [InlineData(StunMethod.Data, StunClass.Indication, 0x0017)]
    public void Message_type_encoding_matches_rfc_8489_section_5(ushort method, StunClass cls, int type)
    {
        StunMessageType.Compose(method, cls).Should().Be((ushort)type);
        StunMessageType.MethodOf((ushort)type).Should().Be(method);
        StunMessageType.ClassOf((ushort)type).Should().Be(cls);
    }

    [Fact]
    public void Error_response_round_trips_code_reason_and_unknown_attributes()
    {
        Span<byte> buf = stackalloc byte[256];
        Span<byte> tid = stackalloc byte[12];
        StunWriter.NewTransactionId(tid);
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.ErrorResponse, tid);
        w.AddErrorCode(420, "Unknown Attribute");
        w.AddUnknownAttributes([0x0042, 0x0043]);
        w.AddFingerprint();
        StunMessage.TryParse(w.Written, out var m).Should().Be(StunParseError.None);
        m.TryGetErrorCode(out var code, out var reason).Should().BeTrue();
        code.Should().Be(420);
        reason.Should().Be("Unknown Attribute");
        StunIntegrity.VerifyFingerprint(m).Should().Be(IntegrityResult.Ok);
    }

    [Fact]
    public void Unknown_comprehension_required_attributes_are_reported()
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.Request, new byte[12]);
        w.AddString(StunAttr.Username, "a:b");
        w.AddUInt32(0x0042, 1); // unknown, comprehension-required
        w.AddUInt32(0x8042, 1); // unknown, optional
        StunMessage.TryParse(w.Written, out var m).Should().Be(StunParseError.None);
        Span<ushort> unknown = stackalloc ushort[8];
        var n = m.UnknownComprehensionRequired([StunAttr.Username], unknown);
        unknown[..n].ToArray().Should().Equal((ushort)0x0042);
    }

    [Fact]
    public void Ipv6_xor_address_round_trips()
    {
        Span<byte> buf = stackalloc byte[128];
        Span<byte> tid = stackalloc byte[12];
        StunWriter.NewTransactionId(tid);
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.SuccessResponse, tid);
        var address = IPAddress.Parse("2001:db8::abcd");
        w.AddXorAddress(StunAttr.XorMappedAddress, address, 50000);
        StunMessage.TryParse(w.Written, out var m).Should().Be(StunParseError.None);
        m.TryGetXorAddress(StunAttr.XorMappedAddress, out var ep).Should().BeTrue();
        ep.Should().Be(new IPEndPoint(address, 50000));
    }

    [Theory]
    [InlineData(19, StunParseError.TooShort)]
    [InlineData(5000, StunParseError.TooLong)]
    public void Size_limits(int size, StunParseError expected)
    {
        var data = new byte[size];
        StunMessage.TryParse(data, out _).Should().Be(expected);
    }

    [Fact]
    public void Framing_errors_are_named()
    {
        var ok = Rfc5769Tests.Request.ToArray();
        var badCookie = ok.ToArray();
        badCookie[4] ^= 1;
        StunMessage.TryParse(badCookie, out _).Should().Be(StunParseError.BadMagicCookie);

        var topBits = ok.ToArray();
        topBits[0] |= 0x80;
        StunMessage.TryParse(topBits, out _).Should().Be(StunParseError.NotStun);

        var unaligned = ok.ToArray();
        unaligned[3] = 0x57;
        StunMessage.TryParse(unaligned, out _).Should().Be(StunParseError.LengthNotAligned);

        StunMessage.TryParse(ok.AsSpan(0, ok.Length - 4), out _).Should().Be(StunParseError.LengthMismatch);

        var overflow = ok.ToArray();
        overflow[20 + 3] = 0xF0; // SOFTWARE length runs past the end
        StunMessage.TryParse(overflow, out _).Should().Be(StunParseError.AttributeOverflow);
    }

    [Fact]
    public void Attribute_after_fingerprint_is_rejected()
    {
        Span<byte> buf = stackalloc byte[128];
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.Request, new byte[12]);
        w.AddFingerprint();
        w.AddFlag(StunAttr.UseCandidate);
        StunMessage.TryParse(w.Written, out _).Should().Be(StunParseError.FingerprintNotLast);
    }

    [Fact]
    public void Too_many_attributes_are_rejected()
    {
        var buf = new byte[20 + (4 * 70)];
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.Request, new byte[12]);
        for (var i = 0; i < 70; i++)
        {
            w.AddFlag(StunAttr.UseCandidate);
        }

        StunMessage.TryParse(w.Written, out _).Should().Be(StunParseError.TooManyAttributes);
    }

    [Fact]
    public void Random_mutations_never_throw_and_never_authenticate()
    {
        var key = StunCredentials.ShortTermKey("VOkJxbRl1RmTxUk/WvJxBt");
        var rng = new Random(20261125);
        byte[][] seeds = [Rfc5769Tests.Request, Rfc5769Tests.ResponseV4, Rfc5769Tests.ResponseV6, Rfc5769Tests.RequestLongTerm];
        for (var i = 0; i < 20000; i++)
        {
            var seed = seeds[i % seeds.Length];
            var data = seed.ToArray();
            var flips = 1 + rng.Next(4);
            for (var f = 0; f < flips; f++)
            {
                data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            }

            if (rng.Next(4) == 0)
            {
                data = data.AsSpan(0, rng.Next(data.Length)).ToArray();
            }

            if (StunMessage.TryParse(data, out var m) != StunParseError.None)
            {
                continue;
            }

            foreach (var a in m.Attributes)
            {
                _ = a.Value.Length;
            }

            _ = m.TryGetXorAddress(StunAttr.XorMappedAddress, out _);
            _ = m.TryGetErrorCode(out _, out _);
            var integrity = StunIntegrity.VerifySha1(m, key);
            if (integrity == IntegrityResult.Ok)
            {
                // Only flips outside the integrity-covered prefix (in the MAC-irrelevant trailing FINGERPRINT) may pass.
                data.AsSpan(0, m.OffsetOf(StunAttr.MessageIntegrity)).SequenceEqual(seed.AsSpan(0, m.OffsetOf(StunAttr.MessageIntegrity)))
                    .Should().BeTrue();
            }
        }
    }
}
