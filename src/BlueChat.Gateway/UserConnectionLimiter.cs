using StackExchange.Redis;

namespace BlueChat.Gateway;

/// <summary>
/// Per-user connection cap shared by all Gateway nodes: one Valkey sorted set per user, member = connection id,
/// score = lease expiry (ms). Acquire is a single Lua script (purge expired, count, add) so two nodes cannot both admit
/// the 21st connection. A node refreshes the leases of its connections; a crashed node's leases expire.
/// </summary>
public sealed class UserConnectionLimiter(IConnectionMultiplexer valkey, GatewayOptions options, TimeProvider clock)
{
    private const string AcquireScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZSCORE', KEYS[1], ARGV[3]) then
          redis.call('ZADD', KEYS[1], ARGV[2], ARGV[3])
          return 1
        end
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[4]) then
          return 0
        end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[3])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return 1
        """;

    public static RedisKey KeyFor(Guid userId) => "bc:conn:user:" + userId.ToString("N");

    public async Task<bool> TryAcquireAsync(Guid userId, string connectionId)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var lease = (long)options.SlotLease.TotalMilliseconds;
        var result = await valkey.GetDatabase().ScriptEvaluateAsync(AcquireScript, [KeyFor(userId)],
            [now, now + lease, connectionId, options.MaxConnectionsPerUser, lease * 2]);
        return (long)result == 1;
    }

    public Task ReleaseAsync(Guid userId, string connectionId) =>
        valkey.GetDatabase().SortedSetRemoveAsync(KeyFor(userId), connectionId);

    public async Task RefreshAsync(IEnumerable<(Guid UserId, string ConnectionId)> connections)
    {
        var expiry = clock.GetUtcNow().ToUnixTimeMilliseconds() + (long)options.SlotLease.TotalMilliseconds;
        var db = valkey.GetDatabase();
        var batch = db.CreateBatch();
        var tasks = connections.Select(c => batch.SortedSetAddAsync(KeyFor(c.UserId), c.ConnectionId, expiry)).ToList();
        batch.Execute();
        await Task.WhenAll(tasks);
    }
}
