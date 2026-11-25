# BlueChat.Media.Rtp

RTP/RTCP framing and payload descriptors for the media server (spec §6.4). BCL only, no EF/MediatR, no I/O.
Readers work on `ReadOnlySpan<byte>` and return offsets or small structs (no allocation on the packet path);
writers fill caller buffers. Every bad input raises `RtpParseException` with a stable `Code`
(`truncated`, `rtp_version`, `rtp_padding`, `rtp_extension`, `rtcp_length`, `rtcp_padding`, `rtcp_compound_first`,
`rtcp_twcc`, `rtcp_nack`, `vp9_descriptor`, `h264_fu_a`, `av1_obu_length`, …). The SFU never decodes media: the
payload readers only find key frames, frame boundaries and layers.

| Spec | Section | What | Status |
|---|---|---|---|
| RFC 3550 | §5.1, §5.3.1 | RTP header: CSRC, padding, extension block; in-place rewrite of seq/ts/SSRC/PT/marker (`RtpHeader`, `RtpPacketWriter`) | done |
| RFC 3550 | §A.1 | sequence wraparound compare, 64-bit unwrapper (`SequenceNumbers`, `SequenceUnwrapper`) | done |
| RFC 8285 | §4.2, §4.3 | one-byte and two-byte header extensions: read, write, rewrite in place (`HeaderExtensionReader`) | done |
| webrtc.org abs-send-time | — | 6.18 fixed point (`RtpExtensions.ReadAbsSendTime`) | done |
| RFC 6464 | §3 | ssrc-audio-level V + level | done |
| draft-holmer-rmcat-transport-wide-cc-extensions-01 | §2 | transport-wide sequence number | done |
| RFC 9143 §15 / RFC 8852 §3 / RFC 8851 §10 | — | MID, RID, repaired RID with character checks | done |
| AV1 RTP spec | App. A.8.1 | Dependency Descriptor mandatory fields | done (extended fields: not parsed) |
| RFC 3550 | §6.1, §6.4–6.7, §A.2 | SR, RR, report blocks, SDES (all chunks/items), BYE, APP passthrough; compound validation | done |
| RFC 5506 | §3.4 | reduced-size RTCP (`Validate(…, allowReducedSize: true)`) | done |
| RFC 4585 | §6.2.1, §6.3.1 | generic NACK read/build, PLI | done |
| RFC 5104 | §4.3.1 | FIR with command sequence numbers | done |
| draft-alvestrand-rmcat-remb-03 | §2.2 | REMB read/build (6-bit exponent, 18-bit mantissa) | done |
| draft-holmer-rmcat-transport-wide-cc-extensions-01 | §3.1 | transport-cc feedback: run-length, 1-bit and 2-bit status vectors, small/large deltas — read **and** build | done |
| RFC 7741 | §4.2, §4.3 | VP8 descriptor (X/I/M/L/T/K, PictureID, TL0PICIDX, TID/Y/KEYIDX), key frame from the P bit, in-place PictureID/TL0PICIDX rewrite | done |
| RFC 9628 | §4.2, §4.2.1 | VP9 descriptor incl. flexible-mode P_DIFF and scalability structure (sizes, picture group) | done |
| RFC 6184 | §5.6–5.8 | H.264 single NAL, STAP-A, FU-A; IDR/SPS/PPS detection. STAP-B/MTAP/FU-B rejected (packetization-mode 1 only) | done |
| AV1 RTP spec | §4.4, §4.5 | aggregation header Z/Y/W/N, LEB128 OBU elements, OBU type and extension (temporal/spatial id) | done |
| RFC 7587 / RFC 6716 | §4.2 / §3.1–3.2 | Opus TOC: mode, stereo, frame count and duration, 120 ms cap | done |

## Independent cross-check (Pion)

`tests/BlueChat.Media.Rtp.Tests/Vectors/gen/main.go` uses Pion `rtp` v1.10.6, `rtcp` v1.2.20 and `interceptor`
v0.1.51 (twcc recorder):

- `go run . gen` → `pion-vectors.json`: 47 packets built by Pion packetizers/marshallers (RTP with one- and two-byte
  extensions, CSRCs, padding; VP8/VP9/H.264/AV1 payloader output plus hand-made descriptors parsed by Pion; SR, RR,
  SDES, BYE, NACK, PLI, FIR, REMB, a compound; transport-cc feedback from Pion's recorder under clean, 10 % loss,
  bursty and reordered arrival). `PionVectorTests` must read every field exactly as Pion does.
- `go run . verify < ../ours.json`: 20 packets built by **our** writers (`WriterCrossCheckTests` writes the file),
  parsed by Pion and compared. Result 2026-11-25: **20 vectors, 0 failed**.

```sh
cd tests/BlueChat.Media.Rtp.Tests/Vectors
MSYS_NO_PATHCONV=1 docker run --rm -v "$PWD:/v" -v bc-gomod:/go/pkg/mod -w /v/gen golang:1.25-bookworm \
  sh -c 'go run . gen > /v/pion-vectors.json && go run . verify < /v/ours.json'
```

Note: Pion computes abs-send-time from NTP time (epoch 1900); `AbsSendTimeFromMicroseconds` accepts any epoch.

## Tests

`dotnet test tests/BlueChat.Media.Rtp.Tests`: 84 tests — Pion vectors, writer round trips, unit cases for every
error code, and mutation fuzzing of all readers (~75 000 inputs seeded from the Pion vectors; only
`RtpParseException` may escape). The fuzzer found one bug (AV1 LEB128 length ≥ 2^31 narrowed to a negative `int`),
fixed with a red-then-green test.
