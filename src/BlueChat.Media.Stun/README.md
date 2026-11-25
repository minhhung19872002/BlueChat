# BlueChat.Media.Stun

STUN message codec (BCL only, no allocation when reading; writer into a caller buffer). Used by ICE-lite
(`BlueChat.Media.Ice`) and TURN (`BlueChat.Turn`).

| RFC | Section | Status |
|---|---|---|
| RFC 8489 STUN | §5 header, type encoding (method/class bits) | done |
| | §6 transaction id (CSPRNG), §14 attribute TLV + padding, duplicate handling (first wins) | done |
| | §9.1.1 short-term key, §9.2.2 long-term key MD5 and SHA-256, §14.4 USERHASH | done |
| | §14.1 MAPPED-ADDRESS, §14.2 XOR-MAPPED-ADDRESS (IPv4, IPv6) | done |
| | §14.5 MESSAGE-INTEGRITY, §14.6 MESSAGE-INTEGRITY-SHA256 (full 32 bytes), §14.7 FINGERPRINT (must be last) | done |
| | §14.8 ERROR-CODE, §14.9 UNKNOWN-ATTRIBUTES, §15 comprehension-required detection | done |
| | password preparation | SASLprep mapping + NFKC, as the RFC 5769/8489 vectors expect (see below) |
| RFC 8445 ICE | §16.1 PRIORITY, USE-CANDIDATE, ICE-CONTROLLED, ICE-CONTROLLING | constants + typed accessors |
| RFC 8656 TURN | §18 attribute types | constants |
| RFC 7983 | first byte 0–3 + magic cookie (`LooksLikeStun`) | done |

Limits: message ≤ 4096 bytes by default (configurable), ≤ 64 attributes; every framing error has a stable
`StunParseError` code.

Test vectors: RFC 5769 §2.1–2.4 (all pass; the writer reproduces §2.2 byte for byte) and RFC 8489 Appendix B.1.
The B.1 vector **as printed** has a wrong header length (0x9c counts the header) — erratum **6268** (Verified,
2020-10-19) corrects the length, adds PASSWORD-ALGORITHM and recomputes the MAC. The printed vector is rejected with
`LengthMismatch`; the corrected one verifies with the password prepared to "TheMatrIX". Erratum 8746 (Reported, not
verified, 2026-02-08) argues OpaqueString keeps U+00AD; the verified corrected MAC was computed without it, so we keep
the SASLprep-style mapping.
