# BlueChat.Turn

TURN relay server (RFC 8656) for browsers that cannot reach a media node over UDP. Protocol core (`TurnCore`) has no
socket I/O — tests drive it with a fake clock and in-memory datagrams; `TurnServer` hosts it on real sockets.
STUN encoding comes from `BlueChat.Media.Stun`. BCL only (`SslStream` for TLS).

## Coverage

| RFC | Section | Status |
|---|---|---|
| RFC 8656 §3.1 | client transports UDP, TCP, TLS-over-TCP; stream framing (STUN self-delimiting, ChannelData padded to 4) | done |
| RFC 8656 §5, §7 | allocations per 5-tuple, Allocate (REQUESTED-TRANSPORT UDP only → else 442; retransmission returns the cached response; 437 on a new transaction), lifetime min(requested, max) but ≥ default | done |
| RFC 8656 §7.2 | REQUESTED-ADDRESS-FAMILY (440 when the family is not available), EVEN-PORT (R=0 honoured) | done |
| RFC 8656 §7.2 | RESERVATION-TOKEN, EVEN-PORT with R=1 | refused with 508 (no port reservations) |
| RFC 8656 §7.2 | DONT-FRAGMENT | answered 420 listing it (DF cannot be set portably on the relay socket) |
| RFC 8656 §7.2 | ADDITIONAL-ADDRESS-FAMILY (dual allocation) | ignored (comprehension-optional); one relayed address per allocation |
| RFC 8656 §8 | Refresh, LIFETIME 0 deletes, 443 on family mismatch | done |
| RFC 8656 §9 | CreatePermission: per peer IP, 300 s, all-or-nothing when one peer is refused | done |
| RFC 8656 §11 | Send / Data indications, silent discard without permission | done |
| RFC 8656 §12 | ChannelBind 0x4000–0x4FFF, one-to-one number ↔ peer, 600 s, refreshes the permission, 5-minute cooldown after expiry, ChannelData both ways | done |
| RFC 8656 §3.1 | closing the TCP/TLS connection deletes its allocation | done |
| RFC 8656 §6 (TCP relay, RFC 6062) | | not implemented (not needed: relayed transport is UDP) |
| RFC 8489 §9.2 | long-term credentials: 401 with REALM + NONCE, 400 when USERNAME/REALM/NONCE missing, 438 stale nonce, 441 another user's allocation; responses to authenticated requests signed with MESSAGE-INTEGRITY; FINGERPRINT on every response; bad FINGERPRINT → discard | done |
| RFC 8489 §9.2 | MESSAGE-INTEGRITY-SHA256, PASSWORD-ALGORITHMS, USERHASH | not done: a request with only MESSAGE-INTEGRITY-SHA256 gets 401 and clients fall back to SHA-1 |
| RFC 8489 §15 | unknown comprehension-required attributes → 420 with UNKNOWN-ATTRIBUTES | done |
| draft-uberti-behave-turn-rest-00 | time-limited credentials: username `<expiry>:<user>`, password base64(HMAC-SHA1(secret, username)); expired → 401 | done (`TurnRestCredentials.Issue` for the Api) |

Nonces are stateless: base64url(expiry ‖ HMAC-SHA256(key, expiry ‖ client IP)[..12]), 27 characters, 10 minutes,
bound to the client IP.

## Security policy (spec §6.6, principle 7, lesson 3)

- `PeerAddressPolicy.AllowList` (the default, empty = relay nothing): only the configured networks — BlueChat's own
  SFU nodes. An explicitly listed network may be private or loopback.
- `PeerAddressPolicy.DenyPrivate`: any public unicast address; refuses RFC 1918, CGNAT 100.64/10, loopback,
  link-local, 192.0.0/24, 198.18/15, IPv6 ULA/link-local/site-local/loopback.
- Always refused, even when listed: 0/8, 169.254.169.254 and fd00:ec2::254 (cloud metadata), multicast, 240/4 and
  broadcast, `::`. IPv6 forms that embed IPv4 (IPv4-mapped, IPv4-compatible, NAT64 64:ff9b::/96, 6to4 2002::/16) are
  judged by the embedded IPv4 address.
- Refused peers get 403 on CreatePermission/ChannelBind; Send indications and ChannelData re-check the policy.
- Limits: allocations per user (10), per client IP (50), per server (10 000); permissions (32) and channels (64) per
  allocation; token bucket per allocation (512 KB/s, burst 256 KB, both directions); client message ≤ 4096 bytes; peer
  datagram ≤ 2048 bytes; USERNAME ≤ 508 bytes; stream connections per IP (100) and total (10 000); TLS handshake 10 s;
  per-connection send queue 256 messages (drops when full, so a slow client never blocks the core).

## Evidence

- `tests/BlueChat.Turn.Tests`: 105 tests — every method and error path, 41-case address table, quotas, expiry with a
  fake clock, channel rules and cooldown, bandwidth, 20 000 random mutations, and real sockets on loopback (UDP, TCP,
  TLS × Send indication and ChannelData, allocation deleted on TCP close, garbage closes the stream).
- Independent clients (`tests/interop/run-turn.sh`, rig `tests/interop/turn-compose.yml`, 2026-11-25):

| Client | Transport | Path | Result |
|---|---|---|---|
| coturn `turnutils_uclient` 4.6.1, `-W <secret>` (REST credentials) `-s -c -n 200 -m 2 -l 160` | UDP | Send/Data indications | 0 lost (0.000000 %) |
| same with `-t` | TCP | Send/Data indications | 0 lost |
| same with `-t -S -p 5349` | TLS | Send/Data indications | 0 lost |
| `turnutils_uclient` without `-s` | UDP | ChannelBind | 400 by design: it picks numbers from the RFC 5766 range (e.g. 0x6126); RFC 8656 §12 allows only 0x4000–0x4FFF |
| Pion `turn/v4` v4.1.4 | UDP, TCP, TLS | ChannelData (channel 0x4000) | 200/200 echoes each; server-side capture: 2 ChannelBind successes, 0 errors, 797 ChannelData frames, 0 malformed |
| `turnutils_uclient` to peers 127.0.0.1, 10.79.0.10 (TURN host), 10.79.0.30 (not listed) | UDP | CreatePermission | 403 Forbidden each |
| `turnutils_uclient -W wrong-secret` | UDP | Allocate | refused ("Cannot complete Allocation") |

Run: `docker compose -f tests/interop/turn-compose.yml up -d` then `sh tests/interop/run-turn.sh`.
Console host: `dotnet run --project tools/TurnRunner -- --relay-ip <ip> --secret <s> --allow <cidr,...>`.

## Notes

- The relay receive path allocates per datagram (`SocketReceiveFromResult`); a pooled `SocketAddress` path is a
  Phase 12 optimisation once load tests show it matters.
