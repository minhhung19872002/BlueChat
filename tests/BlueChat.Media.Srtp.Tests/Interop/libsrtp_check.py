"""libsrtp (via pylibsrtp) as an independent SRTP reference for BlueChat.Media.Srtp.

  generate <out.json>   libsrtp protects a fixed packet set (RTP across a sequence wrap, CSRC, header extension,
                        padding, two SSRCs; compound RTCP) for AEAD_AES_128_GCM and AES_CM_128_HMAC_SHA1_80.
                        SRTP is deterministic, so BlueChat's protect must reproduce these bytes exactly and its
                        unprotect must recover the plaintext.
  verify <in.json>      libsrtp unprotects packets that BlueChat protected (file written by the test
                        OurProtect_dump when BC_SRTP_DUMP is set) and checks the plaintext.

Run in the interop tools image:
  MSYS_NO_PATHCONV=1 docker run --rm -v "D:/Source/BlueChat:/src" bluechat/interop-tools:dev \
    /opt/aiortc/bin/python /src/tests/BlueChat.Media.Srtp.Tests/Interop/libsrtp_check.py generate \
    /src/tests/BlueChat.Media.Srtp.Tests/Interop/libsrtp-vectors.json
"""
import hashlib
import json
import struct
import sys

import pylibsrtp
from pylibsrtp import Policy, Session

PROFILES = {
    "AeadAes128Gcm": (Policy.SRTP_PROFILE_AEAD_AES_128_GCM, 12),
    "Aes128CmHmacSha1x80": (Policy.SRTP_PROFILE_AES128_CM_SHA1_80, 14),
}


def material(name: str, salt_len: int) -> tuple[bytes, bytes]:
    digest = hashlib.sha256(("bluechat-srtp-" + name).encode()).digest()
    return digest[:16], digest[16:16 + salt_len]


def rtp(seq: int, ssrc: int, payload: bytes, csrcs=(), extension: bytes | None = None, padding: int = 0) -> bytes:
    b0 = 0x80 | (0x20 if padding else 0) | (0x10 if extension is not None else 0) | len(csrcs)
    head = struct.pack("!BBHII", b0, 111, seq, seq * 960, ssrc)
    head += b"".join(struct.pack("!I", c) for c in csrcs)
    if extension is not None:
        assert len(extension) % 4 == 0
        head += struct.pack("!HH", 0xBEDE, len(extension) // 4) + extension
    pad = (b"\x00" * (padding - 1) + bytes([padding])) if padding else b""
    return head + payload + pad


def rtp_set() -> list[bytes]:
    packets = []
    ssrc_a, ssrc_b = 0x11223344, 0x55667788
    for i, seq in enumerate(list(range(65530, 65536)) + list(range(0, 6))):  # ROC 0 -> 1
        packets.append(rtp(seq, ssrc_a, bytes((seq + k) & 0xFF for k in range(1 + 23 * i))))
    packets.append(rtp(6, ssrc_a, b"csrc list", csrcs=(0xA1A2A3A4, 0xB1B2B3B4)))
    packets.append(rtp(7, ssrc_a, b"one-byte extension", extension=bytes([0x10, 0xAB, 0x00, 0x00])))
    packets.append(rtp(8, ssrc_a, b"padded payload", padding=4))
    for seq in range(1000, 1005):
        packets.append(rtp(seq, ssrc_b, bytes(range(seq % 200, seq % 200 + 50))))
    packets.append(rtp(9, ssrc_a, bytes(1200)))
    return packets


def rtcp_set() -> list[bytes]:
    ssrc = 0x11223344
    sr = struct.pack("!BBHIIIIII", 0x80, 200, 6, ssrc, 0x83AA7E80, 0, 960, 12, 3456)
    rr = struct.pack("!BBHIIIIIII", 0x81, 201, 7, ssrc, 0x55667788, 0x01000002, 1004, 20, 0, 0)
    bye = struct.pack("!BBHI", 0x81, 203, 1, ssrc)
    pli = struct.pack("!BBHII", 0x81, 206, 2, ssrc, 0x55667788)
    return [sr + rr, pli, sr, bye, sr + rr + bye]


def generate(path: str) -> None:
    out = {"libsrtp": f"pylibsrtp {getattr(pylibsrtp, '__version__', '?')}", "profiles": []}
    for name, (profile, salt_len) in PROFILES.items():
        key, salt = material(name, salt_len)
        tx = Session(Policy(key=key + salt, ssrc_type=Policy.SSRC_ANY_OUTBOUND, srtp_profile=profile))
        rx = Session(Policy(key=key + salt, ssrc_type=Policy.SSRC_ANY_INBOUND, srtp_profile=profile))
        rtp_vectors, rtcp_vectors = [], []
        for p in rtp_set():
            protected = tx.protect(p)
            assert rx.unprotect(protected) == p
            rtp_vectors.append({"plain": p.hex(), "protected": protected.hex()})
        for p in rtcp_set():
            protected = tx.protect_rtcp(p)
            assert rx.unprotect_rtcp(protected) == p
            rtcp_vectors.append({"plain": p.hex(), "protected": protected.hex()})
        out["profiles"].append({"profile": name, "masterKey": key.hex(), "masterSalt": salt.hex(),
                                "rtp": rtp_vectors, "rtcp": rtcp_vectors})
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=1)
    print(f"wrote {path}")


def verify(path: str) -> int:
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    failures = 0
    for prof in data["profiles"]:
        profile, _ = PROFILES[prof["profile"]]
        rx = Session(Policy(key=bytes.fromhex(prof["masterKey"] + prof["masterSalt"]),
                            ssrc_type=Policy.SSRC_ANY_INBOUND, srtp_profile=profile))
        ok = 0
        for kind, unprotect in (("rtp", rx.unprotect), ("rtcp", rx.unprotect_rtcp)):
            for v in prof[kind]:
                try:
                    plain = unprotect(bytes.fromhex(v["protected"]))
                except Exception as e:  # libsrtp error: auth failure, replay, ...
                    print(f"FAIL {prof['profile']} {kind}: {e}")
                    failures += 1
                    continue
                if plain.hex() != v["plain"]:
                    print(f"FAIL {prof['profile']} {kind}: plaintext differs")
                    failures += 1
                else:
                    ok += 1
        print(f"{prof['profile']}: libsrtp unprotected {ok} packets produced by BlueChat")
    return 1 if failures else 0


if __name__ == "__main__":
    if sys.argv[1] == "generate":
        generate(sys.argv[2])
    elif sys.argv[1] == "verify":
        sys.exit(verify(sys.argv[2]))
    else:
        sys.exit("usage: libsrtp_check.py generate|verify <file>")
