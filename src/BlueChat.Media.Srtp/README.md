# BlueChat.Media.Srtp

SRTP/SRTCP packet protection written for BlueChat; the cryptographic primitives come only from .NET
(`AesGcm`, `Aes` as the counter-mode block function, `IncrementalHash` HMAC-SHA1). Standalone (BCL + the
`SrtpKeyMaterial` type of `BlueChat.Media.Dtls`), no I/O, in place, no allocation per packet in steady state.

```csharp
using var srtp = new SrtpSession(dtlsSession.Keys!);      // local keys protect, remote keys unprotect
srtp.ProtectRtp(buffer, length, out var wireLength);     // buffer needs srtp.RtpOverhead spare octets
srtp.UnprotectRtp(packet, out var rtpLength);            // returns SrtpResult, never throws on bad input
srtp.ProtectRtcp(buffer, length, out wireLength);        // E = 1, + RtcpOverhead octets
srtp.UnprotectRtcp(packet, out var rtcpLength);
```

Thread-safety: serialize protect calls, serialize unprotect calls (separate contexts, so one sender thread and one
receiver thread may run at the same time).

| RFC | Section | Status |
|---|---|---|
| RFC 3711 SRTP | §3.1 packet format, §3.3 SRTP processing (sender, receiver) | done |
| | §3.3.1 index estimation (ROC, s_l; Appendix A) | done, rollover tested both ways |
| | §3.3.2 replay protection | done, 128-packet window, updated only after authentication |
| | §3.4 SRTCP: E-flag, 31-bit index, tag over ESRTCP | done; index exhaustion reported (`IndexExhausted`) |
| | §4.1.1 AES Counter Mode | done (B.2 vectors) |
| | §4.2.1 HMAC-SHA1, §5.2 80-bit tag | done |
| | §4.3 key derivation, kdr = 0, labels 0x00–0x05 | done (B.3 vectors) |
| | MKI | not used (WebRTC / RFC 8827 §6.5) |
| RFC 7714 AES-GCM for SRTP | §8 SRTP AEAD_AES_128_GCM, §9 SRTCP (E = 1 and E = 0) | done (§16.1, §17.1, §17.3 vectors) |
| | §11 key derivation (96-bit salt right-padded with zeros) | done, matches libsrtp |
| | AEAD_AES_256_GCM | not done (not negotiated by `BlueChat.Media.Dtls`) |
| RFC 5764 §4.1.2 | SRTP_AES128_CM_HMAC_SHA1_80, SRTP_AEAD_AES_128_GCM | done |
| | SRTP_AES128_CM_HMAC_SHA1_32 | not done: `SrtpProfile` (in BlueChat.Media.Dtls) has no value for it and the DTLS context does not offer it |

Evidence (tests/BlueChat.Media.Srtp.Tests, 35 tests, Windows and Linux):

- Published vectors: RFC 3711 B.2, B.3; RFC 7714 §16.1.1, §16.1.2, §17.1 (both directions), §17.3.
- libsrtp (pylibsrtp 1.1.0, in `bluechat/interop-tools:dev`): `Interop/libsrtp_check.py generate` wrote
  `Interop/libsrtp-vectors.json` (RTP across a sequence wrap, CSRC, header extension, padding, two SSRCs, compound
  RTCP, both profiles). Our protect reproduces libsrtp's bytes exactly and our unprotect opens them.
  Reverse leg: `BC_SRTP_DUMP=<file> dotnet test --filter OurProtect_dump` then `libsrtp_check.py verify <file>`:
  libsrtp opened all 90 + 90 packets produced by BlueChat (2026-11-25).
- Robustness: tampering (header, payload, tag), wrong keys, replay, window edge, truncated/malformed/oversized input,
  2 000 random datagrams, stream limit, zero allocation per protect/unprotect after warm-up.
