using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BlueChat.Turn;

/// <summary>
/// Time-limited TURN credentials (draft-uberti-behave-turn-rest-00 §2.2, as used by WebRTC deployments and coturn's
/// "use-auth-secret"): username = "&lt;expiry unix seconds&gt;:&lt;user id&gt;", password = base64(HMAC-SHA1(secret, username)).
/// </summary>
public static class TurnRestCredentials
{
    public static string PasswordFor(ReadOnlySpan<byte> secret, string username) =>
        Convert.ToBase64String(HMACSHA1.HashData(secret, Encoding.UTF8.GetBytes(username)));

    /// <summary>Credentials the Api hands out with a meeting ticket.</summary>
    public static (string Username, string Password) Issue(ReadOnlySpan<byte> secret, string userId, DateTimeOffset expires)
    {
        var username = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + ":" + userId;
        return (username, PasswordFor(secret, username));
    }

    /// <summary>The expiry encoded before the first ':' of a time-limited username.</summary>
    public static bool TryGetExpiry(string username, out DateTimeOffset expires)
    {
        expires = default;
        var colon = username.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon > 12
            || !long.TryParse(username.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > 253402300799)
        {
            return false;
        }

        expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}

/// <summary>
/// Stateless nonces (RFC 8489 §9.2: the server chooses the nonce and its validity). A nonce is
/// base64url(expiry ‖ HMAC-SHA256(key, expiry ‖ client IP)[..12]): no table to fill, bound to the client address,
/// expiry checked on use (→ 438 Stale Nonce).
/// </summary>
internal sealed class NonceService(byte[] key, TimeProvider time, TimeSpan lifetime)
{
    private const int MacLength = 12;

    public string Create(IPAddress client)
    {
        Span<byte> raw = stackalloc byte[8 + MacLength];
        BinaryPrimitives.WriteInt64BigEndian(raw, (time.GetUtcNow() + lifetime).ToUnixTimeSeconds());
        Mac(raw[..8], client, raw[8..]);
        return Base64UrlEncode(raw);
    }

    public bool IsValid(string nonce, IPAddress client)
    {
        Span<byte> raw = stackalloc byte[8 + MacLength];
        if (nonce.Length != 27 || !TryBase64UrlDecode(nonce, raw))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[MacLength];
        Mac(raw[..8], client, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, raw[8..]))
        {
            return false;
        }

        return BinaryPrimitives.ReadInt64BigEndian(raw) > time.GetUtcNow().ToUnixTimeSeconds();
    }

    private void Mac(ReadOnlySpan<byte> expiry, IPAddress client, Span<byte> destination)
    {
        Span<byte> input = stackalloc byte[8 + 16];
        expiry.CopyTo(input);
        var a = client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client;
        a.TryWriteBytes(input[8..], out var n);
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(key, input[..(8 + n)], full);
        full[..MacLength].CopyTo(destination);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string s, Span<byte> destination)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/') + "=";
        return Convert.TryFromBase64String(b64, destination, out var written) && written == destination.Length;
    }

    public static bool IsIPv6(IPAddress a) => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv4MappedToIPv6;
}

/// <summary>Token bucket for relayed bytes of one allocation.</summary>
internal sealed class TokenBucket(long ratePerSecond, long burst, DateTimeOffset now)
{
    private double _tokens = burst;
    private DateTimeOffset _last = now;

    public bool TryConsume(int bytes, DateTimeOffset now)
    {
        var elapsed = (now - _last).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(burst, _tokens + (elapsed * ratePerSecond));
            _last = now;
        }

        if (_tokens < bytes)
        {
            return false;
        }

        _tokens -= bytes;
        return true;
    }
}
