using Microsoft.Extensions.Configuration;

namespace BlueChat.Gateway;

/// <summary>Gateway limits (spec principle 8: every network input is bounded). Environment: BC_GATEWAY_*.</summary>
public sealed record GatewayOptions
{
    /// <summary>Open connections per user across all Gateway nodes.</summary>
    public int MaxConnectionsPerUser { get; init; } = 20;

    /// <summary>Largest client→server hub message.</summary>
    public long MaxMessageBytes { get; init; } = 32 * 1024;

    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>No message (including keep-alive pings) from the client for this long closes the connection.</summary>
    public TimeSpan ClientTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a connection's slot in the per-user count survives without a refresh (crashed node).</summary>
    public TimeSpan SlotLease { get; init; } = TimeSpan.FromSeconds(90);

    public static GatewayOptions Load(IConfiguration c)
    {
        static int Int(string? v, int d) => int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var x) && x > 0 ? x : d;
        var d = new GatewayOptions();
        return d with
        {
            MaxConnectionsPerUser = Int(c["BC_GATEWAY_MAX_CONNECTIONS_PER_USER"], d.MaxConnectionsPerUser),
            MaxMessageBytes = Int(c["BC_GATEWAY_MAX_MESSAGE_BYTES"], (int)d.MaxMessageBytes),
        };
    }
}
