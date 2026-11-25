using System.Buffers.Binary;
using BlueChat.Media.Dtls;

namespace BlueChat.Media.Srtp.Tests;

internal static class TestPackets
{
    public static byte[] Rtp(ushort seq, uint ssrc, ReadOnlySpan<byte> payload, byte payloadType = 111)
    {
        var p = new byte[12 + payload.Length];
        p[0] = 0x80;
        p[1] = payloadType;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), (uint)seq * 960);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), ssrc);
        payload.CopyTo(p.AsSpan(12));
        return p;
    }

    /// <summary>RTCP RR with one report block (RFC 3550 §6.4.2).</summary>
    public static byte[] ReceiverReport(uint ssrc, uint highestSeq)
    {
        var p = new byte[32];
        p[0] = 0x81;
        p[1] = 201;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), 0x01020304);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), highestSeq);
        return p;
    }

    public static SrtpKeyMaterial Keys(SrtpProfile profile, int seed = 1)
    {
        var rng = new Random(seed);
        byte[] Bytes(int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        var saltLength = profile == SrtpProfile.AeadAes128Gcm ? 12 : 14;
        var aKey = Bytes(16);
        var aSalt = Bytes(saltLength);
        var bKey = Bytes(16);
        var bSalt = Bytes(saltLength);
        return new SrtpKeyMaterial(profile, aKey, aSalt, bKey, bSalt);
    }

    /// <summary>The peer's view of the same keys (local and remote swapped), as the other DTLS endpoint exports them.</summary>
    public static SrtpKeyMaterial Mirror(SrtpKeyMaterial k) =>
        new(k.Profile, k.RemoteMasterKey, k.RemoteMasterSalt, k.LocalMasterKey, k.LocalMasterSalt);
}
