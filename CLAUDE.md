# CLAUDE.md — working notes for BlueChat

Specification: `PROMPT_BlueChat_ClaudeCode.md` (authoritative; work phase by phase, section 14).
Decisions: `docs/00-quyet-dinh-ky-thuat.md`. Teams parity: `docs/doi-chieu-teams.md` (update after every feature).
Remote: https://github.com/minhhung19872002/BlueChat (branch `main`). Commit per phase: `phase-N: …`.

## Ground rules (from the spec)
- Own code for media protocols (SDP, STUN/ICE, TURN, SRTP, RTP/RTCP, SFU, recorder). Crypto only from .NET BCL;
  DTLS only through the OS OpenSSL 3.x (`BlueChat.Media.Dtls`). Pion/aiortc/coturn/tshark only as test references.
- No stubs, no mocks in shipped code, no TODOs. Every business table has `tenant_id`. English code/comments/commits.
- Same function as Teams, own identity: never copy Teams names, icons, sounds, colours or layout; never read Teams
  code or traffic. Teams behaviour comes only from current Microsoft docs (URL + date) or using Teams as a user.

## BlueID
- The product owner is writing a **separate BlueID service**. Do **not** use `BlueMail.Identity` and do not build a
  temporary login in BlueChat. Requirements for BlueID are in docs/00 Z1 (B1–B9). Wait for the owner to say it is ready.

## Teams parity (docs/00 Z9)
- Manual comparison in the **Teams desktop app** through agent-desktop, on the account the owner provided, **only in
  the chat "Just me"**. Never message, invite or open any other chat/team. Observe as a user only.
- Screenshots of the full window stay in the scratchpad and are deleted after use; images committed to the repo are
  cropped to the feature under comparison, with no names/messages/notifications of other people.
- Multi-person features: write the step-by-step script and ask the owner to play the second person.

## Phase order
- Phase 1–4 wait for BlueID. Phase 5 media libraries are done ahead (owner's approval 2026-11-25) up to the browser
  loopback test. No SFU and no ticket-based signalling until BlueID exists.

## Dev environment (Windows)
- OpenSSL for tests on Windows: `BC_OPENSSL_DIR="C:/Program Files/Git/mingw64/bin"` (Git for Windows ships 3.2.x).
- Git Bash: prefix docker commands with container paths with `MSYS_NO_PATHCONV=1` (run-z4.sh exports it).
- Interop network `10.78.0.0/24` (BlueMail uses `10.77.0.0/24`). Probe 10.78.0.10, tools 10.78.0.20.
- `tshark` cannot judge encrypted SRTP/SRTCP bodies; only STUN/DTLS and our own packets are checked for malformation.
- Stop host-run `DtlsProbe` before rebuilding (it locks bin/): PowerShell
  `Get-CimInstance Win32_Process | ? CommandLine -match 'DtlsProbe' | % { Stop-Process -Id $_.ProcessId -Force }`.
