using BlueChat.Media.Dtls;

namespace BlueChat.Media.Srtp.Tests;

/// <summary>Published test vectors: RFC 3711 Appendix B.2/B.3, RFC 7714 §16.1 and §17.</summary>
public class RfcVectorTests
{
    internal static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", "", StringComparison.Ordinal));

    [Fact]
    public void Rfc3711_B2_AES_CM_keystream()
    {
        using var ctr = new AesCounterMode(Hex("2B7E151628AED2A6ABF7158809CF4F3C"));
        var data = new byte[65282 * 16]; // zeros: the output is the keystream itself
        ctr.Apply(Hex("F0F1F2F3F4F5F6F7F8F9FAFBFCFD0000"), data);

        Block(data, 0x0000).Should().Equal(Hex("E03EAD0935C95E80E166B16DD92B4EB4"));
        Block(data, 0x0001).Should().Equal(Hex("D23513162B02D0F72A43A2FE4A5F97AB"));
        Block(data, 0x0002).Should().Equal(Hex("41E95B3BB0A2E8DD477901E4FCA894C0"));
        Block(data, 0xFEFF).Should().Equal(Hex("EC8CDF7398607CB0F2D21675EA9EA1E4"));
        Block(data, 0xFF00).Should().Equal(Hex("362B7C3C6773516318A077D7FC5073AE"));
        Block(data, 0xFF01).Should().Equal(Hex("6A2CC3787889374FBEB4C81B17BA6C44"));

        static byte[] Block(byte[] d, int j) => d.AsSpan(j * 16, 16).ToArray();
    }

    [Fact]
    public void Rfc3711_B3_key_derivation()
    {
        var key = Hex("E1F97A0D3E018BE0D64FA32C06DE4139");
        var salt = Hex("0EC675AD498AFEEBB6960B3AABE6");

        SrtpKdf.Derive(key, salt, SrtpKdf.RtpEncryption, 16).Should().Equal(Hex("C61E7A93744F39EE10734AFE3FF7A087"));
        SrtpKdf.Derive(key, salt, SrtpKdf.RtpSalt, 14).Should().Equal(Hex("30CBBC08863D8C85D49DB34A9AE1"));
        SrtpKdf.Derive(key, salt, SrtpKdf.RtpAuthentication, 94).Should().Equal(Hex(
            "CEBE321F6FF7716B6FD4AB49AF256A15" + "6D38BAA48F0A0ACF3C34E2359E6CDBCE" + "E049646C43D9327AD175578EF7227098" +
            "6371C10C9A369AC2F94A8C5FBCDDDC25" + "6D6E919A48B610EF17C2041E47403576" + "6B68642C59BBFC2F34DB60DBDFB2"));
    }

    // RFC 7714 §16–17: 128-bit key 00..0f, salt "Quid pro quo", stated as session keys.
    private static SrtpContext Rfc7714Context() => SrtpContext.FromSessionKeys(SrtpProfile.AeadAes128Gcm,
        Hex("000102030405060708090a0b0c0d0e0f"), Hex("517569642070726f2071756f"), null,
        Hex("000102030405060708090a0b0c0d0e0f"), Hex("517569642070726f2071756f"), null);

    private const string RtpPlain =
        "8040f17b 8041f8d3 5501a0b2 47616c6c 69612065 7374206f 6d6e6973 20646976 69736120 696e2070 61727465 73207472 6573";

    private const string RtpProtected =
        "8040f17b 8041f8d3 5501a0b2 f24de3a3 fb34de6c acba861c 9d7e4bca be633bd5 0d294e6f 42a5f47a 51c7d19b 36de3adf " +
        "8833899d 7f27beb1 6a9152cf 765ee439 0cce";

    private const string RtcpPlain =
        "81c8000d 4d617273 4e545031 4e545032 52545020 0000042a 0000e930 4c756e61 deadbeef deadbeef deadbeef deadbeef deadbeef";

    private const string RtcpProtected =
        "81c8000d 4d617273 63e94885 dcdab67c a727d766 2f6b7e99 7ff5c0f7 6c06f32d c676a5f1 730d6fda 4ce09b46 86303ded " +
        "0bb9275b c84aa458 96cf4d2f c5abf872 45d9eade 800005d4";

    private const string RtcpTagOnly =
        "81c8000d 4d617273 4e545031 4e545032 52545020 0000042a 0000e930 4c756e61 deadbeef deadbeef deadbeef deadbeef " +
        "deadbeef 841dd968 3dd78ec9 2ae58790 125f62b3 000005d4";

    [Fact]
    public void Rfc7714_16_1_1_SRTP_GCM_encryption()
    {
        using var ctx = Rfc7714Context();
        var plain = Hex(RtpPlain);
        var buffer = new byte[plain.Length + 16];
        plain.CopyTo(buffer, 0);

        ctx.ProtectRtp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);

        buffer[..n].Should().Equal(Hex(RtpProtected));
    }

    [Fact]
    public void Rfc7714_16_1_2_SRTP_GCM_decryption()
    {
        using var ctx = Rfc7714Context();
        var packet = Hex(RtpProtected);

        ctx.UnprotectRtp(packet, out var n).Should().Be(SrtpResult.Ok);

        packet[..n].Should().Equal(Hex(RtpPlain));
    }

    [Fact]
    public void Rfc7714_17_1_SRTCP_GCM_encryption_with_index_5d4()
    {
        using var ctx = Rfc7714Context();
        ctx.SetNextRtcpIndex(0x4d617273, 0x5d4);
        var plain = Hex(RtcpPlain);
        var buffer = new byte[plain.Length + 20];
        plain.CopyTo(buffer, 0);

        ctx.ProtectRtcp(buffer, plain.Length, out var n).Should().Be(SrtpResult.Ok);

        buffer[..n].Should().Equal(Hex(RtcpProtected));
    }

    [Fact]
    public void Rfc7714_17_1_SRTCP_GCM_decryption()
    {
        using var ctx = Rfc7714Context();
        var packet = Hex(RtcpProtected);

        ctx.UnprotectRtcp(packet, out var n).Should().Be(SrtpResult.Ok);

        packet[..n].Should().Equal(Hex(RtcpPlain));
    }

    [Fact]
    public void Rfc7714_17_3_SRTCP_GCM_tag_only_E0_verifies_without_decrypting()
    {
        using var ctx = Rfc7714Context();
        var packet = Hex(RtcpTagOnly);

        ctx.UnprotectRtcp(packet, out var n).Should().Be(SrtpResult.Ok);

        packet[..n].Should().Equal(Hex(RtcpPlain));
    }

    [Fact]
    public void Rfc7714_17_3_tampered_tag_only_packet_fails()
    {
        using var ctx = Rfc7714Context();
        var packet = Hex(RtcpTagOnly);
        packet[20] ^= 1; // a byte of the (unencrypted, authenticated) body

        ctx.UnprotectRtcp(packet, out _).Should().Be(SrtpResult.AuthenticationFailed);
    }
}
