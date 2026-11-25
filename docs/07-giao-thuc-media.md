# 07 — Giao thức media: RFC đã làm và bằng chứng

Bảng chi tiết từng mục RFC nằm trong README của mỗi thư viện; tệp này tóm tắt và chỉ ra bằng chứng đối chứng độc lập
(đặc tả bài học 5: tự kiểm bằng mã của mình không chứng minh được gì).

| Thư viện | RFC chính | Đối chứng độc lập | Chi tiết |
|---|---|---|---|
| `BlueChat.Media.Sdp` | RFC 8866, 8829 (JSEP), 9143 (BUNDLE, bundle-only), 8851/8853 (rid, simulcast), 8285 (extmap), 8839 (candidate) | 17 offer thật của Chromium 159, Firefox 159, WebKit 27.2; câu trả lời được cả ba trình duyệt chấp nhận | `src/BlueChat.Media.Sdp/README.md` |
| `BlueChat.Media.Stun` | RFC 8489, 5769 | vector RFC 5769 §2.1–2.4 (bộ ghi tái tạo §2.2 từng byte), RFC 8489 B.1 theo errata 6268; trình duyệt, Pion, aiortc, coturn | `src/BlueChat.Media.Stun/README.md` |
| `BlueChat.Media.Ice` | RFC 8445 (lite), 7675 (consent), 6544 + 4571 (ICE-TCP), 7983 (demux) | Chromium, Firefox, Pion, aiortc kết nối; ICE-TCP và ICE restart với trình duyệt | `src/BlueChat.Media.Ice/README.md` |
| `BlueChat.Media.Dtls` | RFC 6347, 5764, 5705 (OpenSSL 3.x qua P/Invoke) | Chromium, Firefox, Pion, aiortc; OpenSSL 3.0.13 (Linux) và 3.2.3 (Windows) | `src/BlueChat.Media.Dtls/README.md` |
| `BlueChat.Media.Srtp` | RFC 3711, 7714 | vector RFC 3711 B.2/B.3, RFC 7714 §16–17; **libsrtp** (pylibsrtp 1.1.0) hai chiều, trùng từng byte | `src/BlueChat.Media.Srtp/README.md` |
| `BlueChat.Media.Rtp` | RFC 3550, 8285, 6464, 4585, 5104, 5506, transport-cc, REMB, RFC 7741 (VP8), 9628 (VP9), 6184 (H.264), AV1 RTP, 7587 (Opus) | **Pion** rtp/rtcp hai chiều: 47 gói Pion đọc đúng, 20 gói của mình Pion đọc đúng | `src/BlueChat.Media.Rtp/README.md` |
| `BlueChat.Turn` | RFC 8656, 8489 §9.2, TURN REST credentials | **coturn** `turnutils_uclient` UDP/TCP/TLS 0 % mất; **Pion** `turn/v4` ChannelData UDP/TCP/TLS 200/200; chặn địa chỉ nội bộ 403 | `src/BlueChat.Turn/README.md` |
| `BlueChat.Media.Transport` | ghép ICE-lite + DTLS + SRTP trên một cổng UDP + một cổng TCP | phép thử vòng lặp với trình duyệt thật (dưới) | `src/BlueChat.Media.Transport/README.md` |

## Nghiệm thu Phase 5: vòng lặp với trình duyệt thật

`tools/MediaLoopback` (nút media dựng từ các thư viện trên) + `tests/phase0/loopback-browsers.mjs`: trình duyệt gửi
âm thanh và video giả lên nút media, nút media gửi lại đúng các gói đó dưới SSRC của mình (khai trong SDP answer), trình
duyệt giải mã chính luồng của mình. PLI/FIR/NACK của chiều nhận được đổi thành yêu cầu tới chiều gửi.

| Nút media | Trình duyệt | UDP | ICE-TCP | ICE restart giữa chừng |
|---|---|---|---|---|
| Windows (OpenSSL 3.2.3) | Chromium 159.0.8154.6 | ✅ 101 khung 640×360, 252 gói Opus | ✅ | ✅ video tiếp tục (101 → 181 khung) |
| Windows (OpenSSL 3.2.3) | Firefox 159.0 | ✅ 152 khung | ✅ 28/28 lần | ✅ 153 → 273 khung |
| Linux container (OpenSSL 3.0.13) | Chromium 159.0.8154.6 | ✅ (3/3 lần) | ✅ (3/3) | ✅ (3/3) |
| Linux container (OpenSSL 3.0.13) | Firefox 159.0 | ✅ | 51/55 lần — 4 lần hỏng đều do cổng chuyển tiếp TCP của Docker Desktop (BUG-006), không do mã | ✅ |

Tiêu chí mỗi lượt: kết nối; ≥ 60 khung video giải mã có kích thước; ≥ 150 gói âm thanh; đường truyền đúng giao vận
(udp/tcp) theo `selectedCandidatePairId`; máy chủ không từ chối gói SRTP nào; với ICE restart, video tăng thêm ≥ 60
khung sau khi kết nối lại.

Chưa đo ở Phase 5 (để Phase 6 cùng SFU): mạng xấu (netem) cho vòng lặp, 9 người, simulcast, ước lượng băng thông.
