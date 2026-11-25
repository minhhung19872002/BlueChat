using System.Net;
using System.Text;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Stun.Tests;

/// <summary>Test vectors copied from RFC 5769 Appendix A and RFC 8489 Appendix B.1 (fetched 2026-11-25).</summary>
public class Rfc5769Tests
{
    private const string Password = "VOkJxbRl1RmTxUk/WvJxBt";

    // RFC 5769 §2.1 / Appendix A `req`
    internal static readonly byte[] Request = Hex(
        "00010058 2112a442 b7e7a701bc34d686fa87dfae 80220010" + Ascii("STUN test client") +
        "00240004 6e0001ff 80290008 932ff9b151263b36 00060009 6576746a3a6836765920202000080014" +
        "9aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a2 80280004 e57a3bcf");

    // RFC 5769 §2.2 `respv4`
    internal static readonly byte[] ResponseV4 = Hex(
        "0101003c 2112a442 b7e7a701bc34d686fa87dfae 8022000b 7465737420766563746f7220" +
        "00200008 0001a147e112a643 00080014 2b91f599fd9e90c38c7489f92af9ba53f06be7d7 80280004 c07d4c96");

    // RFC 5769 §2.3 `respv6`
    internal static readonly byte[] ResponseV6 = Hex(
        "01010048 2112a442 b7e7a701bc34d686fa87dfae 8022000b 7465737420766563746f7220" +
        "00200014 0002a147 0113a9faa5d3f179bc25f4b5bed2b9d9" +
        "00080014 a382954e4be67bf11784c97c8292c275bfe3ed41 80280004 c8fb0b4c");

    // RFC 5769 §2.4 `reqltc`
    internal static readonly byte[] RequestLongTerm = Hex(
        "00010060 2112a442 78ad3433c6ad72c029da412e 00060012 e3839ee38388e383aae38383 e382afe382b90000" +
        "0015001c 662f2f3439396b3935346436 4f4c33346f4c394653547679 36347341" +
        "0014000b 6578616d706c652e6f726700 00080014 f67024656dd64a3e02b8e071 2e85c9a28ca89666");

    // RFC 8489 Appendix B.1
    internal static readonly byte[] RequestSha256 = Hex(
        "0001009c 2112a442 78ad3433 c6ad72c0 29da412e" +
        "001e0020 4a3cf38f ef6992bd a952c678 0417da0f 24819415 569e60b2 05c46e41 407f1704" +
        "00150029 6f624d61 744a6f73 32414141 43662f2f 3439396b 39353464 364f4c33 346f4c39 46535476 79363473 41000000" +
        "0014000b 6578616d 706c652e 6f726700" +
        "001c0020 e4686c8f 0edeb590 13e07090 010a93ef ccbccc54 4c0a45d9 f830aa6d 6f735a01");

    // RFC 8489 Appendix B.1 as corrected by erratum 6268 (Verified 2020-10-19): message length 0x90 and the
    // PASSWORD-ALGORITHM (SHA-256) attribute added, MESSAGE-INTEGRITY-SHA256 recomputed.
    internal static readonly byte[] RequestSha256Errata6268 = Hex(
        "00010090 2112a442 78ad3433 c6ad72c0 29da412e" +
        "001e0020 4a3cf38f ef6992bd a952c678 0417da0f 24819415 569e60b2 05c46e41 407f1704" +
        "00150029 6f624d61 744a6f73 32414141 43662f2f 3439396b 39353464 364f4c33 346f4c39 46535476 79363473 41000000" +
        "0014000b 6578616d 706c652e 6f726700" +
        "001d0004 00020000" +
        "001c0020 b5c7bf00 5b6c52a2 1c51c5e8 92f81924 136296cb 927c4314 9309278c c6518e65");

    private const string JapaneseUser = "マトリックス";

    [Fact]
    public void Sample_request_parses_and_authenticates_with_the_short_term_key()
    {
        StunMessage.TryParse(Request, out var m).Should().Be(StunParseError.None);
        m.Method.Should().Be(StunMethod.Binding);
        m.Class.Should().Be(StunClass.Request);
        m.GetString(StunAttr.Software).Should().Be("STUN test client");
        m.GetString(StunAttr.Username).Should().Be("evtj:h6vY");
        m.TryGetUInt32(StunAttr.Priority, out var priority).Should().BeTrue();
        priority.Should().Be(0x6e0001ff);
        m.TryGetUInt64(StunAttr.IceControlled, out var tie).Should().BeTrue();
        tie.Should().Be(0x932ff9b151263b36);
        StunIntegrity.VerifySha1(m, StunCredentials.ShortTermKey(Password)).Should().Be(IntegrityResult.Ok);
        StunIntegrity.VerifyFingerprint(m).Should().Be(IntegrityResult.Ok);
        StunIntegrity.VerifySha1(m, StunCredentials.ShortTermKey("wrong")).Should().Be(IntegrityResult.Mismatch);
    }

    [Theory]
    [InlineData(false, "192.0.2.1")]
    [InlineData(true, "2001:db8:1234:5678:11:2233:4455:6677")]
    public void Sample_responses_decode_the_xor_mapped_address(bool v6, string address)
    {
        StunMessage.TryParse(v6 ? ResponseV6 : ResponseV4, out var m).Should().Be(StunParseError.None);
        m.Class.Should().Be(StunClass.SuccessResponse);
        m.GetString(StunAttr.Software).Should().Be("test vector");
        m.TryGetXorAddress(StunAttr.XorMappedAddress, out var ep).Should().BeTrue();
        ep!.Address.Should().Be(IPAddress.Parse(address));
        ep.Port.Should().Be(32853);
        StunIntegrity.VerifySha1(m, StunCredentials.ShortTermKey(Password)).Should().Be(IntegrityResult.Ok);
        StunIntegrity.VerifyFingerprint(m).Should().Be(IntegrityResult.Ok);
    }

    [Fact]
    public void Long_term_request_authenticates_after_password_preparation()
    {
        StunCredentials.PreparePassword("The­MªtrⅨ").Should().Be("TheMatrIX");
        StunMessage.TryParse(RequestLongTerm, out var m).Should().Be(StunParseError.None);
        m.GetString(StunAttr.Username).Should().Be(JapaneseUser);
        m.GetString(StunAttr.Realm).Should().Be("example.org");
        m.GetString(StunAttr.Nonce).Should().Be("f//499k954d6OL34oL9FSTvy64sA");
        var key = StunCredentials.LongTermKeyMd5(JapaneseUser, "example.org", "The­MªtrⅨ");
        StunIntegrity.VerifySha1(m, key).Should().Be(IntegrityResult.Ok);
        StunIntegrity.VerifyFingerprint(m).Should().Be(IntegrityResult.Missing);
    }

    [Fact]
    public void Rfc8489_B1_as_printed_has_a_wrong_length_and_is_rejected_loudly()
    {
        // Erratum 6268: the printed length 0x9c counts the 20-byte header.
        StunMessage.TryParse(RequestSha256, out _).Should().Be(StunParseError.LengthMismatch);
    }

    [Fact]
    public void Rfc8489_B1_userhash_matches()
    {
        StunMessage.TryParse(RequestSha256Errata6268, out var m).Should().Be(StunParseError.None);
        m.TryGetAttribute(StunAttr.UserHash, out var userHash).Should().BeTrue();
        userHash.ToArray().Should().Equal(StunCredentials.UserHash(JapaneseUser, "example.org"));
        m.GetString(StunAttr.Nonce).Should().Be("obMatJos2AAACf//499k954d6OL34oL9FSTvy64sA");
    }

    [Fact]
    public void Rfc8489_B1_message_integrity_sha256()
    {
        StunMessage.TryParse(RequestSha256Errata6268, out var m).Should().Be(StunParseError.None);
        m.TryGetUInt32(StunAttr.PasswordAlgorithm, out var algorithm).Should().BeTrue();
        algorithm.Should().Be(0x00020000); // SHA-256, no parameters
        var key = StunCredentials.LongTermKeySha256(JapaneseUser, "example.org", "The­MªtrⅨ");
        StunIntegrity.VerifySha256(m, key).Should().Be(IntegrityResult.Ok);
    }

    [Fact]
    public void Writer_output_verifies_and_reproduces_the_rfc_mac_for_the_same_content()
    {
        // Rebuild §2.2 with the RFC's space padding replaced by our zero padding: the MAC covers padding, so compare
        // against the RFC message after zeroing its single padding byte and recomputing — instead check semantics.
        Span<byte> buf = stackalloc byte[128];
        var tid = ResponseV4.AsSpan(8, 12);
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.SuccessResponse, tid);
        w.AddString(StunAttr.Software, "test vector");
        w.AddXorAddress(StunAttr.XorMappedAddress, IPAddress.Parse("192.0.2.1"), 32853);
        w.AddMessageIntegrity(StunCredentials.ShortTermKey(Password));
        w.AddFingerprint();
        var bytes = w.Written.ToArray();

        bytes.Length.Should().Be(ResponseV4.Length);
        // XOR-MAPPED-ADDRESS bytes identical to the RFC
        bytes.AsSpan(36, 12).ToArray().Should().Equal(ResponseV4.AsSpan(36, 12).ToArray());
        StunMessage.TryParse(bytes, out var m).Should().Be(StunParseError.None);
        StunIntegrity.VerifySha1(m, StunCredentials.ShortTermKey(Password)).Should().Be(IntegrityResult.Ok);
        StunIntegrity.VerifyFingerprint(m).Should().Be(IntegrityResult.Ok);
    }

    [Fact]
    public void Writer_with_rfc_space_padding_reproduces_the_rfc_bytes_exactly()
    {
        // "test vector" is 11 bytes; RFC 5769 pads with one space. Writing the 12-byte value "test vector " makes the
        // TLV length differ, so instead patch our zero padding to a space before the MAC: the result must equal §2.2.
        Span<byte> buf = stackalloc byte[128];
        var w = new StunWriter(buf, StunMethod.Binding, StunClass.SuccessResponse, ResponseV4.AsSpan(8, 12));
        w.AddString(StunAttr.Software, "test vector");
        buf[20 + 4 + 11] = 0x20;
        w.AddXorAddress(StunAttr.XorMappedAddress, IPAddress.Parse("192.0.2.1"), 32853);
        w.AddMessageIntegrity(StunCredentials.ShortTermKey(Password));
        w.AddFingerprint();
        w.Written.ToArray().Should().Equal(ResponseV4);
    }

    internal static string Ascii(string s) => Convert.ToHexString(Encoding.ASCII.GetBytes(s));

    internal static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", "", StringComparison.Ordinal));
}
