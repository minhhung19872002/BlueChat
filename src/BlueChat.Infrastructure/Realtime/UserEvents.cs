using System.Text.Json;
using StackExchange.Redis;

namespace BlueChat.Infrastructure.Realtime;

/// <summary>
/// An event for every open session of one user (spec §2.2 "Phân phối"). Published only after the database commit
/// that caused it; the Gateway delivers it to that user's connections. Id is unique per event so a client can drop a
/// duplicate after a reconnect.
/// </summary>
public sealed record UserEvent(Guid Id, Guid TenantId, Guid UserId, string Type, JsonElement Payload, DateTimeOffset OccurredAt);

public interface IUserEventPublisher
{
    Task PublishAsync(UserEvent e, CancellationToken ct = default);
}

/// <summary>
/// Valkey transport between the API/Worker (publishers) and the Gateway nodes (subscribers). Each event goes to the
/// channel of its user's shard (<see cref="ChannelFor"/>) so a Gateway node can subscribe to all shards with one pattern.
/// </summary>
public sealed class ValkeyUserEventPublisher(IConnectionMultiplexer valkey) : IUserEventPublisher
{
    public const string ChannelPrefix = "bc:events:user:";
    public const int Shards = 64;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RedisChannel ChannelFor(Guid userId) =>
        RedisChannel.Literal(ChannelPrefix + (uint)userId.GetHashCode() % Shards);

    public static RedisChannel AllShards => RedisChannel.Pattern(ChannelPrefix + "*");

    public async Task PublishAsync(UserEvent e, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(e, Json);
        await valkey.GetSubscriber().PublishAsync(ChannelFor(e.UserId), body);
    }
}
