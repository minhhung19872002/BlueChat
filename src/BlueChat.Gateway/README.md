# BlueChat.Gateway

Real-time push to open BlueChat windows (spec §2, §2.2 "Phân phối"): SignalR hub `/hubs/events` over WebSocket,
several nodes behind the load balancer, Valkey between them.

## Protocol

| Direction | Name | Content |
|---|---|---|
| server → client | `event` | `UserEvent` JSON: `id`, `tenantId`, `userId`, `type`, `payload`, `occurredAt` |
| client → server | `Ping()` | returns the server time (UTC) |

Authentication: BlueID access token (`access_token` query on the WebSocket request, `Authorization` on negotiate),
validated by `AddBlueIdAuthentication`; the connection closes when the token expires
(`CloseOnAuthenticationExpiration`). On connect, `IdentityResolver` maps the token to tenant + user.

## Delivery design: per-node Valkey subscription (Valkey is the backplane for this traffic)

Publishers (API, Worker) send each `UserEvent` to the Valkey channel of the user's shard
(`bc:events:user:<0..63>`, `ValkeyUserEventPublisher`). **Every Gateway node subscribes to all shards and delivers
only to its own connections** (`ConnectionRegistry`).

Why this rather than the SignalR Redis backplane:

- Exactly once per connection without coordination: each node receives each Valkey message once, and each
  connection lives on exactly one node. With the SignalR backplane, a node would still need an event source; if every
  node consumed the events and called `Clients.Group(...)`, each event would go out N times (N nodes). Avoiding that
  needs a single consumer (stream consumer group) plus a second hop through the backplane.
- One hop, in order: one user's events share one shard channel, and StackExchange.Redis processes a subscription
  queue sequentially, so a connection sees the publish order.
- Valkey **is** the backplane here: the fan-out across nodes happens in Valkey pub/sub. The Gateway makes no
  cross-node SignalR sends in Phase 1, so the SignalR Redis backplane package is not registered. If a later phase
  needs Gateway-initiated group sends across nodes, add `AddStackExchangeRedis` for those only.

Tenant guard: an event is sent to a connection only if `event.tenantId` equals the connection's tenant.

Pub/sub does not keep messages for a node that is down or reconnecting; the guarantee "no lost messages" belongs to the
data layer (Phase 2: `seq` per conversation and `user_event_seq` per user, clients fetch what they missed after a
reconnect, spec §2.2 "Đồng bộ").

## Control: closing a user's sessions

`bc:control:user` carries `{tenantId, userId, reason}`; every node aborts that user's local connections at once
(spec §2.1, lesson 8). `ValkeyRelay.PublishKickAsync` is the publisher API (BlueID's account events will call it once
BlueID exists, docs/00 Z1 B5).

## Limits (spec principle 8)

| Limit | Default | Variable |
|---|---|---|
| Connections per user, across all nodes | 20 | `BC_GATEWAY_MAX_CONNECTIONS_PER_USER` |
| Client → server message | 32 KB | `BC_GATEWAY_MAX_MESSAGE_BYTES` |
| Keep-alive / client timeout | 15 s / 45 s | |
| Event size accepted from Valkey | 256 KB | |
| CORS | `BC_WEB_URL` only | |

The per-user cap is a Valkey sorted set per user (member = connection id, score = lease expiry), admitted by one Lua
script so two nodes cannot both admit one connection too many; nodes refresh leases every 30 s, so a crashed node's
slots expire after 90 s.

Health: `/health/live`, `/health/ready` (PostgreSQL, Valkey, and the node's Valkey subscriptions).

## Tests (`tests/BlueChat.Gateway.Tests`)

PostgreSQL and Valkey in containers, the test BlueID issuer, **two real Gateway nodes** on loopback ports:

- user A with 3 connections over both nodes, B (same tenant) and C (other tenant): 200 events for A → each A
  connection gets all 200, once, in order; B and C get none; an event with A's user id but C's tenant reaches no one;
- 8 writers × 50 events in parallel → 400 unique ids per connection, per-writer order kept;
- kick closes A on both nodes, B stays connected;
- connection cap across nodes, slot freed on disconnect;
- negotiate without token, expired, foreign key, wrong audience → 401; valid → 200;
- readiness includes the subscriptions.
