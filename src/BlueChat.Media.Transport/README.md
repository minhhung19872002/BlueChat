# BlueChat.Media.Transport

Socket side of a media node, built from the protocol libraries: one shared UDP port and one passive ICE-TCP port for
every session (spec §2.3), RFC 7983 demultiplexing, ICE-lite → DTLS (OpenSSL) → SRTP per browser. The SFU (Phase 6)
sits on top of `PeerTransport.RtpReceived/RtcpReceived` and `SendRtp/SendRtcp`.

| Piece | What it does | Built on |
|---|---|---|
| `MediaNode` | binds UDP + TCP, receive loops, RFC 4571 framing on TCP, 100 ms timer (DTLS retransmission, consent), counters | `SharedPortMux`, `Rfc4571Framer`, `PacketClassifier` |
| `PeerTransport` | one browser: ICE-lite agent, DTLS server, SRTP session after the handshake; sends only on the selected pair while consent holds | `IceLiteAgent`, `DtlsSession`, `SrtpSession` |

Rules kept here:

- An address is bound to a transport only by an authenticated STUN check (shared-port mux); DTLS, RTP and RTCP from
  any other address are dropped before parsing.
- DTLS keys are released only after the browser certificate matches the SDP fingerprint (`BlueChat.Media.Dtls`).
- ICE restart keeps DTLS and SRTP (same fingerprint) and swaps ICE credentials; the old ones work until the first
  check with the new ones.
- Nothing is sent after consent expires (RFC 7675, 30 s).
- Windows: `SIO_UDP_CONNRESET` is switched off so one unreachable peer cannot break the shared socket.

Evidence: the browser loopback test (`tools/MediaLoopback`, `tests/phase0/loopback-browsers.mjs`) — Chromium 159 and
Firefox 159 receive their own audio and video over UDP, over ICE-TCP, and across an ICE restart (docs/06 P5-xx).
