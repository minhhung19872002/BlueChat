using System.Text.Json;
using BlueChat.Infrastructure.Realtime;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace BlueChat.Gateway;

/// <summary>Message on <see cref="ValkeyRelay.ControlChannel"/>: close every connection of a user (locked account, …).</summary>
public sealed record UserControl(Guid TenantId, Guid UserId, string Reason);

/// <summary>
/// Valkey → local connections. Each Gateway node subscribes to all user-event shards and to the control channel, and
/// acts only on its own connections (<see cref="ConnectionRegistry"/>). Events of one user share one shard channel and
/// StackExchange.Redis delivers a subscription queue in order, so per-connection order is the publish order.
/// </summary>
public sealed class ValkeyRelay(
    IConnectionMultiplexer valkey, ConnectionRegistry registry, IHubContext<EventsHub> hub, UserConnectionLimiter limiter,
    TimeProvider clock, ILogger<ValkeyRelay> log) : BackgroundService
{
    public const string ControlChannelName = "bc:control:user";

    public static RedisChannel ControlChannel => RedisChannel.Literal(ControlChannelName);

    private const int MaxEventBytes = 256 * 1024;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once both Valkey subscriptions are active (readiness probe, tests).</summary>
    public Task Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = valkey.GetSubscriber();
        var events = await sub.SubscribeAsync(ValkeyUserEventPublisher.AllShards);
        events.OnMessage(async m => await DeliverAsync(m.Message, stoppingToken));
        var control = await sub.SubscribeAsync(ControlChannel);
        control.OnMessage(async m => await KickAsync(m.Message));
        _ready.TrySetResult();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await limiter.RefreshAsync(registry.All().Select(c => (c.UserId, c.ConnectionId)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await events.UnsubscribeAsync();
            await control.UnsubscribeAsync();
        }
    }

    internal async Task DeliverAsync(RedisValue message, CancellationToken ct)
    {
        UserEvent? e;
        try
        {
            var bytes = (byte[]?)message;
            if (bytes is null || bytes.Length > MaxEventBytes)
            {
                return;
            }

            e = JsonSerializer.Deserialize<UserEvent>(bytes, ValkeyUserEventPublisher.Json);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Dropped a malformed user event");
            return;
        }

        if (e is null)
        {
            return;
        }

        // Tenant must match the connection's tenant: an event can never cross tenants (spec §7.2).
        foreach (var c in registry.Of(e.TenantId, e.UserId))
        {
            try
            {
                await hub.Clients.Client(c.ConnectionId).SendAsync(EventsHub.EventMethod, e, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not deliver event {EventId} to {ConnectionId}", e.Id, c.ConnectionId);
            }
        }
    }

    internal Task KickAsync(RedisValue message)
    {
        UserControl? c;
        try
        {
            c = JsonSerializer.Deserialize<UserControl>((byte[])message!, ValkeyUserEventPublisher.Json);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }

        if (c is null)
        {
            return Task.CompletedTask;
        }

        foreach (var conn in registry.Of(c.TenantId, c.UserId))
        {
            log.LogInformation("Closing {ConnectionId} of user {UserId}: {Reason}", conn.ConnectionId, c.UserId, c.Reason);
            conn.Context.Abort();
        }

        return Task.CompletedTask;
    }

    /// <summary>Publishes a kick to every Gateway node (used by the API/Worker when BlueID reports a locked account).</summary>
    public static Task PublishKickAsync(IConnectionMultiplexer valkey, UserControl c) =>
        valkey.GetSubscriber().PublishAsync(ControlChannel, JsonSerializer.SerializeToUtf8Bytes(c, ValkeyUserEventPublisher.Json));
}

/// <summary>Readiness includes the Valkey subscriptions: a node that cannot receive events must not take traffic.</summary>
internal sealed class RelayHealthCheck(ValkeyRelay relay) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(relay.Ready.IsCompletedSuccessfully
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("valkey subscriptions not active"));
}
