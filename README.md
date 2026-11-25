# BlueChat

Enterprise chat, calls and online meetings (feature parity target: Microsoft Teams; own brand and own code).
Specification: `PROMPT_BlueChat_ClaudeCode.md`. Decisions: `docs/00-quyet-dinh-ky-thuat.md`.
Teams parity table: `docs/doi-chieu-teams.md`.

## Status

| Phase | Content | Status |
|---|---|---|
| 0 | Test infrastructure and decisions (Z1–Z9) | Z3–Z9 done; Z1 waits for the separate BlueID service; Z2 (network) not decided by the owner yet |
| 1 | Foundation | **paused until BlueID is ready (owner, 2026-11-25)**. Done and tested: Domain/Infrastructure/Application/Api, PostgreSQL schema + migration, tenant filter, hash-chained audit log, en/vi server strings, BlueID integration point (`Infrastructure/Identity/BlueId.cs`), Gateway SignalR + Valkey (2 nodes, exactly-once per session). Not committed, unfinished: web shell (`web/`), Docker Compose (`deploy/`) |
| 2–4 | Chat, teams/channels, activity/search/files | waiting for BlueID |
| 5 | Media libraries: SDP, STUN/ICE-lite/ICE-TCP, DTLS-SRTP, RTP/RTCP, TURN — up to the browser loopback test | **done, ahead of Phase 1–4** with the owner's approval (2026-11-25). Chromium 159 and Firefox 159 receive their own audio/video back over UDP, ICE-TCP and across an ICE restart (docs/07). SFU and ticket-based signalling stay for Phase 6 (need BlueID) |
| 6–14 | | not started |

## Layout

- `src/BlueChat.Domain`, `src/BlueChat.Infrastructure`, `src/BlueChat.Application`, `src/BlueChat.Api` — backend (Clean Architecture + CQRS)
- `src/BlueChat.Gateway` — SignalR `/hubs/events`, delivers user events from Valkey to every open session
- `tests/BlueChat.IntegrationTests`, `tests/BlueChat.Gateway.Tests` — real PostgreSQL + Valkey (Testcontainers) and a test-only BlueID issuer (`tests/BlueChat.TestSupport`)
- `src/BlueChat.Media.Sdp` — SDP parser/writer, ICE-lite JSEP answerer
- `src/BlueChat.Media.Stun` — STUN codec
- `src/BlueChat.Media.Ice` — ICE-lite, shared-port mux, ICE-TCP framing, RFC 7983 demux
- `src/BlueChat.Media.Dtls` — DTLS-SRTP through the OS OpenSSL 3.x (P/Invoke, memory BIOs)
- `src/BlueChat.Media.Srtp` — SRTP/SRTCP (AES-GCM, AES-CM + HMAC-SHA1)
- `src/BlueChat.Media.Rtp` — RTP/RTCP, header extensions, transport-cc, VP8/VP9/H.264/AV1/Opus descriptors
- `src/BlueChat.Media.Transport` — media node sockets: one UDP + one ICE-TCP port, ICE → DTLS → SRTP per browser
- `src/BlueChat.Turn` — TURN server (RFC 8656) with allow-list relay policy
- `tools/MediaLoopback` — Phase 5 acceptance: browser gets its own streams back
- `tools/TurnRunner` — runs the TURN server for interop tests
- `tools/DtlsProbe` — Phase 0 prototype: browser ⇄ ICE-lite ⇄ OpenSSL DTLS, opens one SRTP packet with the exported keys
- `tests/BlueChat.Media.Dtls.Tests` — unit tests
- `tests/phase0` — Playwright (Chromium, Firefox) against the probe
- `tests/interop` — reference tools in Docker: Pion, aiortc, coturn, tshark, tc netem; TURN interop (`run-turn.sh`)
- `tests/BlueChat.*.Tests` — unit tests per library (vectors from RFCs, libsrtp, Pion; mutation fuzzing)

## Quick checks

```sh
dotnet test tests/BlueChat.Media.Dtls.Tests          # Windows: set BC_OPENSSL_DIR to a folder with libssl-3-x64.dll
dotnet run --project tools/DtlsProbe                  # then, in tests/phase0: npm install && npm run dtls
docker compose -f tests/interop/compose.yml up -d --build && sh tests/interop/run-z4.sh
```
