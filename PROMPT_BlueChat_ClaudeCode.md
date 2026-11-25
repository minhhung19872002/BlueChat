# **BlueChat** — NỀN TẢNG CHAT, CUỘC GỌI VÀ HỌP TRỰC TUYẾN CHO DOANH NGHIỆP (TƯƠNG ĐƯƠNG MICROSOFT TEAMS)

> Đặc tả để Claude Code xây dựng BlueChat từ đầu, **kể cả máy chủ media cho cuộc gọi và họp**. Đọc hết trước khi
> viết dòng mã đầu tiên. Làm tuần tự theo mục 14, không nhảy phase.
>
> **Mục tiêu chức năng: ngang Microsoft Teams** (phần chat, nhóm/kênh, cuộc gọi, họp, webinar, quản trị, tuân thủ).
> Sau **mỗi** chức năng làm xong phải đối chiếu với Teams theo quy trình ở mục 3 và ghi vào bảng đối chiếu.

---

## 0. VAI TRÒ, MỤC TIÊU, NGUYÊN TẮC

Bạn là kỹ sư chính xây dựng **BlueChat** — nền tảng làm việc nhóm gồm chat, nhóm và kênh, cuộc gọi thoại/video, họp
trực tuyến, webinar, bán dạng dịch vụ đám mây nhiều khách hàng **và** bản cài riêng tại máy chủ của khách. BlueChat là
sản phẩm đứng riêng; đăng nhập dùng chung **BlueID** với các sản phẩm khác của bộ Blue (mục 2.1).

**Nguyên tắc không thương lượng:**

1. **Giống Teams về chức năng, không giống về nhận diện.** Được làm đủ chức năng và hành vi như Teams. **Không** được
   chép tên gọi thương hiệu (Teams, Loop, Copilot, Fluent…), biểu tượng, hình ảnh, âm thanh, phông, bảng màu hay bố
   cục giao diện y hệt; **không** dịch ngược, không đọc mã nguồn hay gói tin của ứng dụng Teams. Giao diện BlueChat có
   bộ nhận diện riêng (mục 7.1). Nguồn đối chiếu duy nhất là **tài liệu công khai của Microsoft** và việc dùng thử
   Teams như một người dùng (mục 3).
2. **Tự viết lõi.** Giao thức media (STUN, ICE, TURN, SRTP, RTP/RTCP, SDP), bộ chuyển tiếp media (SFU), bộ ghi hình, máy
   khách media phía trình duyệt, giao thức đẩy thông báo, máy khách SMTP, bộ hiển thị thẻ: **tự viết**. Ranh giới chi
   tiết ở mục 1.1.
3. **Tự viết giao thức, không tự viết mật mã.** Mọi phép tính mật mã dùng thư viện chuẩn của .NET
   (`System.Security.Cryptography`, `SslStream`). Riêng **DTLS** — .NET không có — gọi **OpenSSL của hệ điều hành** qua
   P/Invoke (chính thư viện .NET dùng bên dưới cho TLS trên Linux). Không tự cài đặt thuật toán hay giao thức bắt tay
   mật mã nào.
4. **Không stub, không mock trong mã bàn giao, không TODO.** Chức năng nào chưa làm được thì dừng lại hỏi.
5. **Không bao giờ mất tin nhắn, không bao giờ trùng tin nhắn, không bao giờ sai thứ tự trong một cuộc trò chuyện.**
6. **Quốc tế ngay từ đầu** (mục 0.2). `tenant_id` có mặt trong mọi bảng nghiệp vụ ngay từ đầu.
7. **Bảo mật mặc định đóng**: nội dung người dùng luôn được lọc trước khi hiển thị; máy chủ TURN không bao giờ chuyển
   tiếp tới địa chỉ nội bộ; người lạ vào họp phải qua phòng chờ trừ khi người tổ chức cho phép khác.
8. **Mọi đầu vào từ mạng** (WebSocket, SDP, gói STUN/RTP/RTCP, tệp tải lên) có giới hạn độ dài, độ sâu, thời gian và
   được thử bằng fuzzing.

### 0.1. Định danh sản phẩm

| Hạng mục | Giá trị |
|---|---|
| Tên sản phẩm | **BlueChat** |
| Solution .NET | `BlueChat.slnx`, namespace gốc `BlueChat.*` |
| Đăng nhập | **BlueID** (dịch vụ riêng, OIDC) |
| Docker compose project | `bluechat` |
| CSDL PostgreSQL | `bluechat` (schema: `tenant`, `org`, `chat`, `team`, `meeting`, `media`, `files`, `apps`, `admin`, `compliance`, `audit`, `hangfire`) |
| Tiền tố biến môi trường | `BC_` |
| Bucket lưu trữ | `bc-files`, `bc-recordings`, `bc-exports`, `bc-backups` |
| Tên miền dev | `bluechat.test`, `acme.test` (hai khách) |

### 0.2. Quốc tế hoá

- Giao diện tiếng Anh (gốc, dự phòng) + tiếng Việt có sẵn; thêm ngôn ngữ = thêm tệp dịch. ICU cho số nhiều và chèn
  biến. Phép thử: đủ khoá ở mọi ngôn ngữ, không chuỗi hiển thị nào nằm ngoài tệp dịch.
- Hỗ trợ chữ phải-sang-trái (`dir="rtl"`, thuộc tính logic CSS); tin nhắn tự nhận hướng (`dir="auto"`).
- Ngày giờ, số theo ngôn ngữ và múi giờ (IANA) của người dùng; 12/24 giờ; ngày đầu tuần. Máy chủ chạy
  `InvariantCulture`.
- Lỗi API có `code` ổn định + câu theo ngôn ngữ người gọi.
- Tìm kiếm mọi ngôn ngữ: bỏ dấu chữ Latin, gập hoa/thường Unicode, bigram cho chữ không khoảng trắng (CJK, Thái).
- Unicode NFC; cắt chuỗi theo cụm chữ (grapheme) — không cắt đôi emoji.
- Vùng lưu dữ liệu theo tenant; công cụ cho GDPR và luật tương tự (xuất, xoá dữ liệu cá nhân trừ khi giữ pháp lý).
- WCAG 2.1 AA.

---

## 1. STACK

| Thành phần | Công nghệ | Ghi chú |
|---|---|---|
| Backend | **.NET 10**, ASP.NET Core, Clean Architecture + CQRS (MediatR **12.5.0**) | Không nâng MediatR ≥ 13 (thương mại) |
| Máy chủ media, TURN | .NET 10, `System.Net.Sockets`, `System.IO.Pipelines`, xử lý gói UDP không cấp phát bộ nhớ trên đường nóng (`ArrayPool`, `Span<T>`) | Tự viết (mục 6) |
| DTLS | **OpenSSL 3.x của hệ điều hành** qua P/Invoke | Chỉ cho bắt tay DTLS và xuất khoá SRTP |
| ORM | EF Core 10 + Npgsql, snake_case | |
| CSDL | **PostgreSQL 16** | `unaccent`, `pg_trgm` |
| Lưu trữ | Dịch vụ tương thích S3 (chốt ở Phase 0: MinIO / Garage / SeaweedFS theo giấy phép) | |
| Bộ đệm, trạng thái online, backplane | **Valkey** (BSD) | Không dùng Redis ≥ 7.4 |
| Thời gian thực | **SignalR** (WebSocket) + backplane Valkey | |
| Việc nền | Hangfire (PostgreSQL) | |
| Đăng nhập | BlueID (OIDC) | |
| Lọc HTML | HtmlSanitizer (máy chủ), DOMPurify (trình duyệt) | Thư viện bảo mật, không tự viết |
| Frontend | **React 18 + TypeScript + Vite**, Ant Design 5, TanStack Query, Zustand, react-i18next | |
| Soạn tin | TipTap (lõi MIT) | |
| Media phía trình duyệt | **WebRTC có sẵn của trình duyệt**; thư viện điều khiển tự viết `@bluechat/media-client` (TypeScript) | Bộ mã hoá Opus/VP8/VP9/H.264/AV1 là của trình duyệt — máy chủ không giải mã |
| Log | Serilog (JSON có cấu trúc) | |
| Kiểm thử | xUnit, FluentAssertions **7.2.0**, Testcontainers, SharpFuzz, Vitest, Playwright, k6 | |
| Triển khai | Docker Compose, Caddy (HTTPS) | Cổng UDP media và TURN đi thẳng vào dịch vụ, không qua Caddy |

### 1.1. Ranh giới "tự phát triển"

| Tự viết | Dùng (nền tảng hoặc thư viện bảo mật/hạ tầng) |
|---|---|
| SDP, STUN, ICE-lite, ICE-TCP, TURN, SRTP/SRTCP, RTP/RTCP, các phần mở rộng tiêu đề RTP, bộ đọc payload VP8/VP9/H.264/AV1, ước lượng băng thông, SFU, ghép tầng SFU | .NET (runtime, BCL, Kestrel, SignalR) |
| Bộ đóng gói WebM/Matroska cho bản ghi, trình phát bản ghi nhiều luồng | OpenSSL (chỉ DTLS) |
| `@bluechat/media-client` (điều khiển `RTCPeerConnection`, simulcast, đăng ký luồng, đổi thiết bị, kết nối lại, gửi số liệu chất lượng) | PostgreSQL, S3, Valkey, Hangfire, EF Core, MediatR, OpenIddict (qua BlueID) |
| Web Push (RFC 8030, 8291, 8292), máy khách SMTP tối thiểu, bộ dựng thư tối thiểu, iCalendar (RFC 5545) | HtmlSanitizer, DOMPurify |
| Bộ hiển thị thẻ (theo lược đồ Adaptive Cards — đặc tả mở), bộ xem trước liên kết, tìm kiếm | React, Ant Design, TipTap, pdf.js |

Thêm thư viện nào khác vào đường xử lý chính thì ghi lý do vào `docs/00-quyet-dinh-ky-thuat.md` và hỏi chủ sản phẩm.
Phần mềm của người khác (Pion, aiortc, coturn, Wireshark…) **chỉ** được có trong kiểm thử làm đối chứng (mục 12).

---

## 2. KIẾN TRÚC

```
 Trình duyệt / PWA / ứng dụng desktop bọc web
   │ HTTPS REST        │ WebSocket (SignalR)        │ WebSocket báo hiệu media     │ UDP/TCP media (SRTP)
   ▼                   ▼                            ▼                              ▼
┌──────────┐   ┌───────────────┐          ┌──────────────────────────────────────────────┐
│ Api      │   │ Gateway       │          │ Media (SFU) — nhiều nút                       │
│ REST     │   │ đẩy sự kiện,  │          │ báo hiệu, ICE-lite, DTLS(OpenSSL)-SRTP,       │
│          │   │ online, gõ    │          │ RTP/RTCP, simulcast, BWE, ghép tầng           │
└────┬─────┘   └──────┬────────┘          └──────────────┬───────────────────────────────┘
     │                │                                  │ chuyển luồng cho Recorder
     ▼                ▼                                  ▼
 PostgreSQL · Valkey (sự kiện, online, sổ đăng ký nút media) · S3        ┌─────────────┐
                                                                        │ Recorder     │
 Worker (Hangfire): thông báo đẩy, thư tóm tắt, lưu giữ, xuất dữ liệu   │ WebM/MKA     │
 Turn: STUN/TURN UDP 3478, TCP/TLS 443 (IP riêng)                       └─────────────┘
 BlueID: đăng nhập, người dùng, nhóm, tenant (dịch vụ riêng)
```

Mỗi khối là một tiến trình (container) riêng. Gateway và Media mở rộng ngang; Valkey làm backplane và sổ đăng ký.

### 2.1. Phụ thuộc BlueID

BlueChat **không tự quản người dùng**. BlueID cung cấp: đăng nhập OIDC, 2FA, danh bạ người dùng/nhóm của tenant, khách
mời (guest), ứng dụng OAuth cho tích hợp, sự kiện "tài khoản bị khoá/xoá" (BlueChat ngắt mọi phiên và cuộc gọi đang
mở của người đó ngay). **Phase 0 kiểm BlueID đã sẵn sàng chưa; chưa có thì dừng lại báo chủ sản phẩm**, không tự dựng
bản đăng nhập tạm trong BlueChat.

### 2.2. Mô hình tin nhắn

- Mọi nơi có tin nhắn là một **conversation**: chat 1-1, chat nhóm, chat của cuộc họp, chat với chính mình, mỗi bài
  đăng (post) trong kênh là gốc của một luồng trả lời.
- **Thứ tự**: mỗi conversation có dãy số `seq` tăng dần, cấp nguyên tử ở CSDL (`UNIQUE(conversation_id, seq)`).
  Thứ tự hiển thị theo `seq`, **không bao giờ** theo đồng hồ máy khách.
- **Không trùng**: máy khách sinh `client_message_id` (UUID) cho mỗi tin; `UNIQUE(sender_id, client_message_id)`; gửi
  lại sau mất mạng trả về đúng tin đã tạo.
- **Đồng bộ**: máy khách giữ `seq` cuối cùng của mỗi conversation và con trỏ sự kiện của chính mình
  (`user_event_seq`); kết nối lại thì xin phần còn thiếu — không tải lại toàn bộ.
- **Phân phối**: ghi CSDL xong (commit) mới phát sự kiện qua Valkey → Gateway → các phiên đang mở của thành viên;
  thành viên không trực tuyến nhận thông báo đẩy qua Worker. Kênh đông người (hàng nghìn thành viên): chỉ gửi nội
  dung đầy đủ cho phiên đang mở kênh đó, các phiên khác chỉ nhận "kênh có hoạt động mới".
- **Đã đọc**: mỗi thành viên có `last_read_seq`; số chưa đọc **tính từ nguồn** (đếm tin có `seq > last_read_seq` không
  phải của mình), không cộng dồn tay.

### 2.3. Mô hình media

- Trình duyệt mở **một** `RTCPeerConnection` tới nút SFU (BUNDLE + rtcp-mux), gửi lên luồng của mình (video gửi
  **simulcast 3 lớp**), nhận về các luồng mình đăng ký.
- Máy chủ chạy **ICE-lite**: ứng viên host trên IP công khai, **một cổng UDP dùng chung** cho mọi phiên (phân biệt theo
  ufrag/địa chỉ), ứng viên **ICE-TCP** thụ động (RFC 6544) dự phòng, và **TURN** (UDP 3478, TLS 443) cho mạng chặn UDP.
- SFU **không giải mã** media: chỉ đọc tiêu đề RTP và bộ mô tả payload để biết khung khoá, lớp thời gian/không gian,
  rồi viết lại số thứ tự/dấu thời gian khi đổi lớp.
- Mỗi cuộc họp ở trên **một nút** SFU (bản đầu). Phase 12 thêm **ghép tầng** giữa các nút cho cuộc họp lớn và webinar.
- Âm thanh: chuyển tiếp mọi luồng khi ≤ 30 người nói được; lớn hơn thì chỉ chuyển **N luồng to nhất** theo phần mở rộng
  mức âm (RFC 6464), có làm mượt chống nhảy.
- Video: máy khách khai báo ô nào đang hiển thị và kích thước → SFU chọn lớp simulcast phù hợp cho từng ô dựa trên ước
  lượng băng thông chiều xuống của người nhận.

---

## 3. QUY TRÌNH ĐỐI CHIẾU VỚI MICROSOFT TEAMS (BẮT BUỘC SAU MỖI CHỨC NĂNG)

Tạo và duy trì `docs/doi-chieu-teams.md` — bảng đối chiếu từng chức năng. Mỗi chức năng trong mục 8–10 có một mã
`T-xxx`. Mẫu dòng:

| Mã | Chức năng | Hành vi của Teams (tóm tắt) | Nguồn (URL + ngày đọc) | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|
| T-021 | Sửa tin nhắn | … | learn.microsoft.com/… (2026-xx-xx) | Xong | Không | phép thử `EditMessageTests`, ảnh chụp |

Trạng thái: `Chưa làm` / `Đang làm` / `Xong` / `Khác có chủ ý` (ghi lý do) / `Ngoài phạm vi` (theo mục 11).

**Khi làm xong một chức năng, trước khi đánh dấu Xong:**

1. **Đọc tài liệu chính thức** của Microsoft về đúng chức năng ấy (support.microsoft.com, learn.microsoft.com — trang
   hướng dẫn người dùng **và** trang cho quản trị viên, kể cả trang "Limits and specifications for Microsoft Teams").
   Ghi URL và ngày đọc. **Không dựa vào trí nhớ** về hành vi hay con số giới hạn của Teams — đọc trang hiện hành.
2. **Liệt kê mọi hành vi** tài liệu mô tả: các bước người dùng làm, mọi tuỳ chọn, ai được làm (vai trò), giới hạn số
   lượng, chính sách quản trị nào bật/tắt nó, thông báo nào sinh ra, hiển thị trên điện thoại khác gì.
3. **So từng dòng** với BlueChat. Mỗi chỗ thiếu: hoặc làm cho đủ, hoặc ghi `Khác có chủ ý` kèm lý do, hoặc hỏi chủ sản
   phẩm. Giới hạn số lượng: BlueChat **bằng hoặc hơn** Teams, trừ khi Phase 0 đo được hạ tầng không gánh nổi.
4. **Dùng thử trên Teams thật** bằng tài khoản thử nghiệm chủ sản phẩm cung cấp (mục 3.1): làm đúng kịch bản của chức
   năng ấy trên Teams web (`teams.microsoft.com`) bằng Playwright, ghi các bước, ảnh chụp và hành vi quan sát được, rồi
   chạy cùng kịch bản trên BlueChat và so. Không đăng nhập được (thiếu biến, MFA chặn, mạng chặn) thì ghi "chỉ đối chiếu
   theo tài liệu" kèm lý do, **không** đoán hành vi của Teams.
5. Viết **phép thử chấp nhận** cho chính các hành vi đã liệt kê, chạy xanh, rồi mới đánh dấu `Xong`.
6. Cuối mỗi phase: rà lại toàn bộ dòng của phase trong bảng; phase chỉ được coi là xong khi không còn dòng `Chưa làm`
   hay `Đang làm` ngoài những dòng đã được chủ sản phẩm chấp nhận để sau.

### 3.1. Tài khoản Teams thử nghiệm

- Thông tin đăng nhập **chỉ** đọc từ biến môi trường do chủ sản phẩm tự nhập trong cài đặt môi trường, không bao giờ nằm
  trong kho mã, tệp `.env` được commit, nhật ký, ảnh chụp hay bảng đối chiếu:
  - `BC_TEAMS_TEST_USERS` — JSON danh sách tài khoản (ít nhất **3**: một người tổ chức, hai người tham gia — đủ để thử
    chat nhóm, gọi, phòng chờ, vai trò), mỗi tài khoản có `email`, `password`, và `totp_secret` nếu tài khoản bật xác thực
    hai lớp bằng ứng dụng mã (tự sinh mã TOTP theo RFC 6238 bằng mật mã .NET để đăng nhập).
  - `BC_TEAMS_TEST_TENANT` — tên miền tenant thử nghiệm (để ghi vào bảng đối chiếu tenant nào đã dùng).
- Mã so sánh nằm ở `tests/TeamsParity/` (Playwright), **tách hẳn** khỏi mã sản phẩm và khỏi bộ phép thử chạy trong CI.
  Chạy tay theo từng chức năng, không chạy định kỳ.
- Đăng nhập một lần, lưu phiên của Playwright (`storageState`) ra thư mục ngoài kho mã (trong `.gitignore`), dùng lại
  cho các lượt sau để không đăng nhập liên tục (tránh bị Microsoft khoá vì nghi tự động).
- Chỉ dùng tài khoản trong **tenant thử nghiệm riêng**, không bao giờ là tenant làm việc thật của công ty. Dọn những gì đã
  tạo (nhóm, chat, cuộc họp) sau mỗi lượt so.
- Chỉ quan sát giao diện và hành vi như một người dùng; **không** chặn bắt hay đọc lưu lượng mạng, không đọc mã JavaScript
  của Teams, không gọi API nội bộ của Teams (nguyên tắc 1).
- Phần họp có âm thanh/hình ảnh cần UDP và mạng ra ngoài rộng; nếu môi trường chạy Claude Code chặn thì ghi rõ và để chủ
  sản phẩm tự so phần media trên máy của mình theo kịch bản đã ghi.
- Không đăng nhập được → không đoán, ghi "chỉ đối chiếu theo tài liệu" và báo chủ sản phẩm.

---

## 4. PHASE 0 — HẠ TẦNG KIỂM THỬ VÀ QUYẾT ĐỊNH

| # | Việc | Kết quả |
|---|---|---|
| Z1 | BlueID có sẵn chưa (OIDC, danh bạ, khách mời, sự kiện khoá tài khoản) | Chưa có → dừng, báo chủ sản phẩm |
| Z2 | Hạ tầng định thuê: IP công khai cố định, mở được UDP (dải cổng media, 3478), TCP 443 trên **IP riêng** cho TURN-TLS, băng thông và giá băng thông ra | Ghi `docs/00`; không đạt → báo ngay |
| Z3 | Gọi OpenSSL 3.x DTLS qua P/Invoke trên Linux (và Windows Server nếu bán bản cài Windows): bắt tay DTLS 1.2 với Chrome và Firefox thật, xuất khoá `EXTRACTOR-dtls_srtp` (RFC 5705/5764) | Nguyên mẫu chạy; ghi phiên bản OpenSSL tối thiểu |
| Z4 | Khung kiểm thử media: Playwright với Chrome/Firefox dùng thiết bị giả (`--use-fake-device-for-media-stream`, `--use-file-for-fake-video-capture` / `media.navigator.streams.fake`), **Pion** (Go) và **aiortc** (Python) làm máy khách WebRTC độc lập, **coturn** làm đối chứng TURN, `tshark` kiểm định dạng gói, `tc netem` giả lập mất gói/trễ/giới hạn băng thông | Lệnh ghi `docs/06`; không thứ nào nằm trong image chạy thật |
| Z5 | Chọn kho S3 theo giấy phép | `docs/00` |
| Z6 | Đọc trang giới hạn của Teams, ghi bảng giới hạn mục tiêu (thành viên nhóm, số kênh, người trong chat nhóm, người trong họp tương tác, webinar, town hall, cỡ tệp…) | Bảng trong `docs/doi-chieu-teams.md` |
| Z7 | Ước lượng tải mục tiêu cho một máy 8 nhân/16 GB: kết nối WebSocket, tin nhắn/giây, số luồng media chuyển tiếp | `docs/00`, làm ngưỡng phép thử tải |
| Z8 | Dựng khung `docs/doi-chieu-teams.md` với toàn bộ mã `T-xxx` của mục 8–10 | Bảng có đủ dòng, trạng thái `Chưa làm` |
| Z9 | Kiểm tài khoản Teams thử nghiệm (mục 3.1): đọc biến, đăng nhập bằng Playwright cả 3 tài khoản, lưu phiên, gửi một tin chat giữa hai tài khoản rồi dọn | Ghi kết quả vào `docs/00` (không ghi thông tin đăng nhập); hỏng thì báo chủ sản phẩm lý do cụ thể |

---

## 5. MÔ HÌNH DỮ LIỆU POSTGRESQL

Mọi bảng: `id uuid`, `tenant_id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `deleted_at` (xoá mềm). Chỉ
mục duy nhất có `WHERE deleted_at IS NULL`. Luật "chỉ một …" là ràng buộc duy nhất ở CSDL. Bảng tin nhắn **phân vùng
theo `tenant_id`**.

```
tenant.tenants              -- name, plan, status, default_locale, default_time_zone, data_region, limits(jsonb)
org.users_cache             -- bản sao chỉ-đọc từ BlueID: user_id, display_name, email, avatar_key, status,
                            -- is_guest, department, job_title, manager_id, updated_from_blueid_at
org.user_settings           -- user_id, settings(jsonb): theme, density, read_receipts_enabled, notification prefs,
                            -- quiet_hours, language, time_zone, hour_cycle, chat_layout, keyboard
org.tags                    -- team_id, name; tag_members(tag_id, user_id)   (nhãn để @nhắc một nhóm người)

chat.conversations          -- type(ONE_ON_ONE|GROUP|SELF|MEETING|CHANNEL_THREAD), title, avatar_key, team_id null,
                            -- channel_id null, meeting_id null, root_message_id null, next_seq, last_message_at,
                            -- is_federated
                            -- UNIQUE cho ONE_ON_ONE theo cặp người dùng đã sắp xếp
chat.conversation_members   -- conversation_id, user_id, role(OWNER|MEMBER|GUEST), joined_at, history_from_seq,
                            -- last_read_seq, last_delivered_seq, muted_until, hidden, pinned_order,
                            -- notification_level, left_at
chat.messages               -- conversation_id, seq, sender_id, client_message_id, kind(TEXT|SYSTEM|CARD|CALL_EVENT|
                            -- MEETING_EVENT|POLL), body_html (đã lọc), body_text, mentions(jsonb), importance(NORMAL|
                            -- IMPORTANT|URGENT), subject null, reply_to_message_id null, forwarded_from(jsonb) null,
                            -- edited_at, deleted_at, scheduled_for null, is_announcement, announcement_banner(jsonb)
                            -- UNIQUE(conversation_id, seq), UNIQUE(sender_id, client_message_id)
chat.message_versions       -- message_id, version, body_html, edited_at, edited_by
chat.reactions              -- message_id, user_id, emoji   UNIQUE(message_id, user_id, emoji)
chat.attachments            -- message_id, file_id
chat.link_previews          -- url_hash, url, title, description, image_key, site_name, fetched_at, status
chat.pins                   -- conversation_id, message_id, pinned_by   UNIQUE(conversation_id, message_id)
chat.saved_messages         -- user_id, message_id   UNIQUE(user_id, message_id)
chat.urgent_notifications   -- message_id, recipient_id, next_at, remaining   (nhắc lại tin khẩn)
chat.polls / poll_options / poll_votes  UNIQUE(poll_id, user_id) khi một lựa chọn
chat.scheduled_messages     -- conversation_id, sender_id, draft(jsonb), send_at, status
chat.drafts                 -- user_id, conversation_id, body(jsonb)   UNIQUE(user_id, conversation_id)
chat.sections               -- user_id, name, sort_order; section_items(section_id, conversation_id|channel_id)

team.teams                  -- name, description, visibility(PUBLIC|PRIVATE|ORG_WIDE), join_code, avatar_key,
                            -- archived_at, settings(jsonb: member/guest permissions, mentions, fun stuff), template_id
team.team_members           -- team_id, user_id, role(OWNER|MEMBER|GUEST)   UNIQUE(team_id, user_id)
team.channels               -- team_id, name, description, type(STANDARD|PRIVATE|SHARED), is_general,
                            -- layout(POSTS|THREADS), moderation(jsonb), email_address null, archived_at, sort_order
                            -- UNIQUE(team_id, lower(name)) WHERE deleted_at IS NULL
team.channel_members        -- channel_id, user_id, role   (chỉ cho PRIVATE và SHARED)
team.channel_shares         -- channel_id, shared_with_team_id | shared_with_tenant_id
team.channel_tabs           -- channel_id | conversation_id, type(FILES|NOTES|WEBSITE|APP), title, config(jsonb), order
team.follows                -- user_id, conversation_id | channel_id
team.templates              -- tenant_id null, name, channels(jsonb), settings(jsonb)

activity.events             -- user_id, user_event_seq, type(MENTION|REPLY|REACTION|MISSED_CALL|MEETING|ADDED_TO_TEAM|
                            -- FOLLOWED|APP|…), payload(jsonb), read_at   UNIQUE(user_id, user_event_seq)
activity.push_subscriptions -- user_id, endpoint, p256dh, auth, user_agent, created_at, last_success_at, failures
activity.email_digests      -- user_id, last_sent_at, next_due_at

meeting.meetings            -- organizer_id, title, description_html, start_at, end_at, time_zone, recurrence(rrule),
                            -- type(SCHEDULED|MEET_NOW|CHANNEL|WEBINAR|TOWN_HALL|CALL), channel_id null,
                            -- join_code (duy nhất), passcode_hash, ics_uid, ics_sequence, options(jsonb),
                            -- conversation_id (chat của cuộc họp), cancelled_at
meeting.invitees            -- meeting_id, user_id | email, required, response(NONE|ACCEPTED|TENTATIVE|DECLINED)
meeting.roles               -- meeting_id, user_id | email, role(ORGANIZER|CO_ORGANIZER|PRESENTER|ATTENDEE)
meeting.occurrences         -- meeting_id, occurrence_start, overrides(jsonb), cancelled
meeting.sessions            -- meeting_id, occurrence_start, media_node_id, started_at, ended_at, locked,
                            -- recording_state   (mỗi lần cuộc họp diễn ra; một cuộc họp một phiên đang mở:
                            -- UNIQUE(meeting_id) WHERE ended_at IS NULL)
meeting.participants        -- session_id, user_id | guest_name, role, joined_at, left_at, device, client_version,
                            -- network_type
meeting.breakout_rooms      -- session_id, name, sub_session_id, assignments(jsonb), opened_at, closed_at, timer
meeting.registrations       -- meeting_id, email, name, fields(jsonb), status, join_token_hash  (webinar)
meeting.qna_questions / qna_answers / qna_votes
meeting.attendance          -- session_id, identity, first_join, last_leave, total_seconds, role
meeting.recordings          -- session_id, started_by, started_at, stopped_at, status, manifest_key, duration,
                            -- expires_at
meeting.recording_tracks    -- recording_id, participant_id, kind(AUDIO|VIDEO|SCREEN), object_key, codec,
                            -- first_rtp_ntp, start_offset_ms
meeting.call_quality        -- session_id, participant_id, sampled_at, direction, rtt_ms, jitter_ms, loss_pct,
                            -- bitrate_kbps, frame_width, frame_height, fps, freeze_count, concealed_ms, network_type

media.nodes                 -- (sổ đăng ký sống ở Valkey; bảng này chỉ lưu lịch sử) host, region, capacity, version

files.files                 -- owner_conversation_id | channel_id, name, size, content_type, sha256, object_key,
                            -- uploaded_by, version, scan_status, preview_key, thumbnail_key
files.file_versions         -- file_id, version, object_key, size, uploaded_by

apps.webhooks_incoming      -- channel_id, name, token_hash, avatar_key, created_by
apps.webhooks_outgoing      -- team_id, name, callback_url, hmac_secret_encrypted
apps.bots                   -- tenant_id, name, oauth_client_id (BlueID), endpoint_url, scopes, installed_scopes(jsonb)
apps.installations          -- app_id, scope(PERSONAL|TEAM|CHAT), scope_id, consented_by
apps.tabs_apps              -- name, content_url_template, valid_domains(jsonb), sso_enabled

admin.policies              -- tenant_id, type(MESSAGING|MEETING|CALLING|LIVE_EVENTS|APP_PERMISSION|APP_SETUP|
                            -- GUEST|EXTERNAL_ACCESS|TEAMS|UPDATE), name, is_default, settings(jsonb)
admin.policy_assignments    -- policy_id, user_id | group_id, priority
admin.external_access       -- tenant_id, mode(ALLOW_ALL|ALLOW_LIST|BLOCK_LIST|NONE), domains(jsonb)

compliance.retention_policies, compliance.legal_holds, compliance.ediscovery_exports (như BlueMail, áp cho tin nhắn,
                            -- tệp, bản ghi)
audit.audit_logs            -- append-only, chuỗi băm
```

---

## 6. MÁY CHỦ MEDIA (BlueChat.Media, BlueChat.Turn, BlueChat.Recorder)

Các thư viện dưới đây là **dự án .NET độc lập** (không EF, không MediatR), mỗi cái có `README.md` liệt kê RFC và mục đã
làm. Đường nóng xử lý gói không cấp phát bộ nhớ; đo bằng benchmark.

### 6.1. `BlueChat.Media.Sdp`
Phân tích và dựng SDP (RFC 8866) theo JSEP (RFC 8829), Unified Plan, BUNDLE (RFC 9143), rtcp-mux (RFC 5761), `msid`,
`mid`, `rid`/simulcast (RFC 8851, 8853), `extmap` (RFC 8285), `rtcp-fb`, `fmtp`, `ssrc-group`, ICE và DTLS
(`fingerprint`, `setup`). Bộ mã đề xuất cho trình duyệt: **Opus** (FEC trong luồng, DTX), **VP8**, **VP9**, **H.264**
(constrained baseline, `packetization-mode=1`), **AV1** (tuỳ chọn); thứ tự ưu tiên cấu hình được theo tenant. Kiểm
bằng SDP thật do Chrome, Firefox, Safari sinh ra (lưu làm bộ mẫu).

### 6.2. `BlueChat.Media.Stun` và ICE
- STUN (RFC 8489): mã hoá/giải mã, `MESSAGE-INTEGRITY`, `MESSAGE-INTEGRITY-SHA256`, `FINGERPRINT`, `XOR-MAPPED-ADDRESS`.
- **ICE-lite** phía máy chủ (RFC 8445), consent freshness (RFC 7675), ICE restart, ICE-TCP thụ động (RFC 6544).
- Một cổng UDP dùng chung: tách luồng theo ufrag trong STUN rồi theo địa chỉ/cổng nguồn; phân biệt STUN/DTLS/RTP theo
  byte đầu (RFC 7983).

### 6.3. DTLS-SRTP
- Bắt tay **DTLS 1.2** (RFC 6347; DTLS 1.3 khi trình duyệt và OpenSSL đều hỗ trợ ổn định) qua OpenSSL dùng BIO bộ
  nhớ; vai trò theo `a=setup`; kiểm `fingerprint` chứng chỉ của trình duyệt khớp SDP; chứng chỉ ECDSA P-256 tự ký của
  nút, xoay định kỳ.
- Xuất khoá theo RFC 5764. **SRTP/SRTCP** (RFC 3711) tự viết phần đóng gói, dùng mật mã của .NET:
  `AEAD_AES_128_GCM` (RFC 7714, ưu tiên) và `AES_CM_128_HMAC_SHA1_80` (AES-CTR dựng từ `Aes` ECB + `HMACSHA1`). Kiểm
  bằng **vector thử của RFC 3711 và RFC 7714**. Cửa sổ chống phát lại (replay window), quản lý ROC.

### 6.4. `BlueChat.Media.Rtp`
- RTP/RTCP (RFC 3550): SR, RR, SDES, BYE; RTCP ghép (compound) và rút gọn (RFC 5506); phản hồi (RFC 4585): NACK,
  PLI, FIR (RFC 5104); RTX (RFC 4588); REMB; **transport-wide congestion control** (phần mở rộng `transport-cc` và gói
  phản hồi tương ứng); `abs-send-time`; mức âm (RFC 6464); `mid`, `rid`, `repaired-rid`; Dependency Descriptor (AV1).
- Bộ đọc payload để biết khung khoá và lớp: VP8 (RFC 7741), VP9 (RFC 9628), H.264 (RFC 6184), AV1 (đặc tả RTP của
  AOMedia), Opus (RFC 7587).

### 6.5. SFU (`BlueChat.Media`)
- Phòng (room) = phiên họp; người tham gia có một transport, nhiều track lên, nhiều track xuống.
- **Chuyển tiếp**: viết lại SSRC, số thứ tự, dấu thời gian, picture ID/TL0PICIDX khi đổi lớp hay đổi nguồn; chỉ đổi lớp
  ở khung khoá (lên) hoặc ranh giới lớp thời gian (xuống).
- **Phục hồi mất gói**: bộ đệm gói theo track để trả lời NACK bằng RTX; gộp và hạn chế tần suất PLI/FIR gửi lên người
  gửi; khi người nhận mới vào, xin khung khoá.
- **Ước lượng băng thông chiều xuống** từ phản hồi transport-cc của trình duyệt: bộ ước lượng dựa trên độ trễ và tỉ lệ
  mất (theo mô hình Google Congestion Control), thăm dò (probing) bằng gói đệm/RTX khi cần tăng; phân bổ băng thông cho
  các ô video theo mức ưu tiên (người đang nói, người được ghim, màn hình chia sẻ trước).
- **Chiều lên**: SFU gửi phản hồi transport-cc để trình duyệt tự điều chỉnh; có thể yêu cầu trình duyệt tắt lớp
  simulcast không ai xem (qua báo hiệu) để tiết kiệm băng thông người gửi.
- **Người đang nói**: từ mức âm, cửa sổ trượt, chống nhảy; phát sự kiện cho máy khách.
- **Last-N** và **phân trang ô video**: chỉ chuyển video những ô đang hiển thị trên màn hình của từng người.
- **Chia sẻ màn hình**: track riêng, ưu tiên độ nét (khuyến nghị nội dung `detail`), có tiếng hệ thống nếu trình duyệt
  cho.
- **Kiểm soát của người tổ chức**: tắt tiếng một người/mọi người, khoá mic/camera của người dự (máy chủ **không chuyển
  tiếp** luồng bị khoá, không chỉ dựa vào giao diện), đưa người ra khỏi họp (đóng transport ngay).
- **Báo hiệu media**: WebSocket thẳng tới nút SFU, xác thực bằng **vé vào họp** ngắn hạn do Api ký (chứa phiên, vai trò,
  quyền); giao thức JSON riêng có phiên bản, mọi thông điệp có kiểm lược đồ.
- **Kết nối lại**: ICE restart khi đổi mạng; mất hẳn thì vào lại phiên với cùng danh tính trong 2 phút mà không bị coi
  là người mới (không qua phòng chờ lại).
- Nút SFU đăng ký sức chứa vào Valkey; Api chọn nút cho phiên mới theo tải và vùng. Nút chết → người tham gia tự vào lại
  phiên trên nút khác (Phase 12: chuyển phiên có kế hoạch khi nâng cấp).

### 6.6. TURN (`BlueChat.Turn`)
- RFC 8656: UDP, TCP, TLS (dùng `SslStream`), Allocate/Refresh/CreatePermission/ChannelBind/Send/Data, kênh
  (ChannelData), xác thực **long-term credential** với tên người dùng có hạn dùng ký HMAC (cấp kèm vé vào họp).
- **Bảo mật**: từ chối chuyển tiếp tới dải địa chỉ nội bộ, loopback, link-local, metadata đám mây, và mọi đích không
  phải nút SFU của chính hệ thống (cấu hình được); hạn mức cấp phát theo người dùng, theo IP, băng thông.
- Đối chứng hành vi bằng `turnutils` của coturn trong kiểm thử.

### 6.7. Ghi hình (`BlueChat.Recorder`)
- Bật ghi → mọi người thấy biểu ngữ "Cuộc họp đang được ghi" (và thông báo cho người vào sau); chỉ vai trò được phép
  theo chính sách.
- Recorder nhận bản sao gói **đã giải mã SRTP** từ SFU (trong cùng mạng nội bộ, kênh có xác thực), **ghi riêng từng
  luồng** vào tệp WebM (VP8/VP9/AV1 + Opus) hoặc Matroska (H.264) bằng bộ đóng gói EBML tự viết; ghi bảng đồng bộ thời
  gian từ RTCP SR (NTP ↔ RTP). Xử lý mất gói (ghi khung khoá tiếp theo), đổi độ phân giải giữa chừng.
- **Trình phát bản ghi trên web** tự viết: phát đồng bộ nhiều luồng, bố cục lưới/người nói/màn hình theo dòng thời gian
  sự kiện của cuộc họp (ai nói lúc nào, ai chia sẻ màn hình lúc nào), tua, tốc độ phát, tải từng luồng.
- **Bản ghi dạng một video lưới duy nhất cần giải mã và mã hoá lại — ngoài phạm vi bản này** (mục 11).
- Bản ghi xuất hiện trong chat của cuộc họp; quyền xem theo thành viên cuộc họp; hết hạn theo chính sách.

### 6.8. `@bluechat/media-client` (TypeScript)
- Điều khiển `RTCPeerConnection` (một kết nối), transceiver, mã hoá simulcast (`sendEncodings` với `rid`, `scaleResolutionDownBy`, `maxBitrate`), thay track khi đổi thiết bị không đàm phán lại.
- Màn hình trước khi vào: chọn camera, micro, loa (`setSinkId` khi trình duyệt hỗ trợ), xem trước, kiểm mức âm.
- Đăng ký luồng theo ô hiển thị (IntersectionObserver + kích thước ô) → gửi lên SFU.
- Thu thập `getStats` định kỳ → gửi số liệu chất lượng về Api; tính chỉ báo chất lượng mạng hiển thị cho người dùng.
- Phát hiện mất kết nối, ICE restart, vào lại phiên; giữ trạng thái tắt mic/camera qua lần kết nối lại.
- Tôn trọng chính sách trình duyệt: `noiseSuppression`, `echoCancellation`, `autoGainControl` (khử ồn là của trình
  duyệt, không tự làm).

---

## 7. GIAO DIỆN, PHÂN QUYỀN, API

### 7.1. Giao diện
- Bộ nhận diện riêng của BlueChat (logo, màu, biểu tượng). Cấu trúc điều hướng theo thói quen của ứng dụng cộng tác:
  thanh ứng dụng bên trái (Hoạt động, Chat, Nhóm, Lịch họp, Cuộc gọi, Tệp, Ứng dụng), danh sách ở giữa, nội dung bên
  phải — **nhưng hình thức thể hiện là của BlueChat**.
- Sáng/tối/tương phản cao; mật độ thoải mái/gọn; cỡ chữ; bố cục chạy từ 1366×768 và ở 375×812 (PWA điện thoại).
- Mọi chức năng dùng được bằng bàn phím; bảng phím tắt mở bằng `Ctrl+.`; phím tắt chính đối chiếu với Teams (T-200).
- Màu chỉ lấy từ token; tương phản đo bằng máy ở mọi chủ đề.

### 7.2. Phân quyền
- Quyền quản trị dạng `chat.admin.<nhóm>.<hành động>`; mọi endpoint quản trị khai `[RequirePermission]`.
- Quyền trong nhóm/kênh/cuộc họp tính theo **vai trò + chính sách áp cho người dùng**, ở **một** dịch vụ dùng chung
  (`IAccessEvaluator`), dùng cho Api, Gateway và SFU (vé vào họp). Phép thử quét: endpoint nào cũng phải có
  `[RequirePermission]`, `[AllowAnonymous]`, hoặc tên trong danh sách tự canh kèm lý do.
- Phép thử truy cập chéo: người ngoài conversation/nhóm/kênh riêng/cuộc họp gọi mọi endpoint phải bị chặn; người tenant
  A không thấy gì của tenant B (trừ chia sẻ liên tenant đã cho phép).

### 7.3. API
- REST `/api/v1`, khuôn trả `{ success, data, message, errors }`, lỗi ProblemDetails có `code`; phân trang bằng con trỏ.
- **API công khai cho tích hợp** (Phase 10): tài nguyên người dùng, nhóm, kênh, chat, tin nhắn, cuộc họp, bản ghi, sự
  kiện thay đổi (webhook đăng ký thay đổi), xác thực OAuth qua BlueID với phạm vi quyền và sự đồng ý của quản trị viên.
  Tài liệu OpenAPI đầy đủ.

---

## 8. CHỨC NĂNG — CHAT, NHÓM, KÊNH, HOẠT ĐỘNG

Mỗi gạch đầu dòng dưới đây là một hoặc nhiều mã `T-xxx` trong bảng đối chiếu.

### 8.1. Chat (1-1, nhóm, với chính mình)
- Bắt đầu chat với một người hoặc nhiều người; đặt tên chat nhóm; ảnh chat; thêm người với lựa chọn **chia sẻ lịch sử**
  (không / N ngày gần nhất / toàn bộ); xoá người; rời chat; chat với chính mình (ghi chú cá nhân).
- Danh sách chat: ghim, ẩn, tắt thông báo, đánh dấu chưa đọc, lọc (chưa đọc, chat, cuộc họp, đã tắt tiếng), **mục tự tạo
  (sections)** để sắp xếp chat và kênh, chế độ xem chat và kênh gộp chung hoặc tách riêng.
- Soạn tin: định dạng chữ đầy đủ (đậm, nghiêng, gạch, màu, cỡ, tiêu đề, danh sách, trích dẫn, bảng, liên kết, **khối mã
  có tô màu cú pháp**), phím tắt kiểu Markdown khi gõ, emoji, chèn ảnh (dán, kéo thả), đính kèm tệp, khung soạn mở rộng,
  lưu nháp tự động theo từng chat, **hẹn giờ gửi**, đánh dấu **quan trọng** và **khẩn** (khẩn: nhắc lại người nhận theo
  chu kỳ tới khi đọc — đối chiếu chu kỳ và thời lượng với Teams).
- Trả lời trích dẫn một tin cụ thể; **chuyển tiếp** tin sang chat khác; sửa tin (hiện "Đã sửa", giữ lịch sử phiên bản
  cho tuân thủ); xoá tin (hoàn tác trong khoảng ngắn); thả cảm xúc bằng mọi emoji; ghim tin trong chat; **lưu tin**.
- **Nhắc tên**: @người, @mọi người trong chat, @kênh, @nhóm, @nhãn (tag); gợi ý khi gõ; người được nhắc nhận thông báo
  ưu tiên.
- **Đã gửi / đã xem** (bật/tắt theo người dùng và theo chính sách), **đang gõ**.
- Xem trước liên kết (proxy tải, chặn SSRF, giới hạn cỡ, có thể tắt theo chính sách), xem trước ảnh/video/PDF ngay trong
  chat.
- **Thăm dò ý kiến** (poll) trong chat: một/nhiều lựa chọn, ẩn danh hay không, hạn chót, xem kết quả.
- Thẻ (card) do ứng dụng gửi: hiển thị theo lược đồ Adaptive Cards (tập con được hỗ trợ ghi trong `docs/00`), có nút hành
  động gọi về ứng dụng.
- Tab trong chat: Tệp, Ảnh, (ứng dụng ở Phase 10).
- Tìm trong chat hiện tại (`Ctrl+F`), nhảy tới tin cũ theo kết quả tìm, nhảy tới tin chưa đọc đầu tiên.

### 8.2. Nhóm (team)
- Tạo nhóm: từ đầu, từ mẫu, từ nhóm khác (sao chép kênh/cài đặt/thành viên tuỳ chọn); công khai / riêng tư / toàn tổ chức.
- Vai trò chủ nhóm / thành viên / khách; thêm/xoá thành viên, yêu cầu tham gia nhóm riêng (chủ nhóm duyệt), **mã tham
  gia**, tìm và tham gia nhóm công khai.
- Cài đặt nhóm: quyền thành viên (tạo/sửa/xoá kênh, thêm ứng dụng, sửa/xoá tin của mình), quyền khách, cài đặt nhắc tên
  @nhóm/@kênh, mục vui (emoji/GIF/sticker — GIF từ dịch vụ ngoài **ngoài phạm vi**, sticker do tenant tự tải lên), ảnh
  nhóm, mô tả.
- **Nhãn (tags)** trong nhóm để nhắc một nhóm người.
- Lưu trữ nhóm (chỉ đọc), khôi phục; xoá nhóm (xoá mềm, khôi phục trong thời hạn như Teams); chủ nhóm và quản trị viên.

### 8.3. Kênh
- Kênh **chuẩn** (mọi thành viên nhóm), **riêng tư** (thành viên chọn riêng, chủ kênh riêng), **chia sẻ** (với người/nhóm
  khác, kể cả tenant khác khi chính sách cho phép — Phase 11).
- Kênh **Chung** mặc định không xoá được; ghim/ẩn kênh; sắp xếp; đổi tên; xoá và khôi phục kênh.
- Hai **bố cục**: bài đăng (post có tiêu đề + luồng trả lời) và luồng (thread) — đối chiếu bố cục mới nhất của Teams.
- **Thông báo (announcement)** có ảnh nền tiêu đề; **đăng chéo** một bài sang nhiều kênh.
- **Điều tiết kênh**: ai được bắt đầu bài, ai được trả lời, bot có được đăng không.
- **Theo dõi** kênh/luồng; mức thông báo theo kênh (mọi hoạt động / tắt / tuỳ chỉnh).
- Tab kênh: Tệp, Ghi chú (soạn thảo chung dạng trang đơn giản), Trang web (iframe theo danh sách miền được phép), ứng dụng.
- **Địa chỉ email của kênh** (gửi thư vào kênh) — **ngoài phạm vi bản này** (cần máy chủ thư; để khi tích hợp BlueMail).
- Họp kênh (cuộc họp thuộc kênh, chat của họp nằm trong kênh).

### 8.4. Hoạt động (activity feed) và thông báo
- Dòng hoạt động: được nhắc tên, trả lời bài của tôi, cảm xúc, cuộc gọi nhỡ, mời họp, được thêm vào nhóm, kênh đang theo
  dõi, ứng dụng; lọc theo loại; đánh dấu đã đọc từng mục / tất cả.
- **Thông báo đẩy Web Push** (RFC 8030 + mã hoá RFC 8291 + VAPID RFC 8292, tự viết bằng ECDH P-256, HKDF, AES-GCM của
  .NET) cho trình duyệt và PWA; thông báo trên màn hình khi đang mở ứng dụng; âm báo bật/tắt.
- **Giờ yên lặng** (theo giờ/ngày), không làm phiền, tắt theo chat/kênh.
- **Thư tóm tắt hoạt động bị bỏ lỡ** qua email (máy khách SMTP tự viết gửi qua máy chủ SMTP cấu hình được), tần suất
  người dùng chọn.

### 8.5. Trạng thái hiện diện (presence)
- Trạng thái: Sẵn sàng, Bận, Không làm phiền, Sẽ quay lại ngay, Đi vắng, Ẩn (hiện ngoại tuyến), Ngoại tuyến; tự động: Đang
  họp, Đang gọi, Đang trình bày (tự bật không làm phiền), Không hoạt động sau N phút.
- Đặt trạng thái có thời hạn; **lời nhắn trạng thái** có hạn và "hiện khi người khác nhắn cho tôi"; **vắng mặt** có thời
  gian và lời nhắn.
- **Danh sách được phép làm phiền** khi đang Không làm phiền.
- Gộp trạng thái từ nhiều thiết bị theo quy tắc ưu tiên (đối chiếu quy tắc của Teams).
- Lưu ở Valkey có TTL theo nhịp tim; không ghi vào PostgreSQL mỗi lần đổi.

### 8.6. Tìm kiếm
- Một ô tìm kiếm chung (`Ctrl+E`): tin nhắn, người, nhóm/kênh, tệp; gợi ý khi gõ; bộ lọc (người gửi, thời gian, kênh,
  có tệp, có nhắc tên tôi); kết quả mở thẳng tới tin trong ngữ cảnh.
- Tìm đa ngôn ngữ (mục 0.2); chỉ trả kết quả người tìm **có quyền xem** (lọc trong truy vấn, không lọc sau).
- Lệnh gõ tắt trong ô tìm (đối chiếu danh sách lệnh của Teams còn hiệu lực).

### 8.7. Tệp
- Tệp trong chat và kênh: tải lên (tus, tiếp tục khi đứt mạng), tải xuống, xem trước (ảnh, PDF, video, âm thanh, văn bản),
  phiên bản, đổi tên, xoá, sao chép liên kết; quyền theo thành viên conversation/kênh.
- Màn hình Tệp chung: tệp gần đây, tệp tôi đã chia sẻ, tệp trong các nhóm.
- Quét tệp tải lên: kiểm chữ ký byte, chặn loại nguy hiểm, giới hạn cỡ theo chính sách.
- Mở/sửa tài liệu Office trực tuyến **ngoài phạm vi** (để khi tích hợp BlueOffice/BlueDrive).

---

## 9. CHỨC NĂNG — CUỘC GỌI VÀ HỌP

### 9.1. Cuộc gọi (từ chat)
- Gọi thoại/video 1-1 và nhóm từ chat; **đổ chuông** trên mọi thiết bị đang mở (WebSocket + Web Push), nhận/từ chối/
  nhắn lại; hết giờ thì ghi **cuộc gọi nhỡ** vào chat và dòng hoạt động.
- Trong cuộc gọi: thêm người, chuyển từ thoại sang video, chia sẻ màn hình, chat, giữ máy (hold) với âm báo, kết thúc.
- Lịch sử cuộc gọi, quay số nhanh (danh bạ ưu tiên).
- Gọi điện thoại thường (PSTN), hộp thư thoại, chuyển cuộc gọi kiểu tổng đài: **ngoài phạm vi**.

### 9.2. Lên lịch họp
- Tạo cuộc họp: tiêu đề, người dự (bắt buộc/tuỳ chọn, trong và ngoài tổ chức), thời gian, múi giờ, **lặp lại** (RRULE
  RFC 5545), nội dung, họp trong kênh.
- Gửi lời mời **iCalendar qua email** (RFC 5545, iTIP RFC 5546, iMIP RFC 6047) để Outlook, Gmail, Apple Calendar thêm vào
  lịch và trả lời; cập nhật và huỷ gửi đúng `SEQUENCE`; nhận phản hồi trả lời qua liên kết trong thư.
- Màn hình **Lịch họp** trong BlueChat: ngày/tuần/tháng, các cuộc họp mình được mời, tham gia bằng một nút. (Lịch đầy đủ
  kiểu Outlook là sản phẩm khác.)
- **Họp ngay** (Meet now) trong kênh hoặc chat; tham gia bằng liên kết hoặc **mã cuộc họp + mật mã**.
- **Tuỳ chọn cuộc họp**: ai được bỏ qua phòng chờ, ai trình bày được, cho phép mic/camera người dự, chat (bật/tắt/chỉ trong
  họp), cảm xúc, tự động ghi, khoá cuộc họp, ẩn tên người dự, thông báo khi người gọi vào/ra; đối chiếu đầy đủ danh sách
  tuỳ chọn của Teams.

### 9.3. Trong cuộc họp
- **Màn hình trước khi vào**: chọn thiết bị, xem trước, bật/tắt mic camera, tên hiển thị cho khách.
- **Phòng chờ**: người tổ chức/trình bày thấy danh sách, cho vào từng người hoặc tất cả, từ chối.
- Vai trò người tổ chức / đồng tổ chức / người trình bày / người dự; đổi vai trò trong họp.
- Tắt/bật mic (`Ctrl+Shift+M`), camera (`Ctrl+Shift+O`), giơ tay (`Ctrl+Shift+K`) có **thứ tự giơ tay**, cảm xúc bay trên
  màn hình, chat trong họp (lưu lại sau họp), danh sách người tham gia với trạng thái mic/camera/tay.
- **Chia sẻ** toàn màn hình / cửa sổ / tab, có âm thanh hệ thống khi trình duyệt cho; người trình bày thấy viền và nút
  dừng; người xem phóng to/thu nhỏ.
- **Bố cục**: lưới (nhiều trang, cỡ lưới lớn đối chiếu với Teams), người nói, tập trung nội dung; **ghim** cho riêng mình,
  **nổi bật (spotlight)** cho mọi người; ẩn video của chính mình; ẩn người không bật camera.
- Người tổ chức: tắt mic tất cả, khoá mic/camera người dự, đưa người ra, khoá cuộc họp, kết thúc cho mọi người.
- **Chỉ báo chất lượng mạng**, thông báo khi mic đang tắt mà đang nói, cảnh báo thiết bị lỗi.
- **Phòng nhóm (breakout rooms)**: tạo tự động/thủ công, đặt hẹn giờ, gửi thông báo cho mọi phòng, chuyển người giữa phòng,
  mở/đóng phòng, người tổ chức vào từng phòng; đối chiếu đầy đủ với Teams.
- **Thăm dò trong họp**, **Hỏi & Đáp** (có kiểm duyệt, bình chọn câu hỏi), **ghi chú cuộc họp** (trang soạn chung gắn với
  cuộc họp).
- **Ghi hình** (mục 6.7) và **báo cáo điểm danh** (vào/ra, tổng thời lượng, tải về CSV).
- **Tổng kết sau họp**: bản ghi, chat, ghi chú, điểm danh ở một chỗ.
- Họp có khách không có tài khoản (vào bằng tên, qua phòng chờ theo tuỳ chọn).

### 9.4. Webinar và town hall
- **Webinar**: trang đăng ký (trường tuỳ chỉnh, duyệt đăng ký, giới hạn số lượng), email xác nhận và nhắc, người trình bày
  và người dự tách biệt, người dự mặc định không mic/camera, Hỏi & Đáp, báo cáo người đăng ký và người dự.
- **Town hall**: người dự chỉ xem, quy mô lớn qua ghép tầng SFU (Phase 12), độ trễ thấp; Hỏi & Đáp có kiểm duyệt.
- Mục tiêu quy mô: đối chiếu giới hạn của Teams (Z6) và ghi mức BlueChat đạt được đã **đo** ở Phase 12.

---

## 10. CHỨC NĂNG — ỨNG DỤNG, QUẢN TRỊ, TUÂN THỦ

### 10.1. Ứng dụng và tích hợp
- **Webhook đến**: URL bí mật cho một kênh, nhận JSON (văn bản hoặc thẻ) để đăng tin.
- **Webhook đi**: gọi một URL khi có người @nhắc webhook, ký HMAC, nhận câu trả lời để đăng lại.
- **Bot API**: bot đăng ký qua BlueID (OAuth), nhận sự kiện tin nhắn/cài đặt qua HTTPS có chữ ký, gửi tin và thẻ, cập nhật
  thẻ, nhắn chủ động (đã được cài).
- **Tab ứng dụng**: trang web nhúng iframe có SSO (thẻ BlueID ngắn hạn qua `postMessage`), danh sách miền hợp lệ.
- Quản lý ứng dụng trong tenant: duyệt, chặn, ghim ứng dụng cho người dùng theo chính sách.
- API công khai (mục 7.3) + đăng ký nhận thay đổi.

### 10.2. Trung tâm quản trị
- **Nhóm**: danh sách, thành viên, kênh, chủ nhóm, lưu trữ, xoá/khôi phục, nhóm không có chủ.
- **Chính sách** (gán theo người hoặc nhóm, có mặc định của tổ chức): nhắn tin (sửa/xoá tin, đã xem, nhắc khẩn, xem trước
  liên kết, nội dung vui), họp (ghi hình, phòng chờ, chia sẻ màn hình, giới hạn người, chat, ẩn danh tham gia), cuộc gọi,
  webinar/town hall, nhóm (ai được tạo nhóm, kênh riêng/chia sẻ), khách, ứng dụng; đối chiếu nhóm chính sách với Teams.
- **Truy cập bên ngoài** (liên tenant trong cùng hệ thống BlueChat): cho tất cả / danh sách cho phép / danh sách chặn;
  **khách mời**.
- **Báo cáo sử dụng**: người dùng hoạt động, tin nhắn, cuộc họp, phút họp, thiết bị; xuất Excel/CSV.
- **Bảng chất lượng cuộc gọi**: từng cuộc gọi/họp — mất gói, độ trễ, rung pha, đóng băng khung hình theo người, theo mạng
  (dây/Wi-Fi/di động), theo vị trí; tổng hợp theo tuần; tìm cuộc gọi có vấn đề.
- **Nhật ký kiểm toán** (append-only, chuỗi băm).
- Mọi khoá cấu hình/chính sách phải có **chỗ đọc** trong mã — phép thử quét bảo đảm.

### 10.3. Tuân thủ
- **Lưu giữ** tin nhắn, tệp, bản ghi theo chính sách (giữ tối thiểu / xoá sau N ngày).
- **Giữ pháp lý** theo người/nhóm: tin đã sửa/xoá vẫn giữ mọi phiên bản cho người tuân thủ.
- **eDiscovery**: tìm trên chat/kênh/cuộc họp theo người, thời gian, từ khoá; xuất kèm tệp kê khai SHA-256.
- **Rào thông tin** (cấm hai nhóm người liên lạc) và **ngăn rò rỉ dữ liệu** theo mẫu (số thẻ, số định danh): Phase 11, mức
  cơ bản; đối chiếu với Teams và ghi phần thiếu.

---

## 11. NGOÀI PHẠM VI BẢN NÀY (ghi `Ngoài phạm vi` trong bảng đối chiếu, kèm lý do)

Gọi điện thoại thường (PSTN), số điện thoại, hộp thư thoại, tổng đài; **ứng dụng di động gốc** (cần bộ WebRTC gốc — bản
này dùng PWA); phụ đề trực tiếp, chép lời, dịch, trợ lý AI; nền ảo/làm mờ nền (cần mô hình học máy); bảng trắng; trình
chiếu PowerPoint Live; thành phần cộng tác kiểu Loop; chế độ Together; các ứng dụng Planner/Approvals/Shifts; liên thông
với Teams/Slack/Skype; mã hoá đầu-cuối (để giai đoạn sau với SFrame RFC 9605); bản ghi dạng một video lưới; GIF từ dịch vụ
ngoài; địa chỉ email của kênh; tích hợp BlueMail/BlueDrive/BlueOffice. **Không viết interface, bảng hay nút chờ sẵn cho
những thứ này.**

---

## 12. KIỂM THỬ

- **Mỗi lỗi sửa kèm phép thử đỏ trước, xanh sau.**
- **Unit**: SDP (bộ mẫu SDP thật của Chrome/Firefox/Safari), STUN (vector RFC 5769), SRTP (vector RFC 3711, 7714), RTCP,
  bộ đọc payload VP8/VP9/H.264/AV1, bộ ước lượng băng thông (kịch bản mô phỏng), viết lại số thứ tự khi đổi lớp, iCalendar
  (RRULE, múi giờ, ngày đổi giờ mùa hè), Web Push (vector RFC 8291), lọc HTML (bộ mẫu XSS), quyền.
- **Integration**: mọi endpoint; thứ tự và không trùng tin (gửi lại cùng `client_message_id` từ ba yêu cầu **thật sự song
  song** chỉ ra một tin); đồng bộ sau mất kết nối; số chưa đọc khớp đếm độc lập; truy cập chéo; khoá tài khoản ở BlueID
  ngắt phiên chat và rời cuộc họp ngay.
- **Media với trình duyệt thật** (Playwright, thiết bị giả): 2, 4, 9, 25, 50 người trong một phòng; luồng tới đúng người,
  đổi lớp simulcast khi thu nhỏ ô, chia sẻ màn hình, tắt mic từ người tổ chức (kiểm **máy chủ không chuyển gói**, không chỉ
  giao diện), vào lại sau khi cắt mạng.
- **Đối chứng bằng cài đặt độc lập**: Pion và aiortc vào cùng phòng với trình duyệt; `tshark` phân tích gói không lỗi định
  dạng; `turnutils` của coturn chạy với TURN của mình; đối chứng hành vi TURN chặn địa chỉ nội bộ.
- **Mạng xấu** (`tc netem`): 5 %, 10 %, 20 % mất gói; 150 ms, 300 ms trễ; băng thông 500 kbps — đo bằng `getStats`: tỉ lệ
  đóng băng, thời gian âm thanh bị che lấp, thời gian phục hồi; ngưỡng đạt ghi ở `docs/00`.
- **Fuzz**: SDP, STUN, RTP/RTCP, thông điệp báo hiệu, iCalendar, thân webhook.
- **Tải**: k6 cho REST/WebSocket; bộ tạo người tham gia ảo (máy khách media đơn giản bằng .NET hoặc Pion) cho SFU; đo luồng
  chuyển tiếp tối đa trên một nút, độ trễ thêm của SFU, CPU/RAM.
- **E2E giao diện** ở 1366×768 và 375×812; truy cập bằng bàn phím; tương phản đo bằng máy.
- **Phép thử chấp nhận theo bảng đối chiếu** (mục 3): mỗi dòng `Xong` có phép thử trỏ tới.

---

## 13. QUY ƯỚC CODE

- API: `Application/Features/<Tên>/` (Command/Query + Handler + Validator + DTO); không logic nghiệp vụ trong Controller.
- Thư viện media tách bộ phân tích khỏi trạng thái khỏi I/O để unit test và fuzz không cần mạng; mỗi hàm giao thức ghi RFC
  và mục.
- Mọi ghi tin nhắn đi qua **một** dịch vụ `IMessageStore` (cấp `seq`, chống trùng, phát sự kiện); phép thử quét cấm ghi thẳng
  bảng tin nhắn ở chỗ khác.
- Không bao giờ dùng đồng hồ máy khách để sắp xếp hay tính hạn.
- Mọi tiến trình chạy `InvariantCulture`; định dạng cho người dùng lấy từ ngôn ngữ của yêu cầu.
- Frontend: `web/src/modules/` (`activity`, `chat`, `teams`, `calendar`, `calls`, `meeting`, `files`, `apps`, `settings`,
  `admin`); `packages/media-client`; không `any`; màu từ token; chuỗi ở tệp dịch.
- Migration có tên nghĩa, không sửa migration đã commit; phép thử bảo đảm mọi lớp migration có `[Migration]`.
- Sau mỗi phase: build sạch, mọi phép thử xanh, cập nhật `README.md`, `CLAUDE.md`, `docs/`, **bảng đối chiếu**, commit
  `phase-N: …`.

---

## 14. THỨ TỰ THỰC HIỆN

| Phase | Nội dung | Nghiệm thu |
|---|---|---|
| **0** | Z1–Z8 | BlueID sẵn sàng; nguyên mẫu DTLS bắt tay được với Chrome/Firefox; khung kiểm thử media chạy; bảng đối chiếu có đủ dòng |
| **1** | Nền móng: solution, Docker, PostgreSQL, BlueID, tenant, nhật ký, đa ngôn ngữ, khung React (thanh ứng dụng), Gateway SignalR + Valkey | Đăng nhập, mở ứng dụng, sự kiện tới đúng phiên |
| **2** | Chat (8.1) + `IMessageStore`, đồng bộ, đã xem, đang gõ, xem trước liên kết, tệp đính kèm cơ bản | Không mất/trùng/sai thứ tự dưới mất mạng và song song |
| **3** | Nhóm và kênh (8.2, 8.3) | |
| **4** | Hoạt động, thông báo đẩy, thư tóm tắt, trạng thái hiện diện, tìm kiếm, tệp (8.4–8.7) | |
| **5** | Thư viện media: SDP, STUN/ICE-lite/ICE-TCP, DTLS-SRTP, RTP/RTCP, TURN (6.1–6.4, 6.6) | Trình duyệt kết nối tới nút media, gửi và nhận lại chính luồng của mình (vòng lặp) |
| **6** | SFU (6.5) + `media-client` (6.8) + cuộc gọi trong chat (9.1) | Gọi 1-1 và nhóm 9 người ổn định trên mạng xấu theo ngưỡng |
| **7** | Họp: lên lịch + iCalendar, phòng chờ, vai trò, tuỳ chọn, điều khiển, chia sẻ màn hình, bố cục, chat, giơ tay, cảm xúc, kết nối lại (9.2, 9.3 phần lõi) | 50 người trong một phòng với trình duyệt thật |
| **8** | Phòng nhóm, thăm dò, Hỏi & Đáp, ghi chú, ghi hình + trình phát, điểm danh, tổng kết sau họp | |
| **9** | Thẻ (Adaptive Cards tập con), thăm dò trong chat, tab trong chat/kênh, ghi chú kênh | |
| **10** | Ứng dụng: webhook đến/đi, Bot API, tab có SSO, API công khai + đăng ký thay đổi (10.1) | |
| **11** | Quản trị, chính sách, truy cập bên ngoài, khách, báo cáo, bảng chất lượng cuộc gọi, tuân thủ (10.2, 10.3), kênh chia sẻ liên tenant | |
| **12** | Quy mô: nhiều nút SFU, ghép tầng, webinar, town hall, phép thử tải | Đạt mức đã cam kết ở `docs/00`, ghi số đo |
| **13** | Ứng dụng desktop bọc web: Windows (WebView2, host .NET), macOS (WKWebView, .NET for macOS) — thông báo hệ thống, khay, tự khởi động, liên kết `bluechat://`, phím tắt toàn cục tắt mic, tự cập nhật có kiểm chữ ký (tự viết bằng mật mã .NET) | |
| **14** | Hoàn thiện: hiệu năng, truy cập, rà bảo mật, sao lưu/phục hồi thử thật, tài liệu, **rà toàn bộ bảng đối chiếu với Teams lần cuối** | `docker compose up -d` lên hệ thống đầy đủ có dữ liệu mẫu |

### 14.1. Tài liệu `docs/`
`00-quyet-dinh-ky-thuat.md` · `01-huong-dan-su-dung.md` · `02-huong-dan-quan-tri.md` · `03-sao-luu-phuc-hoi.md` ·
`04-cai-dat-mang-va-tuong-lua.md` (cổng, IP, TURN, yêu cầu mạng của khách) · `05-api.md` · `06-kich-ban-kiem-thu.md` ·
`07-giao-thuc-media.md` (bảng RFC: mục nào đã làm, bằng chứng) · `08-so-loi.md` · `09-giay-phep-thanh-phan.md` ·
**`doi-chieu-teams.md`**.

### 14.2. Dữ liệu mẫu
Hai tenant (một `en`, một `vi`), 30–50 người dùng tên nhiều nước không trùng, vài nhóm có kênh chuẩn/riêng tư, chat 1-1 và
nhóm có lịch sử nhiều ngôn ngữ, tệp, thăm dò, cuộc họp đã lên lịch và lặp lại, một bản ghi mẫu. Mốc thời gian tính từ ngày
nạp; không tin nào ở tương lai (trừ tin hẹn giờ).

---

## 15. BÀI HỌC TỪ CÁC DỰ ÁN TRƯỚC — ĐỪNG LẶP LẠI

1. **Luật "một … một" phải là ràng buộc duy nhất ở CSDL**, phép thử đồng thời gửi yêu cầu **thật sự song song**: một tin
   một `client_message_id`, một `seq` một vị trí, một chat 1-1 một cặp người, một phiên họp đang mở một cuộc họp, một phiếu
   bầu một người.
2. **Công tắc được lưu không có nghĩa là có ai đọc nó** — mọi chính sách phải có chỗ đọc; có phép thử quét.
3. **Chặn ở giao diện không phải là chặn.** Khoá mic người dự thì SFU không chuyển gói; người ngoài kênh riêng gọi API thì
   bị từ chối.
4. **Cột chép sẵn phải tính lại từ nguồn** (số chưa đọc, số thành viên).
5. **Tự kiểm bằng mã của mình không chứng minh được gì** — đối chứng bằng trình duyệt thật, Pion, aiortc, coturn, tshark,
   vector thử của RFC.
6. **Giao thức phải trả lời sai một cách ồn ào**: SDP hỏng, thông điệp báo hiệu sai lược đồ → lỗi rõ ràng, không im lặng.
7. **Bộ đếm và danh sách chạy trên cùng một tập dòng; phân trang sắp theo khoá duy nhất ở cuối.**
8. **Khoá một tài khoản phải ngắt luôn phiên đang mở** — chat, WebSocket, cuộc họp.
9. **"Đã lưu" chưa phải "hiện ra được"** — kiểm tới nơi người dùng thấy, trên thiết bị thứ hai.
10. **Đo ở đúng khổ và đúng mạng đã cam kết** — 1366×768, 375×812, mạng mất gói.
11. **Màu viết thẳng không đi qua token**; tương phản đo bằng máy.
12. **Giờ hiện cho người dùng là giờ của người dùng; thứ tự tin nhắn là của máy chủ.**
13. **Việc gửi đi phải hỏi kênh gửi có mở không**: không báo "đã gửi lời mời" khi SMTP chưa cấu hình.
14. **Dữ liệu mẫu là một phần của sản phẩm.**
15. **Triển khai xong phải kiểm chính thứ vừa sửa trên máy chủ thật** — với media: gọi thật giữa hai mạng khác nhau, một bên
    sau tường lửa chỉ mở 443.
16. **Mỗi lượt triển khai dọn image cũ.**
17. **Dọn theo kết cục, không theo cách thoát.**
18. **Ký tự U+0000 và UTF-8 hỏng sẽ đến** — lọc ở cửa vào.
19. **Đối chiếu với Teams bằng tài liệu hiện hành, không bằng trí nhớ** — Teams đổi liên tục; ghi URL và ngày đọc.

## 16. LƯU Ý CUỐI

1. **Không stub.** Không làm được thì dừng lại hỏi.
2. **Không mất, không trùng, không sai thứ tự tin nhắn.**
3. **Âm thanh quan trọng hơn hình ảnh.** Mạng xấu thì giảm/tắt video trước, giữ tiếng.
4. **TURN và SFU là cửa mở ra Internet** — mọi thứ đi vào phải được xác thực và giới hạn.
5. **Giống Teams ở chức năng, khác Teams ở nhận diện.**
6. Mỗi phase xong: đối chiếu với tài liệu này **và** với `docs/doi-chieu-teams.md`.
