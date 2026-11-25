using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BlueChat.Infrastructure.Configuration;

/// <summary>
/// Process configuration from BC_* environment variables (spec §0.1). A missing required variable stops the process
/// with a message naming it (see deploy/.env.example).
/// </summary>
public sealed class BcSettings
{
    public required string DbConnection { get; init; }

    public required string Valkey { get; init; }

    /// <summary>BlueID issuer URL (OIDC discovery at /.well-known/openid-configuration). docs/00 Z1.</summary>
    public required string BlueIdAuthority { get; init; }

    /// <summary>Audience of BlueChat API access tokens (docs/00 Z1, B1).</summary>
    public string BlueIdAudience { get; init; } = "bluechat-api";

    /// <summary>Plain-HTTP BlueID only in development and tests.</summary>
    public bool BlueIdRequireHttps { get; init; } = true;

    /// <summary>Public URL of the web app (CORS origin of the API and Gateway).</summary>
    public required string WebUrl { get; init; }

    /// <summary>CIDR ranges of reverse proxies whose X-Forwarded-For is trusted.</summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>
    /// E-mail addresses given the TenantAdmin role the first time they sign in, when their tenant has no admin yet
    /// (bootstrap of a new tenant; afterwards admins assign roles in the admin centre).
    /// </summary>
    public IReadOnlySet<string> BootstrapAdmins { get; init; } = new HashSet<string>();

    /// <summary>BlueID tenant ids whose users are platform administrators (all tenants).</summary>
    public IReadOnlySet<string> PlatformTenants { get; init; } = new HashSet<string>();

    public string LogDirectory { get; init; } = "logs";

    public static BcSettings Load(IConfiguration config)
    {
        string Required(string key)
        {
            var v = config[key];
            return string.IsNullOrWhiteSpace(v)
                ? throw new InvalidOperationException($"Required environment variable {key} is not set. See deploy/.env.example.")
                : v;
        }

        static IReadOnlyList<string> List(string? v) =>
            (v ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new BcSettings
        {
            DbConnection = WithPoolSize(Required("BC_DB_CONNECTION"), config["BC_DB_MAX_POOL"]),
            Valkey = Required("BC_VALKEY"),
            BlueIdAuthority = Required("BC_BLUEID_AUTHORITY").TrimEnd('/'),
            BlueIdAudience = config["BC_BLUEID_AUDIENCE"] ?? "bluechat-api",
            BlueIdRequireHttps = !string.Equals(config["BC_BLUEID_ALLOW_HTTP"], "true", StringComparison.OrdinalIgnoreCase),
            WebUrl = Required("BC_WEB_URL").TrimEnd('/'),
            TrustedProxies = List(config["BC_TRUSTED_PROXIES"]),
            BootstrapAdmins = List(config["BC_BOOTSTRAP_ADMINS"]).Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal),
            PlatformTenants = List(config["BC_PLATFORM_TENANTS"]).ToHashSet(StringComparer.Ordinal),
            LogDirectory = config["BC_LOG_DIR"] ?? "logs",
        };
    }

    /// <summary>BC_DB_MAX_POOL caps this process's connections unless the connection string sets one.</summary>
    public static string WithPoolSize(string connection, string? maxPool)
    {
        var b = new NpgsqlConnectionStringBuilder(connection);
        if (int.TryParse(maxPool, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0
            && !connection.Contains("Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            b.MaxPoolSize = max;
        }

        return b.ConnectionString;
    }
}
