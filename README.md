# BlueChat

Enterprise chat, calls and online meetings (feature parity target: Microsoft Teams; own brand and own code).
Specification: `PROMPT_BlueChat_ClaudeCode.md`. Decisions: `docs/00-quyet-dinh-ky-thuat.md`.
Teams parity table: `docs/doi-chieu-teams.md`.

## Status

| Phase | Content | Status |
|---|---|---|
| 0 | Test infrastructure and decisions (Z1–Z9) | in progress: Z3–Z8 done; Z1 waits for the separate BlueID service; Z2 and Z9 need the product owner |
| 1–14 | | not started (Phase 1 needs BlueID) |

## Layout

- `src/BlueChat.Media.Dtls` — DTLS-SRTP through the OS OpenSSL 3.x (P/Invoke, memory BIOs)
- `tools/DtlsProbe` — Phase 0 prototype: browser ⇄ ICE-lite ⇄ OpenSSL DTLS, opens one SRTP packet with the exported keys
- `tests/BlueChat.Media.Dtls.Tests` — unit tests
- `tests/phase0` — Playwright (Chromium, Firefox) against the probe
- `tests/interop` — reference tools in Docker: Pion, aiortc, coturn, tshark, tc netem

## Quick checks

```sh
dotnet test tests/BlueChat.Media.Dtls.Tests          # Windows: set BC_OPENSSL_DIR to a folder with libssl-3-x64.dll
dotnet run --project tools/DtlsProbe                  # then, in tests/phase0: npm install && npm run dtls
docker compose -f tests/interop/compose.yml up -d --build && sh tests/interop/run-z4.sh
```
