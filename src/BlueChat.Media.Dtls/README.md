# BlueChat.Media.Dtls

DTLS-SRTP key agreement for the media server. The handshake itself is **OpenSSL 3.x of the operating system**
(P/Invoke); this library only moves datagrams through memory BIOs and checks the result. No cryptography is
implemented here. Standalone: BCL only, no EF/MediatR.

| RFC | Section | Status |
|---|---|---|
| RFC 6347 DTLS 1.2 | handshake, retransmission timer (`HandleTimeout`) | done (by OpenSSL) |
| RFC 5764 DTLS-SRTP | §4.1.2 `use_srtp` profiles, §4.2 key export and split | done |
| RFC 5705 exporters | `EXTRACTOR-dtls_srtp` | done |
| RFC 7714 | `SRTP_AEAD_AES_128_GCM` profile | done (preferred) |
| RFC 8122 §5 | `a=fingerprint` sha-256, checked after the handshake, before keys are released | done |
| RFC 8827 §6.5 | self-signed ECDSA P-256 certificate (generated with .NET) | done |
| RFC 7983 | demultiplexing (first byte 20–63 is DTLS) | caller's job (ICE/UDP layer) |
| RFC 9147 DTLS 1.3 | | not enabled yet (spec 6.3) |

Native library lookup: `libssl.so.3` / `libssl-3-x64.dll` / `libssl.3.dylib`, or the folder in `BC_OPENSSL_DIR`.
Minimum OpenSSL: 3.0. Evidence: `docs/00-quyet-dinh-ky-thuat.md` Z3 and Z4.
