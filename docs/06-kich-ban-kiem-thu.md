# 06 — Kịch bản kiểm thử

## Lệnh

| Việc | Lệnh |
|---|---|
| Unit test DTLS (Windows) | `BC_OPENSSL_DIR="C:/Program Files/Git/mingw64/bin" dotnet test tests/BlueChat.Media.Dtls.Tests` |
| Unit test DTLS (Linux) | `dotnet test tests/BlueChat.Media.Dtls.Tests` (cần `libssl3`) |
| Trình duyệt thật ↔ DTLS | `dotnet run --project tools/DtlsProbe` rồi `cd tests/phase0 && npm install && npx playwright install chromium firefox && npm run dtls` |
| Đối chứng độc lập (Pion, aiortc, tshark, coturn, netem) | `docker compose -f tests/interop/compose.yml up -d --build` rồi `sh tests/interop/run-z4.sh`; dọn: `docker compose -f tests/interop/compose.yml down` |

Thiết bị giả cho trình duyệt: Chromium `--use-fake-device-for-media-stream --use-fake-ui-for-media-stream`
(video từ tệp: `--use-file-for-fake-video-capture=<tệp .y4m>`); Firefox `media.navigator.streams.fake = true`,
`media.navigator.permission.disabled = true`.

Mạng xấu: `tc qdisc add dev eth0 root netem loss 10% delay 150ms` (thêm `rate 500kbit` để giới hạn băng thông) trong
container `tools`; gỡ bằng `tc qdisc del dev eth0 root`.

## Phase 0

| Mã | Kịch bản | Mong đợi | Kết quả | |
|---|---|---|---|---|
| P0-01 | Bắt tay hai `DtlsSession` trong tiến trình | Connected, DTLSv1.2, AEAD_AES_128_GCM, khoá đối xứng chéo | `Two_sessions_complete_the_handshake…` (Windows 3.2.3, Linux 3.0.13) | ✅ |
| P0-02 | Fingerprint trong SDP sai | Failed `fingerprint_mismatch`, không có khoá | `Wrong_SDP_fingerprint_fails…` | ✅ |
| P0-03 | Gói rác | Không Connected, không khoá | `Garbage_datagram_fails…` | ✅ |
| P0-04 | Chromium 159 ↔ nguyên mẫu | connected, gói SRTP của trình duyệt mở được bằng khoá xuất | Windows và Linux | ✅ |
| P0-05 | Firefox 159 ↔ nguyên mẫu | như P0-04 | Windows và Linux | ✅ |
| P0-06 | Pion v4.2.29 ↔ nguyên mẫu | connected, SRTP mở được | mạng sạch và netem 10 %/150 ms | ✅ |
| P0-07 | aiortc 1.16.0 ↔ nguyên mẫu | như P0-06 | mạng sạch và netem 10 %/150 ms | ✅ |
| P0-08 | tshark chấm gói STUN/DTLS và gói máy chủ gửi | 0 gói hỏng | 20 gói, 0 hỏng | ✅ |
| P0-09 | coturn `turnutils_uclient` | chạy, 0 % mất | 0 % | ✅ |

## Phase 5 (thư viện media, làm trước Phase 1–4 theo đồng ý của chủ sản phẩm)

Lệnh: `dotnet test BlueChat.slnx` (Windows: đặt `BC_OPENSSL_DIR`); vòng lặp: `dotnet run --project tools/MediaLoopback`
rồi `cd tests/phase0 && node loopback-browsers.mjs` (`ONLY=firefox:tcp REPEAT=25` để lặp một kịch bản); TURN:
`docker compose -f tests/interop/turn-compose.yml up -d && sh tests/interop/run-turn.sh`.

| Mã | Kịch bản | Mong đợi | Kết quả | |
|---|---|---|---|---|
| P5-01 | STUN: vector RFC 5769 §2.1–2.4, RFC 8489 B.1 (errata 6268) | xác thực đúng; bộ ghi tái tạo §2.2 từng byte | 26 phép thử | ✅ |
| P5-02 | STUN: 20 000 đột biến | không ném ngoại lệ, không xác thực nhầm | `Random_mutations…` | ✅ |
| P5-03 | ICE-lite: đề cử, đề cử lại, 487, 420, consent 10 s/30 s, ICE restart, mux chỉ gắn địa chỉ qua kiểm có xác thực | như RFC 8445/7675 | 21 phép thử | ✅ |
| P5-04 | SRTP/SRTCP: vector RFC 3711, 7714; ROC, cửa sổ chống phát lại; không cấp phát bộ nhớ | đúng vector; 0 byte cấp phát mỗi gói | 35 phép thử | ✅ |
| P5-05 | SRTP ↔ libsrtp hai chiều | trùng từng byte; libsrtp mở 180/180 gói của mình | `LibsrtpInteropTests` | ✅ |
| P5-06 | RTP/RTCP ↔ Pion hai chiều | 47 gói Pion đọc đúng; Pion đọc đúng 20 gói của mình | 84 phép thử | ✅ |
| P5-07 | SDP: 17 offer thật (Chromium, Firefox, WebKit), đột biến, câu trả lời được trình duyệt nhận | parse, ghi lại y nguyên, answer hợp lệ | 153 phép thử; 15/15 answer được 3 trình duyệt nhận | ✅ |
| P5-08 | SDP: Firefox max-bundle (`a=bundle-only`, port 0) | video không bị từ chối | `Bundle_only…` (BUG-005) | ✅ |
| P5-09 | TURN: mọi phương thức, nhánh lỗi, chặn 41 địa chỉ, hạn mức, đồng hồ giả, socket thật UDP/TCP/TLS | như RFC 8656 | 105 phép thử | ✅ |
| P5-10 | TURN ↔ coturn `turnutils_uclient` UDP/TCP/TLS | 0 % mất | 0 % cả ba | ✅ |
| P5-11 | TURN ↔ Pion `turn/v4` ChannelData UDP/TCP/TLS | 200/200 | 200/200 cả ba | ✅ |
| P5-12 | TURN: peer nội bộ/chính máy TURN/ngoài danh sách; sai secret | 403; không cấp phát | 403; từ chối | ✅ |
| P5-13 | Vòng lặp Chromium 159: UDP, ICE-TCP, ICE restart (nút Windows và Linux) | nhận lại video ≥ 60 khung, âm ≥ 150 gói, đúng giao vận, video tiếp tục sau restart | qua cả 6 ô | ✅ |
| P5-14 | Vòng lặp Firefox 159: như P5-13 | như P5-13 | Windows 28/28 TCP; Linux 51/55 TCP (4 lần do Docker Desktop, BUG-006) | ✅ |

