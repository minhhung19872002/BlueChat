using System.Security.Cryptography;

namespace BlueChat.Media.Srtp;

/// <summary>
/// AES in Segmented Integer Counter Mode (RFC 3711 §4.1.1): keystream block j = AES(k_e, IV + j), the counter in the
/// low 16 bits. Built from an AES-ECB transform reused for the lifetime of the context; scratch buffers grow once and
/// are then reused, so steady-state calls do not allocate. Not thread-safe.
/// </summary>
internal sealed class AesCounterMode : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private byte[] _counters = new byte[2048];
    private byte[] _keystream = new byte[2048];

    public AesCounterMode(byte[] key)
    {
        _aes = Aes.Create();
        _aes.Key = key;
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _ecb = _aes.CreateEncryptor();
    }

    /// <summary>XORs <paramref name="data"/> in place with the keystream that starts at <paramref name="iv"/> (16 octets, low 16 bits zero).</summary>
    public void Apply(ReadOnlySpan<byte> iv, Span<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var blocks = (data.Length + 15) / 16;
        var bytes = blocks * 16;
        if (_counters.Length < bytes)
        {
            _counters = new byte[bytes];
            _keystream = new byte[bytes];
        }

        for (var j = 0; j < blocks; j++)
        {
            var block = _counters.AsSpan(j * 16, 16);
            iv.CopyTo(block);
            // RFC 3711 §4.1.1: the counter is added in the low 16 bits; the IV's low 16 bits are zero, so add = write.
            block[14] = (byte)(j >> 8);
            block[15] = (byte)j;
        }

        _ecb.TransformBlock(_counters, 0, bytes, _keystream, 0);
        var ks = _keystream.AsSpan(0, data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= ks[i];
        }
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }
}
