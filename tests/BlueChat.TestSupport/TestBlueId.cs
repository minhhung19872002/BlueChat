using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace BlueChat.TestSupport;

/// <summary>
/// TEST ONLY. A minimal OIDC issuer that serves discovery and JWKS on a loopback port and mints RS256 access tokens
/// with the claims BlueChat expects from BlueID (docs/00 Z1, B1). Lets integration and E2E tests exercise the real
/// JwtBearer validation path (discovery, key fetch, signature, issuer, audience, lifetime) without BlueID.
/// </summary>
public sealed class TestBlueId : IAsyncDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly WebApplication _app;

    private TestBlueId(WebApplication app, string authority)
    {
        _app = app;
        Authority = authority;
        _key = new RsaSecurityKey(_rsa) { KeyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) };
    }

    /// <summary>Issuer URL without a trailing slash, e.g. http://127.0.0.1:51234.</summary>
    public string Authority { get; }

    public const string Audience = "bluechat-api";

    public static async Task<TestBlueId> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        TestBlueId? self = null;
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = self!.Authority + "/",
            ["jwks_uri"] = self.Authority + "/jwks",
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
        }));
        app.MapGet("/jwks", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(self!._rsa.ExportParameters(false)) { KeyId = self._key.KeyId });
            return Results.Json(new { keys = new[] { new { kty = jwk.Kty, use = "sig", alg = "RS256", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
        });
        await app.StartAsync();
        var address = app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal);
        self = new TestBlueId(app, address.TrimEnd('/'));
        return self;
    }

    /// <summary>Mints an access token for <paramref name="user"/>. Expired or foreign tokens can be made for negative tests.</summary>
    public string Token(TestUser user, TimeSpan? lifetime = null, string? audience = null, bool signWithForeignKey = false)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", user.Subject),
            new("tid", user.Tenant),
            new("sid", user.Session),
            new("name", user.Name),
        };
        if (user.Email is not null)
        {
            claims.Add(new Claim("email", user.Email));
        }

        if (user.TenantName is not null)
        {
            claims.Add(new Claim("tenant_name", user.TenantName));
        }

        if (user.Guest)
        {
            claims.Add(new Claim("guest", "true"));
        }

        var expires = now + (lifetime ?? TimeSpan.FromMinutes(15));
        var notBefore = expires.AddMinutes(-16) < now.AddMinutes(-1) ? expires.AddMinutes(-16) : now.AddMinutes(-1);
        var signing = signWithForeignKey ? new RsaSecurityKey(RSA.Create(2048)) { KeyId = _key.KeyId } : _key;
        var token = new JwtSecurityToken(
            issuer: Authority + "/",
            audience: audience ?? Audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }
}

public sealed record TestUser(string Subject, string Tenant, string Name, string? Email = null, string? TenantName = null,
    bool Guest = false)
{
    public string Session { get; init; } = Guid.NewGuid().ToString("N");

    public static TestUser New(string tenant, string name, string? email = null) =>
        new(Guid.NewGuid().ToString("N"), tenant, name, email, tenant);
}

/// <summary>Helpers for HttpClient calls with a bearer token.</summary>
public static class TestHttp
{
    public static HttpRequestMessage With(this HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static bool IsSuccess(this HttpStatusCode code) => (int)code is >= 200 and < 300;
}
