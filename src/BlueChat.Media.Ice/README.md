# BlueChat.Media.Ice

Server side of ICE for the media node: ICE-lite answerer, one shared UDP port and one passive TCP port for every
session, RFC 7983 demultiplexing. No socket I/O here (the media host owns the sockets), so everything is unit-tested
with a manual clock. Depends only on `BlueChat.Media.Stun`.

| RFC | Section | Status |
|---|---|---|
| RFC 8445 ICE | §2.5 lite implementation (always controlled, no own checks) | done |
| | §5.1.2 candidate priority, §5.3 ufrag/pwd (CSPRNG, 48/144 bits) | done |
| | §7.3 answering checks: USERNAME `ours:theirs`, MESSAGE-INTEGRITY, FINGERPRINT, XOR-MAPPED-ADDRESS = source | done |
| | §7.3.1.1 role: a check without ICE-CONTROLLING gets 487 | done |
| | §8.2.2 nomination by USE-CANDIDATE; later nominations replace the pair (renomination) | done |
| | §9 ICE restart: old credentials valid until the first check with the new ones | done |
| RFC 8489 | §9.1.3 400 without USERNAME/MI; wrong MI dropped silently; §15 420 + UNKNOWN-ATTRIBUTES | done |
| RFC 7675 consent freshness | §5.1: Disconnected after 10 s without checks on the selected pair, Failed (stop sending) at 30 s | done |
| RFC 6544 ICE-TCP | §4.5 passive host candidate, §4.2 priority below UDP; framing RFC 4571 §2 | done (socket side in the media host) |
| RFC 7983 | first-byte demux STUN / ZRTP / DTLS / TURN channel / RTP / RTCP (RFC 5761 §4 for RTCP) | done |
| RFC 8839 | `candidate` attribute grammar for our host candidates | done |

Shared port (`SharedPortMux`): a remote address is bound to a session **only** after an authenticated check whose
USERNAME names that session's local ufrag; unauthenticated packets never bind an address. Bounded: 32 candidate pairs
per agent, 16 bound endpoints per session.
