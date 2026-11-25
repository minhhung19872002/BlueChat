using BlueChat.Media.Dtls;

namespace BlueChat.Media.Dtls.Tests;

public class DtlsSessionTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public void Loaded_OpenSSL_is_version_3()
    {
        (DtlsContext.OpenSslVersion >> 28).Should().Be(3);
    }

    [Fact]
    public void Two_sessions_complete_the_handshake_and_derive_mirrored_SRTP_keys()
    {
        using var serverCtx = new DtlsContext(DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30)));
        using var clientCtx = new DtlsContext(DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30)));
        using var server = new DtlsSession(serverCtx, DtlsRole.Server, clientCtx.Certificate.Fingerprint);
        using var client = new DtlsSession(clientCtx, DtlsRole.Client, serverCtx.Certificate.Fingerprint);

        Pump(client, server);

        server.State.Should().Be(DtlsState.Connected, server.FailureReason);
        client.State.Should().Be(DtlsState.Connected, client.FailureReason);
        server.ProtocolVersion.Should().Be("DTLSv1.2");
        server.Keys!.Profile.Should().Be(SrtpProfile.AeadAes128Gcm);
        server.Keys.LocalMasterKey.Should().Equal(client.Keys!.RemoteMasterKey);
        server.Keys.LocalMasterSalt.Should().Equal(client.Keys.RemoteMasterSalt);
        server.Keys.RemoteMasterKey.Should().Equal(client.Keys.LocalMasterKey);
        server.Keys.LocalMasterKey.Should().HaveCount(16);
        server.Keys.LocalMasterSalt.Should().HaveCount(12);
        server.Keys.LocalMasterKey.Should().NotEqual(server.Keys.RemoteMasterKey);
    }

    [Fact]
    public void Wrong_SDP_fingerprint_fails_and_hands_out_no_keys()
    {
        using var serverCtx = new DtlsContext(DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30)));
        using var clientCtx = new DtlsContext(DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30)));
        using var other = new DtlsContext(DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30)));
        using var server = new DtlsSession(serverCtx, DtlsRole.Server, other.Certificate.Fingerprint);
        using var client = new DtlsSession(clientCtx, DtlsRole.Client, serverCtx.Certificate.Fingerprint);

        Pump(client, server);

        server.State.Should().Be(DtlsState.Failed);
        server.FailureReason.Should().Be("fingerprint_mismatch");
        server.Keys.Should().BeNull();
    }

    [Fact]
    public void Garbage_datagram_fails_the_session_loudly()
    {
        using var ctx = new DtlsContext(DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30)));
        using var server = new DtlsSession(ctx, DtlsRole.Server, ctx.Certificate.Fingerprint);
        server.Start();
        // Record header claiming a handshake record with a length past the end, then junk.
        server.Receive([22, 0xFE, 0xFD, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0x00, 1, 2, 3, 4, 5]);
        server.Receive(Enumerable.Repeat((byte)0x16, 64).ToArray());
        server.State.Should().NotBe(DtlsState.Connected);
        server.Keys.Should().BeNull();
    }

    [Fact]
    public void Records_are_packed_into_datagrams_without_splitting_a_record()
    {
        static byte[] Record(int bodyLength)
        {
            var r = new byte[13 + bodyLength];
            r[0] = 22;
            r[11] = (byte)(bodyLength >> 8);
            r[12] = (byte)bodyLength;
            return r;
        }

        var data = Record(500).Concat(Record(500)).Concat(Record(100)).ToArray();
        var packed = DtlsSession.PackRecords(data, 1000);
        packed.Select(p => p.Length).Should().Equal(513, 513 + 113);
        packed.SelectMany(p => p).Should().Equal(data);
    }

    [Fact]
    public void Certificate_fingerprint_is_uppercase_sha256_hex_pairs()
    {
        var cert = DtlsCertificate.Generate(Clock, TimeSpan.FromDays(30));
        cert.Fingerprint.Should().MatchRegex("^([0-9A-F]{2}:){31}[0-9A-F]{2}$");
    }

    private static void Pump(DtlsSession client, DtlsSession server)
    {
        server.Start();
        client.Start();
        for (var round = 0; round < 20; round++)
        {
            var moved = false;
            foreach (var d in client.TakeOutgoing())
            {
                server.Receive(d);
                moved = true;
            }

            foreach (var d in server.TakeOutgoing())
            {
                client.Receive(d);
                moved = true;
            }

            if (!moved)
            {
                break;
            }
        }
    }
}
