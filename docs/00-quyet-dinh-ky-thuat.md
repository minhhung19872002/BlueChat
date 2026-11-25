# 00 — Quyết định kỹ thuật (Technical decisions)

Tài liệu kỹ thuật nội bộ. Mỗi quyết định ghi: **bối cảnh → quyết định → bằng chứng → hệ quả**.
Mục nào cần chủ sản phẩm xác nhận được đánh dấu **[CẦN CHỦ SẢN PHẨM]**.

Ngày lập: 2026-11-25 (Phase 0). Đặc tả: `PROMPT_BlueChat_ClaudeCode.md`.

## Tình trạng Phase 0

| # | Việc | Tình trạng |
|---|---|---|
| Z1 | BlueID | **Đang chờ** — BlueID được xây ở kho riêng; hợp đồng tích hợp (`docs/05-hop-dong-tich-hop.md` của BlueID) sẽ được đối chiếu với B1–B9 khi có |
| Z2 | Hạ tầng mạng | **Chưa quyết** (chủ sản phẩm, 2026-11-25) — mọi thử nghiệm media chạy trên máy dev |
| Z3 | DTLS qua OpenSSL | **Xong** — Chromium 159 và Firefox 159, trên Windows (OpenSSL 3.2.3) và Linux (OpenSSL 3.0.13) |
| Z4 | Khung kiểm thử media | **Xong** — Pion, aiortc, coturn, tshark, netem chạy; Pion và aiortc bắt tay được với nguyên mẫu (mục "Z4 — kết quả") |
| Z5 | Kho S3 | **Xong** — Garage v2.5.0 (giống BlueMail) |
| Z6 | Giới hạn của Teams | **Xong** — bảng trong `doi-chieu-teams.md` (trang đọc 2026-11-25) |
| Z7 | Ước lượng tải | **Xong (ước lượng, chưa đo)** — sẽ đo lại ở Phase 6 và 12 |
| Z8 | Khung bảng đối chiếu | **Xong** — `doi-chieu-teams.md`, mọi dòng `Chưa làm` / `Ngoài phạm vi` |
| Z9 | Đối chiếu Teams | **Thay quy trình** — đối chiếu tay trên Teams desktop qua điều khiển máy tính, chỉ trong chat "Just me" (mục Z9) |

---

## Z1. BlueID — chờ dịch vụ riêng của chủ sản phẩm

**Bối cảnh.** Đặc tả (mục 2.1) yêu cầu BlueChat không tự quản người dùng. Trên máy dev, BlueID đang nằm trong
BlueMail (`BlueMail.Identity`, OpenIddict, chung CSDL với BlueMail, chỉ có client `bluemail-web`).

**Quyết định (chủ sản phẩm, 2026-11-25).** **Không** dùng và không sửa BlueID trong BlueMail. BlueID được xây ở kho
riêng; phiên làm BlueID đối chiếu hợp đồng tích hợp với bảng B1–B9 dưới đây. Khi BlueID có
`docs/05-hop-dong-tich-hop.md`, chủ sản phẩm gửi sang để BlueChat đối chiếu lại B1–B9. BlueChat **không** dựng bản
đăng nhập tạm (đặc tả 2.1).

**Hệ quả.** Phase 1–4 chờ BlueID. Chủ sản phẩm đồng ý (2026-11-25) làm trước **thư viện media của Phase 5** (SDP,
STUN/ICE-lite/ICE-TCP, DTLS-SRTP, RTP/RTCP, TURN) tới phép thử vòng lặp (trình duyệt gửi và nhận lại chính luồng của
mình), vì chúng không cần đăng nhập. **Chưa** làm SFU hay báo hiệu có vé vào họp (cần BlueID).

**BlueChat cần BlueID cung cấp** (để chủ sản phẩm đối chiếu khi viết BlueID):

| # | Năng lực | Chi tiết BlueChat dùng |
|---|---|---|
| B1 | OIDC chuẩn | Discovery `/.well-known/openid-configuration`, JWKS; authorization code + PKCE (S256) cho SPA `bluechat-web`; refresh token; end-session. Access token JWT có `sub` (user id), `tid` (tenant id), `sid` (phiên), `aud` = `bluechat-api` |
| B2 | 2FA | Do BlueID làm hoàn toàn; BlueChat chỉ đọc claim `amr` nếu có |
| B3 | Danh bạ của tenant | API máy-với-máy (client credentials, scope ví dụ `directory.read`): liệt kê và tìm người dùng (id, tên hiển thị, email, ảnh, phòng ban, chức danh, quản lý, trạng thái, `is_guest`), nhóm người dùng và thành viên nhóm; trả theo trang và có **con trỏ thay đổi** (delta) để BlueChat cập nhật bản sao `org.users_cache` mà không tải lại toàn bộ |
| B4 | Khách mời | Tạo/mời tài khoản khách theo email vào một tenant; claim hoặc trường `is_guest` |
| B5 | Sự kiện tài khoản | Webhook ký HMAC (hoặc hàng đợi) khi tài khoản **bị khoá, bị xoá, đổi mật khẩu, thu hồi phiên** — BlueChat ngắt ngay WebSocket, phiên chat và rời mọi cuộc họp của người đó (đặc tả 2.1, bài học 8). Cần có id sự kiện để chống trùng và gửi lại khi BlueChat không nhận được |
| B6 | Kiểm phiên | Cách kiểm một `sid` còn hiệu lực (introspection hoặc sự kiện B5) để Gateway và SFU từ chối phiên đã bị thu hồi trước hạn token |
| B7 | Ứng dụng OAuth cho tích hợp | Đăng ký client của bên thứ ba (bot, ứng dụng tab, API công khai Phase 10) với phạm vi quyền và sự đồng ý của quản trị viên tenant |
| B8 | Đa tenant | Một BlueID phục vụ nhiều tenant; tên miền đăng nhập theo tenant (`bluechat.test`, `acme.test` khi dev) |
| B9 | Thẻ ngắn hạn cho tab SSO | Trao đổi token (token exchange hoặc `prompt=none`) để tab ứng dụng nhúng lấy thẻ BlueID ngắn hạn (Phase 10) |

---

## Z2. Hạ tầng mạng — **chưa quyết**

Chủ sản phẩm (2026-11-25): để sau, chưa quyết. Mọi thử nghiệm media chạy trên máy dev; không tự chọn hạ tầng thay chủ
sản phẩm. Những điều cần biết khi quyết (ghi lại để không quên):

Cần biết trước Phase 5–6:

- IP công khai cố định cho nút media; mở UDP dải cổng media (BlueChat dùng **một cổng UDP dùng chung** mỗi nút, ví dụ
  50000) và UDP/TCP 3478 cho STUN/TURN.
- **Một IP riêng** cho TURN-TLS trên TCP 443 (vì 443 của IP chính đã thuộc Caddy cho HTTPS). Máy chủ dùng chung của
  BlueStar (14.225.83.93) đã có Caddy chiếm 443 cho nhiều trang, nên TURN-TLS cần IP thứ hai hoặc máy khác.
- Băng thông ra và giá theo GB: cuộc họp 50 người có video lưới ≈ 50 × (0,3–1,5 Mbps) chiều xuống mỗi người, tức
  vài chục Mbps đến hàng trăm Mbps mỗi cuộc họp.


---

## Z3. DTLS 1.2 qua OpenSSL 3.x bằng P/Invoke — **xong**

**Bối cảnh.** .NET không có DTLS. Đặc tả cho phép gọi OpenSSL của hệ điều hành chỉ cho bắt tay DTLS và xuất khoá SRTP.

**Quyết định.** Thư viện `src/BlueChat.Media.Dtls`:

- `Native` — `LibraryImport` tới `libssl`/`libcrypto` 3.x. Tìm thư viện theo hệ điều hành
  (`libssl.so.3`, `libssl-3-x64.dll`, `libssl.3.dylib`), có thể chỉ thư mục bằng `BC_OPENSSL_DIR`.
  Kiểu C `long` dùng `CLong` (32 bit trên Windows).
- `DtlsCertificate` — chứng chỉ ECDSA P-256 tự ký **sinh bằng .NET**; OpenSSL chỉ nhận DER. Fingerprint SHA-256
  dạng `AB:CD:…` (RFC 8122).
- `DtlsContext` — `SSL_CTX` với DTLS ≥ 1.2, `use_srtp` = `SRTP_AEAD_AES_128_GCM:SRTP_AES128_CM_SHA1_80`, bắt buộc
  có chứng chỉ của trình duyệt.
- `DtlsSession` — **BIO bộ nhớ**: không I/O, nhận datagram vào, trả datagram ra, nên unit test và fuzz không cần
  mạng. MTU đặt tay (`SSL_OP_NO_QUERY_MTU`); bản ghi được gom vào datagram ≤ MTU, không cắt đôi bản ghi. Sau khi bắt tay,
  **kiểm fingerprint chứng chỉ trình duyệt khớp SDP trước khi trả khoá**; sai thì phiên hỏng (`fingerprint_mismatch`).
  Xuất `EXTRACTOR-dtls_srtp` (RFC 5705/5764) và tách khoá/salt của mỗi bên.

**Bằng chứng.**

1. Unit test `tests/BlueChat.Media.Dtls.Tests` (6 phép thử) xanh trên Windows (OpenSSL 3.2.3 của Git for Windows)
   và Linux (`mcr.microsoft.com/dotnet/sdk:10.0`, OpenSSL 3.0.13): bắt tay hai phiên, khoá đối xứng chéo, sai fingerprint
   thì không có khoá, gói rác không làm phiên kết nối, gom bản ghi đúng.
2. **Trình duyệt thật** (`tools/DtlsProbe` + `tests/phase0/dtls-browsers.mjs`): trang tạo `RTCPeerConnection` có micro
   giả, máy chủ trả SDP ICE-lite một cổng UDP, trả lời STUN (kiểm USERNAME và MESSAGE-INTEGRITY), bắt tay DTLS làm
   server (`a=setup:passive`), rồi **mở một gói SRTP thật của trình duyệt** bằng khoá phiên dẫn xuất từ khoá vừa xuất
   (KDF RFC 3711 §4.3 + AES-GCM RFC 7714). Thẻ xác thực đúng tức là khoá đúng.

| Máy chủ | Trình duyệt | Kết quả |
|---|---|---|
| Windows, OpenSSL 3.2.3 | Chromium 159.0.8154.6 | connected, DTLSv1.2, ECDHE-ECDSA-AES128-GCM-SHA256, AEAD_AES_128_GCM, gói SRTP mở được |
| Windows, OpenSSL 3.2.3 | Firefox 159.0 | như trên |
| Linux (container), OpenSSL 3.0.13 | Chromium 159.0.8154.6 | như trên |
| Linux (container), OpenSSL 3.0.13 | Firefox 159.0 | như trên |

Chạy lại: `dotnet run --project tools/DtlsProbe` rồi `cd tests/phase0 && npm install && npm run dtls`.

**Hệ quả.**

- **OpenSSL tối thiểu: 3.0** (đã thử 3.0.13 và 3.2.3). Image chạy thật dựa trên Debian/Ubuntu có `libssl3`.
- Bản cài Windows Server: cần kèm `libssl-3-x64.dll`/`libcrypto-3-x64.dll` (bản dựng chính thức của OpenSSL 3.x),
  vì Windows không có sẵn OpenSSL. Ghi ở `docs/09`.
- DTLS 1.3: chưa bật; xem lại khi Chrome/Firefox và OpenSSL đều ổn định (đặc tả 6.3).
- Gói `AES_CM_128_HMAC_SHA1_80` chưa thử với trình duyệt (cả hai đều chọn GCM); sẽ ép profile này trong phép thử ở Phase 5.

---

## Z4. Khung kiểm thử media

`tests/interop/compose.yml` dựng mạng riêng `10.78.0.0/24` (BlueMail dùng `10.77.0.0/24`): `probe` (10.78.0.10) và
`tools` (10.78.0.20, image `bluechat/interop-tools:dev`, chỉ dùng cho kiểm thử). `tools` có Pion (Go), aiortc
(Python), coturn + `turnutils`, tshark, `tc netem`. Kiểm: `sh tests/interop/run-z4.sh`.

Trình duyệt thật: Playwright (Chromium, Firefox) với thiết bị giả `--use-fake-device-for-media-stream`,
`media.navigator.streams.fake`. Video giả từ tệp: `--use-file-for-fake-video-capture=<y4m>` (Phase 6).

Kết quả: xem mục "Z4 — kết quả" cuối tệp.

---

## Z5. Kho S3: **Garage v2.5.0**

Dùng cùng lựa chọn với BlueMail (docs/00 Z3 của BlueMail, 2026-11-23): MinIO đã lưu trữ (archived), Garage phát hành
đều. Chạy **nguyên bản, dịch vụ riêng, không sửa mã**, nói chuyện qua API S3 chuẩn, nên nghĩa vụ AGPL-3.0 không phát
sinh với BlueChat. Đổi sang SeaweedFS hay S3 đám mây chỉ là đổi cấu hình. Bucket: `bc-files`, `bc-recordings`,
`bc-exports`, `bc-backups`.

---

## Z6. Giới hạn của Teams

Đọc trang "Limits and specifications for Microsoft Teams"
(learn.microsoft.com/en-us/microsoftteams/limits-specifications-teams, cập nhật 2026-11-09) ngày 2026-11-25.
Bảng giới hạn mục tiêu ở `doi-chieu-teams.md`, mục "Giới hạn". Nguyên tắc: BlueChat **bằng hoặc hơn** Teams, trừ khi
số đo ở Phase 12 cho thấy hạ tầng không gánh nổi (ghi lại ở đó).

---

## Z7. Ước lượng tải cho một máy 8 nhân / 16 GB (ngưỡng ban đầu, **chưa đo**)

Đây là ước lượng để đặt ngưỡng cho phép thử tải. Phase 6 (SFU) và Phase 12 thay bằng số đo thật.

| Đại lượng | Ước lượng | Cơ sở |
|---|---|---|
| Kết nối WebSocket rảnh mỗi nút Gateway | 50 000 | SignalR ≈ 20–40 KB/kết nối → 1–2 GB RAM; nhịp tim 15 s |
| Tin nhắn ghi vào CSDL | 2 000 tin/s duy trì | Mỗi tin: 1 câu `UPDATE … next_seq` + 1 `INSERT` trong một giao dịch, PostgreSQL trên SSD |
| Sự kiện phát tới phiên | 50 000 sự kiện/s | Valkey pub/sub + SignalR, gói nhỏ |
| Luồng media chuyển tiếp mỗi nút SFU | 2 000 luồng ra (≈ 1,5 Gbps) | Không giải mã; chi phí chính là SRTP giải/mã hoá AES-GCM (có AES-NI) và syscall UDP |
| Người trong một phòng (một nút) | 300 tương tác | Bằng mức thấp của Teams (Z6); 1 000 cần ghép tầng (Phase 12) |
| Độ trễ thêm của SFU | < 5 ms p99 | Xử lý gói không cấp phát bộ nhớ |

---

## Z8. Khung bảng đối chiếu

`doi-chieu-teams.md`: mỗi chức năng ở mục 8–10 của đặc tả có một mã `T-xxx`. Đánh số theo nhóm: 8.1 chat
`T-001…`, 8.2 nhóm `T-050…`, 8.3 kênh `T-070…`, 8.4 hoạt động `T-090…`, 8.5 hiện diện `T-100…`, 8.6 tìm kiếm `T-110…`,
8.7 tệp `T-120…`, 9.1 cuộc gọi `T-130…`, 9.2 lịch họp `T-140…`, 9.3 trong họp `T-150…`, 9.4 webinar `T-185…`, phím tắt
`T-200`, 10.1 ứng dụng `T-210…`, 10.2 quản trị `T-230…`, 10.3 tuân thủ `T-250…`, mục 11 ngoài phạm vi `T-300…`.

---

## Z9. Đối chiếu Teams — đối chiếu tay trên Teams desktop

**Quyết định (chủ sản phẩm, 2026-11-25).** Không dùng biến môi trường `BC_TEAMS_TEST_USERS` nữa (bỏ phần Playwright
đăng nhập Teams web của đặc tả 3.1). Chủ sản phẩm mở sẵn ứng dụng Teams desktop đã đăng nhập; Claude Code xem và thao
tác qua điều khiển máy tính (agent-desktop). Chủ sản phẩm cho dùng tài khoản đang đăng nhập (không phải tenant thử
nghiệm riêng — chủ sản phẩm đã chấp nhận) với phạm vi thao tác **chỉ trong chat "Just me"** (chat với chính mình).

Quy tắc:

- Chỉ thao tác trong chat "Just me". Không nhắn, không mời ai, không mở chat hay nhóm khác.
- Chỉ quan sát như người dùng: không đọc mã, không chặn lưu lượng mạng, không gọi API nội bộ của Teams.
- Ảnh chụp đưa vào kho mã cắt chỉ còn phần chức năng đang so; không để lộ tên, tin nhắn, thông báo của người khác.
  Ảnh chụp toàn cửa sổ chỉ nằm ở thư mục tạm và xoá sau khi dùng.
- Mỗi dòng đối chiếu ghi "đối chiếu tay trên Teams desktop, tài khoản do chủ sản phẩm cấp, chat Just me, ngày …";
  chức năng chưa xem được trên Teams thật ghi "chỉ đối chiếu theo tài liệu".
- Chức năng cần nhiều người (gọi, chat nhóm, phòng chờ, vai trò): ghi kịch bản từng bước và báo chủ sản phẩm; chủ sản
  phẩm đóng vai người thứ hai.
- Không đối chiếu được (bị chặn thao tác, sai tài khoản…) thì báo lý do cụ thể, không đoán hành vi của Teams.

Kiểm tài khoản (2026-11-25): cửa sổ Teams desktop mở, đã đăng nhập; chủ sản phẩm xác nhận dùng tài khoản này.

---

## D1. Tự viết STUN tối thiểu trong nguyên mẫu

`tools/DtlsProbe/MiniStun.cs` là phần STUN tối thiểu chỉ để nguyên mẫu Z3 chạy (Binding request → success,
MESSAGE-INTEGRITY, FINGERPRINT). Đây là công cụ Phase 0, không bàn giao; thư viện thật là `BlueChat.Media.Stun`
(Phase 5) có vector RFC 5769 và fuzz.

---

## Z4 — kết quả (2026-11-25, `sh tests/interop/run-z4.sh`)

| Công cụ | Phiên bản | Kết quả với nguyên mẫu OpenSSL (Linux, 3.0.13) |
|---|---|---|
| Pion | `github.com/pion/webrtc/v4` v4.2.29 | connected; DTLSv1.2 ECDHE-ECDSA-AES128-GCM-SHA256; AEAD_AES_128_GCM; gói SRTP mở được. Cũng connected dưới netem mất 10 % + trễ 150 ms |
| aiortc | 1.16.0 | như Pion, cả mạng sạch lẫn netem |
| tshark | 4.0.17 | 20 gói STUN/DTLS (hai chiều) và mọi gói do máy chủ gửi: **0** gói hỏng. Bắt tay đầy đủ hai chiều có chứng chỉ: máy chủ gửi 2/11/12/13/14, máy khách 1 rồi 11/16/15 |
| coturn / turnutils | 4.6.1 | `turnutils_uclient` với coturn: 0 % mất — sẵn sàng làm đối chứng cho `BlueChat.Turn` ở Phase 5 |
| tc netem | iproute2 6.1.0 | chạy được trong container `tools` (`cap_add: NET_ADMIN`) |

Ghi chú: thân gói SRTP/SRTCP đã mã hoá nên tshark không đánh giá được; nó gắn cờ "malformed" cho phần mã hoá RTCP
của Pion. Phép kiểm vì thế chỉ chấm STUN/DTLS (hai chiều) và gói do máy chủ của mình gửi. Phase 5 sẽ cho tshark khoá
SRTP để chấm cả RTP/RTCP sau giải mã.

Kết luận Z4: khung kiểm thử media chạy; nguyên mẫu DTLS bắt tay được với **bốn** cài đặt độc lập (Chrome, Firefox,
Pion, aiortc).
