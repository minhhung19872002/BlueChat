# BlueChat.Media.Sdp

SDP parser, writer and JSEP answerer for the ICE-lite media server. Standalone: BCL only, no EF/MediatR, no I/O.
Malformed input throws `SdpParseException` with a stable `Code` and the line number; inputs are bounded by
`SdpLimits` (64 KB, 4096 lines, 4096 chars per line, 128 m= sections, 1024 attributes per section).

| RFC | Section | Status |
|---|---|---|
| RFC 8866 SDP | §5 line grammar and order, §5.2 o=, §5.14 m= (port/count), §5.13 a=, §6.6 rtpmap, §6.7 direction, §6.15 fmtp | done |
| RFC 8829 JSEP | §5.3.1 answer rules (mids and order kept, rejected section = port 0 + one format, mirrored direction) | done (answerer) |
| RFC 8843 BUNDLE | §7.3 answerer: accepted mids in `a=group:BUNDLE`, candidates in the tag section | done |
| RFC 8858 rtcp-mux | answer requires `a=rtcp-mux`; section without it is rejected | done |
| RFC 8839 ICE SDP | §5.1 candidate grammar (raddr/rport, extensions), §5.4 ufrag/pwd character set and length, ice-lite, ice-options, end-of-candidates | done |
| RFC 6544 ICE-TCP | `tcptype` extension | done |
| RFC 8122 / RFC 8842 | fingerprint; setup (answer is `passive`, offer must be actpass/active) | done |
| RFC 8285 | extmap with id 1–255, direction, extension attributes; extmap-allow-mixed | done |
| RFC 4585 / RFC 5104 | rtcp-fb parse; answer keeps nack, nack pli, ccm fir, transport-cc, goog-remb | done |
| RFC 4588 | rtx payload types paired by `apt` | done |
| RFC 8851 RID | rid id grammar, send/recv, `pt=` and other restrictions | done |
| RFC 8853 simulcast | send/recv lists, alternatives, `~` paused; answer turns offered `send` into `recv` | done |
| RFC 5576 | ssrc, ssrc-group (FID, SIM) | done |
| RFC 8830 msid / RFC 5888 group | done | done |
| RFC 8841 SCTP SDP | sctp-port, max-message-size, `UDP/DTLS/SCTP webrtc-datachannel` | done (answer only if enabled) |
| RFC 7587, 7741, 9628, 6184/7742, AV1 RTP spec | codec matching: Opus 48000/2, VP8, VP9 profile 0, H.264 constrained baseline with packetization-mode=1, AV1 profile 0 (optional) | done |

Evidence: `tests/BlueChat.Media.Sdp.Tests` — real offers from Chromium 159, Firefox 159 (Windows) and WebKit 27.2
(Playwright's Linux build in `mcr.microsoft.com/playwright:v1.65.1-noble`; the Windows WebKit build has no WebRTC) in
`Corpus/`, regenerated with `Corpus/generate.mjs gen`. Answers written by the tests (`out/answers`) are fed back to the
browsers with `Corpus/generate.mjs check`: all 15 accepted by `setRemoteDescription` (2026-11-25).
