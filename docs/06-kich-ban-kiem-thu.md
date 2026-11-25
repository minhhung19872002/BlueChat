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
