using BlueChat.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace BlueChat.Infrastructure.Identity;

/// <summary>
/// The ONE place where BlueChat depends on BlueID's contract (docs/00 Z1, B1). BlueChat does not manage sign-in: it
/// accepts BlueID access tokens (JWT, discovered through OIDC metadata of <see cref="BcSettings.BlueIdAuthority"/>).
/// Claim names below are the contract BlueChat asked for (B1); when BlueID publishes docs/05-hop-dong-tich-hop.md,
/// compare and adjust here only.
/// </summary>
public static class BlueIdClaims
{
    public const string Subject = "sub";
    public const string Tenant = "tid";
    public const string TenantName = "tenant_name";
    public const string Session = "sid";
    public const string Name = "name";
    public const string Email = "email";
    public const string Guest = "guest";
    public const string AuthenticationMethods = "amr";
}

public static class BlueIdAuthentication
{
    /// <summary>Hub paths where browsers pass the token in the query string (WebSocket cannot send headers).</summary>
    public const string HubPathPrefix = "/hubs";

    public static IServiceCollection AddBlueIdAuthentication(this IServiceCollection services, BcSettings settings)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = settings.BlueIdAuthority;
                o.Audience = settings.BlueIdAudience;
                o.RequireHttpsMetadata = settings.BlueIdRequireHttps;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.BlueIdAuthority + "/",
                    ValidIssuers = [settings.BlueIdAuthority, settings.BlueIdAuthority + "/"],
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = BlueIdClaims.Name,
                };
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var path = ctx.HttpContext.Request.Path;
                        if (path.StartsWithSegments(HubPathPrefix, StringComparison.Ordinal)
                            && ctx.Request.Query["access_token"].FirstOrDefault() is { Length: > 0 } token)
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }
}
