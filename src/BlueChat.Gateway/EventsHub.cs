using BlueChat.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BlueChat.Gateway;

/// <summary>
/// /hubs/events: one connection per open BlueChat window. Server → client "event" (a UserEvent); client → server only
/// <see cref="Ping"/>. Identity is the validated BlueID token (spec §2.1) mapped to tenant + user by IdentityResolver.
/// </summary>
[Authorize]
public sealed class EventsHub(IdentityResolver resolver, ICurrentUser user, ConnectionRegistry registry, UserConnectionLimiter limiter)
    : Hub
{
    public const string Path = "/hubs/events";
    public const string EventMethod = "event";
    private const string UserKey = "bc.user";

    public override async Task OnConnectedAsync()
    {
        var failure = await resolver.ResolveAsync(Context.User!, Context.ConnectionAborted);
        if (failure != ResolveFailure.None)
        {
            throw new HubException(failure switch
            {
                ResolveFailure.TenantSuspended => "tenant_suspended",
                ResolveFailure.UserLocked => "user_locked",
                _ => "unauthenticated",
            });
        }

        if (!await limiter.TryAcquireAsync(user.UserId, Context.ConnectionId))
        {
            throw new HubException("too_many_connections");
        }

        Context.Items[UserKey] = (user.TenantId, user.UserId);
        registry.Add(new LocalConnection(Context.ConnectionId, user.TenantId, user.UserId, Context));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(UserKey, out var v) && v is (Guid _, Guid userId))
        {
            registry.Remove(userId, Context.ConnectionId);
            await limiter.ReleaseAsync(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Round-trip check for clients; returns the server time (UTC).</summary>
    public DateTimeOffset Ping() => DateTimeOffset.UtcNow;
}
