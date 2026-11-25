using Microsoft.AspNetCore.SignalR;

namespace BlueChat.Gateway;

/// <summary>One open hub connection on this node.</summary>
public sealed record LocalConnection(string ConnectionId, Guid TenantId, Guid UserId, HubCallerContext Context);

/// <summary>
/// Connections open on THIS Gateway node, by user. Events and kicks arriving from Valkey are applied to the local
/// connections only; every node holds a disjoint set, so each connection is served by exactly one node.
/// A single lock keeps add/remove of the same user consistent (an add racing the removal of the user's last
/// connection must not be lost); operations are tiny, so contention is negligible.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Dictionary<string, LocalConnection>> _byUser = [];

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byUser.Values.Sum(c => c.Count);
            }
        }
    }

    public void Add(LocalConnection c)
    {
        lock (_sync)
        {
            if (!_byUser.TryGetValue(c.UserId, out var set))
            {
                set = new Dictionary<string, LocalConnection>(StringComparer.Ordinal);
                _byUser[c.UserId] = set;
            }

            set[c.ConnectionId] = c;
        }
    }

    public void Remove(Guid userId, string connectionId)
    {
        lock (_sync)
        {
            if (_byUser.TryGetValue(userId, out var set) && set.Remove(connectionId) && set.Count == 0)
            {
                _byUser.Remove(userId);
            }
        }
    }

    public IReadOnlyList<LocalConnection> All()
    {
        lock (_sync)
        {
            return _byUser.Values.SelectMany(s => s.Values).ToList();
        }
    }

    /// <summary>Local connections of <paramref name="userId"/> inside <paramref name="tenantId"/> (tenant must match).</summary>
    public IReadOnlyList<LocalConnection> Of(Guid tenantId, Guid userId)
    {
        lock (_sync)
        {
            return _byUser.TryGetValue(userId, out var set)
                ? set.Values.Where(c => c.TenantId == tenantId).ToList()
                : [];
        }
    }
}
