# Đối chiếu BlueChat với Microsoft Teams

Quy trình: đặc tả mục 3. Mỗi chức năng làm xong: đọc tài liệu chính thức của Microsoft (ghi URL + ngày đọc), liệt kê
hành vi, so từng dòng, dùng thử Teams thật nếu có tài khoản thử nghiệm, viết phép thử chấp nhận, rồi mới đánh `Xong`.

Trạng thái: `Chưa làm` / `Đang làm` / `Xong` / `Khác có chủ ý` (ghi lý do) / `Ngoài phạm vi` (đặc tả mục 11).

**Tài khoản Teams thử nghiệm (Z9):** chưa có (2026-11-25), nên mọi dòng hiện **chỉ đối chiếu theo tài liệu**.
Tenant thử nghiệm đã dùng: —

---

## Giới hạn (Z6)

Nguồn: <https://learn.microsoft.com/en-us/microsoftteams/limits-specifications-teams>. Trang cập nhật 2026-11-09,
đọc ngày 2026-11-25. Mục tiêu BlueChat: **bằng hoặc hơn** Teams. Cột "Đo được" điền ở Phase 12.

### Nhóm và kênh

| Hạng mục | Teams | Mục tiêu BlueChat | Đo được |
|---|---|---|---|
| Nhóm một người tạo được | theo giới hạn 250 đối tượng thư mục (trừ quản trị viên toàn cục) | 250 (cấu hình được theo tenant) | |
| Nhóm một người là thành viên (gồm nhóm lưu trữ) | 1 000 | 1 000 | |
| Thành viên một nhóm | 25 000 (gồm thành viên kênh chia sẻ từ ngoài) | 25 000 | |
| Chặn @nhóm/@kênh khi nhóm lớn hơn | 10 000 thành viên | 10 000 | |
| Chủ nhóm mỗi nhóm | 100 | 100 | |
| Nhóm toàn tổ chức mỗi tenant (gồm lưu trữ) | 5 | 5 | |
| Thành viên nhóm toàn tổ chức | 10 000 | 10 000 | |
| Kênh mỗi nhóm (gồm kênh đã xoá; chuẩn + riêng tư + chia sẻ) | 1 000 | 1 000 | |
| Thành viên kênh riêng tư | 5 000 | 5 000 | |
| Kênh chia sẻ mỗi nhóm | 1 000 (trong tổng 1 000) | 1 000 | |
| Số nhóm một kênh chia sẻ được chia sẻ tới (trừ nhóm cha) | 50 | 50 | |
| Thành viên trực tiếp kênh chia sẻ | 5 000, gồm tối đa 50 nhóm (mỗi nhóm tính 1) | 5 000 | |
| Kênh đã xoá khôi phục được trong | 30 ngày (vẫn tính vào giới hạn) | 30 ngày | |
| Nhập nhóm người dùng vào nhóm | 3 500 | 3 500 | |
| Cỡ bài đăng kênh | ≈ 100 KB (không tính ảnh base64) | 100 KB | |
| Nhãn (tag) mỗi nhóm | 200 | 200 | |
| Thành viên gán cho một nhãn | 200 | 200 | |
| Nhãn gán cho một người mỗi nhóm | 25 | 25 | |
| Nhãn mặc định gợi ý mỗi nhóm | 25 | 25 | |
| Tên kênh: ký tự cấm | `~ # % & * { } + / \ : < > ? \| ' " , ..`, mã 0x00–0x1F, 0x80–0x9F | Khác có chủ ý: chỉ cấm ký tự điều khiển và ký tự hướng (bidi) ẩn; các ký tự khác cho phép vì BlueChat không lưu kênh thành thư mục SharePoint | |
| Tên kênh: từ dành riêng | forms, CON, CONIN$, CONOUT$, PRN, AUX, NUL, COM1–9, LPT1–9, desktop.ini, _vti_; không bắt đầu bằng `_` hay `.`, không kết thúc bằng `.` | Khác có chủ ý: không cần (lý do như trên) | |

### Chat và tin nhắn

| Hạng mục | Teams | Mục tiêu BlueChat | Đo được |
|---|---|---|---|
| Người trong một chat (nhóm) | 250 | 250 | |
| Thêm vào chat nhóm cùng lúc | 200 | 200 | |
| Chat > 20 người thì tắt | trả lời tự động Outlook, lời nhắn trạng thái, đang gõ, gọi thoại/video, chia sẻ, đã xem, "Set Delivery Options" | Khác có chủ ý: giữ đang gõ và đã xem tới 250 người (Gateway gộp sự kiện); gọi từ chat như Teams (20) | |
| Người trong cuộc gọi thoại/video từ chat | 20 | 20 (lớn hơn → chuyển thành cuộc họp) | |
| Tệp đính kèm mỗi tin | 10 | 10 | |
| Cỡ tệp trong chat | 100 MB | 100 MB (chính sách tenant đặt được cao hơn) | |
| Cỡ tin | ≈ 100 KB (khuyến nghị ≤ 80 KB) | 100 KB | |
| Tên miền tin cậy cho truy cập bên ngoài | 4 000 | 4 000 | |
| Email vào kênh | 24 KB thư, 20 tệp < 10 MB, 50 ảnh, giới hạn tần suất | Ngoài phạm vi (đặc tả 8.3, 11) | |

### Họp và cuộc gọi

| Hạng mục | Teams | Mục tiêu BlueChat | Đo được |
|---|---|---|---|
| Người trong cuộc họp (chat + gọi vào) | 300 (Business Basic/Standard/Premium, Essentials, A1); tới 1 000 (E3/E5/A3/A5/G3/G5…) | 300 trên một nút SFU; 1 000 bằng ghép tầng (Phase 12) | |
| Thời lượng cuộc họp/sự kiện | 30 giờ | 30 giờ | |
| Bản ghi tối đa | 4 giờ hoặc 1,5 GB, rồi tự ghi tiếp tệp mới | Khác có chủ ý: ghi từng luồng; cắt đoạn mỗi 4 giờ, không giới hạn cỡ | |
| Bản ghi không tải lên Stream giữ để tải về | 20 ngày | Theo chính sách lưu giữ của tenant (mặc định ≥ 20 ngày) | |
| Phòng nhóm (breakout) | chỉ khi cuộc họp ≤ 1 000 người | ≤ 1 000 người | |
| Hết hạn cuộc họp (bản xem trước) | một lần: 60 ngày sau giờ họp; Họp ngay: 60 ngày sau khi tạo liên kết; lặp có ngày kết thúc: 60 ngày sau lần cuối; lặp không kết thúc: 1 năm sau lần dùng cuối; vào/sửa trước hạn cộng 60 ngày (trừ Họp ngay) | Như Teams | |
| Cỡ tệp PowerPoint | 2 GB | Ngoài phạm vi (PowerPoint Live, đặc tả 11) | |
| Trình duyệt: luồng video vào trong họp | Teams web chỉ 1 luồng (video hoặc màn hình) | Khác có chủ ý: BlueChat là ứng dụng web, hiển thị lưới nhiều luồng trên trình duyệt | |

### Webinar, town hall, sự kiện trực tiếp

| Hạng mục | Teams | Mục tiêu BlueChat | Đo được |
|---|---|---|---|
| Webinar | trang không nêu số riêng; dùng giới hạn cuộc họp (30 giờ) | Như cuộc họp; người dự đo ở Phase 12 | |
| Town hall | trang không nêu số riêng | Ghép tầng SFU, mục tiêu ≥ 10 000 người xem; đo ở Phase 12 | |
| Sự kiện trực tiếp (live events) | ngừng 2026-06-30 (sự kiện đã lên lịch trước đó chạy tới 2027-02-28): 10 000 người xem, 4 giờ, 15 sự kiện đồng thời mỗi tổ chức | Không làm riêng; town hall thay thế | |

### Lưu trữ

| Hạng mục | Teams | Mục tiêu BlueChat | Đo được |
|---|---|---|---|
| Dung lượng mỗi tổ chức | 1 TB + 10 GB mỗi giấy phép (F1: 1 TB) | Theo gói tenant (`tenant.tenants.limits`) | |
| Tệp của một nhóm | 25 TB mỗi site/nhóm | 25 TB | |
| Cỡ một tệp tải lên | 250 GB | 250 GB (tus, tải tiếp khi đứt) | |

---

## Bảng chức năng (Z8)

Cột: Mã · Chức năng · Mục đặc tả · Hành vi của Teams (tóm tắt) · Nguồn (URL + ngày đọc) · BlueChat · Khác biệt · Bằng chứng.
Cột "Hành vi" và "Nguồn" điền khi làm chức năng đó (không ghi theo trí nhớ, đặc tả 3 bước 1).

### 8.1 Chat

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-001 | Bắt đầu chat 1-1 | 8.1 | | | Chưa làm | | |
| T-002 | Chat nhóm nhiều người | 8.1 | | | Chưa làm | | |
| T-003 | Đặt tên chat nhóm | 8.1 | | | Chưa làm | | |
| T-004 | Ảnh của chat nhóm | 8.1 | | | Chưa làm | | |
| T-005 | Thêm người, chọn chia sẻ lịch sử (không / N ngày / toàn bộ) | 8.1 | | | Chưa làm | | |
| T-006 | Xoá người khỏi chat | 8.1 | | | Chưa làm | | |
| T-007 | Rời chat | 8.1 | | | Chưa làm | | |
| T-008 | Chat với chính mình | 8.1 | | | Chưa làm | | |
| T-009 | Ghim chat | 8.1 | | | Chưa làm | | |
| T-010 | Ẩn chat | 8.1 | | | Chưa làm | | |
| T-011 | Tắt thông báo một chat | 8.1 | | | Chưa làm | | |
| T-012 | Đánh dấu chưa đọc | 8.1 | | | Chưa làm | | |
| T-013 | Lọc danh sách (chưa đọc, chat, cuộc họp, đã tắt tiếng) | 8.1 | | | Chưa làm | | |
| T-014 | Mục tự tạo (sections) cho chat và kênh | 8.1 | | | Chưa làm | | |
| T-015 | Xem chat và kênh gộp chung hoặc tách riêng | 8.1 | | | Chưa làm | | |
| T-016 | Định dạng chữ đầy đủ (đậm, nghiêng, gạch, màu, cỡ, tiêu đề, danh sách, trích dẫn, bảng, liên kết) | 8.1 | | | Chưa làm | | |
| T-017 | Khối mã có tô màu cú pháp | 8.1 | | | Chưa làm | | |
| T-018 | Phím tắt kiểu Markdown khi gõ | 8.1 | | | Chưa làm | | |
| T-019 | Emoji; chèn ảnh bằng dán và kéo thả | 8.1 | | | Chưa làm | | |
| T-020 | Đính kèm tệp | 8.1 | | | Chưa làm | | |
| T-021 | Sửa tin nhắn ("Đã sửa", lịch sử phiên bản cho tuân thủ) | 8.1 | | | Chưa làm | | |
| T-022 | Xoá tin, hoàn tác trong khoảng ngắn | 8.1 | | | Chưa làm | | |
| T-023 | Trả lời trích dẫn một tin | 8.1 | | | Chưa làm | | |
| T-024 | Chuyển tiếp tin sang chat khác | 8.1 | | | Chưa làm | | |
| T-025 | Thả cảm xúc bằng mọi emoji | 8.1 | | | Chưa làm | | |
| T-026 | Ghim tin trong chat | 8.1 | | | Chưa làm | | |
| T-027 | Lưu tin | 8.1 | | | Chưa làm | | |
| T-028 | Khung soạn mở rộng | 8.1 | | | Chưa làm | | |
| T-029 | Lưu nháp tự động theo từng chat | 8.1 | | | Chưa làm | | |
| T-030 | Hẹn giờ gửi | 8.1 | | | Chưa làm | | |
| T-031 | Đánh dấu tin quan trọng | 8.1 | | | Chưa làm | | |
| T-032 | Tin khẩn: nhắc lại người nhận theo chu kỳ tới khi đọc | 8.1 | | | Chưa làm | | |
| T-033 | Nhắc tên @người, gợi ý khi gõ, thông báo ưu tiên | 8.1 | | | Chưa làm | | |
| T-034 | @mọi người trong chat | 8.1 | | | Chưa làm | | |
| T-035 | @kênh, @nhóm | 8.1 | | | Chưa làm | | |
| T-036 | @nhãn (tag) | 8.1 | | | Chưa làm | | |
| T-037 | Đã gửi / đã xem (theo người dùng và chính sách) | 8.1 | | | Chưa làm | | |
| T-038 | Đang gõ | 8.1 | | | Chưa làm | | |
| T-039 | Xem trước liên kết (chặn SSRF, tắt được theo chính sách) | 8.1 | | | Chưa làm | | |
| T-040 | Xem trước ảnh, video, PDF ngay trong chat | 8.1 | | | Chưa làm | | |
| T-041 | Thăm dò trong chat (một/nhiều lựa chọn, ẩn danh, hạn chót, kết quả) | 8.1 | | | Chưa làm | | |
| T-042 | Thẻ ứng dụng (lược đồ Adaptive Cards), nút hành động | 8.1 | | | Chưa làm | | |
| T-043 | Tab trong chat: Tệp, Ảnh | 8.1 | | | Chưa làm | | |
| T-044 | Tìm trong chat hiện tại (`Ctrl+F`), nhảy tới tin cũ | 8.1 | | | Chưa làm | | |
| T-045 | Nhảy tới tin chưa đọc đầu tiên | 8.1 | | | Chưa làm | | |

### 8.2 Nhóm

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-050 | Tạo nhóm từ đầu | 8.2 | | | Chưa làm | | |
| T-051 | Tạo nhóm từ mẫu | 8.2 | | | Chưa làm | | |
| T-052 | Tạo nhóm từ nhóm khác (chọn sao chép kênh, cài đặt, thành viên) | 8.2 | | | Chưa làm | | |
| T-053 | Công khai / riêng tư / toàn tổ chức | 8.2 | | | Chưa làm | | |
| T-054 | Vai trò chủ nhóm / thành viên / khách | 8.2 | | | Chưa làm | | |
| T-055 | Thêm, xoá thành viên | 8.2 | | | Chưa làm | | |
| T-056 | Yêu cầu tham gia nhóm riêng tư, chủ nhóm duyệt | 8.2 | | | Chưa làm | | |
| T-057 | Mã tham gia nhóm | 8.2 | | | Chưa làm | | |
| T-058 | Tìm và tham gia nhóm công khai | 8.2 | | | Chưa làm | | |
| T-059 | Quyền thành viên (kênh, ứng dụng, sửa/xoá tin) | 8.2 | | | Chưa làm | | |
| T-060 | Quyền khách | 8.2 | | | Chưa làm | | |
| T-061 | Cài đặt nhắc tên @nhóm/@kênh | 8.2 | | | Chưa làm | | |
| T-062 | Mục vui: emoji, sticker tenant tự tải lên | 8.2 | | | Chưa làm | | |
| T-063 | GIF từ dịch vụ ngoài | 8.2 | | | Ngoài phạm vi | Đặc tả 11 | |
| T-064 | Ảnh và mô tả nhóm | 8.2 | | | Chưa làm | | |
| T-065 | Nhãn (tags) trong nhóm | 8.2 | | | Chưa làm | | |
| T-066 | Lưu trữ nhóm (chỉ đọc), khôi phục | 8.2 | | | Chưa làm | | |
| T-067 | Xoá nhóm (xoá mềm), khôi phục trong thời hạn | 8.2 | | | Chưa làm | | |

### 8.3 Kênh

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-070 | Kênh chuẩn | 8.3 | | | Chưa làm | | |
| T-071 | Kênh riêng tư (thành viên riêng, chủ kênh) | 8.3 | | | Chưa làm | | |
| T-072 | Kênh chia sẻ với người/nhóm khác (trong tenant) | 8.3 | | | Chưa làm | | |
| T-073 | Kênh chia sẻ liên tenant (Phase 11) | 8.3 | | | Chưa làm | | |
| T-074 | Kênh Chung mặc định, không xoá được | 8.3 | | | Chưa làm | | |
| T-075 | Ghim, ẩn, sắp xếp kênh | 8.3 | | | Chưa làm | | |
| T-076 | Đổi tên, xoá, khôi phục kênh | 8.3 | | | Chưa làm | | |
| T-077 | Bố cục bài đăng (tiêu đề + luồng trả lời) | 8.3 | | | Chưa làm | | |
| T-078 | Bố cục luồng (threads) | 8.3 | | | Chưa làm | | |
| T-079 | Thông báo (announcement) có ảnh nền tiêu đề | 8.3 | | | Chưa làm | | |
| T-080 | Đăng chéo một bài sang nhiều kênh | 8.3 | | | Chưa làm | | |
| T-081 | Điều tiết kênh (ai bắt đầu bài, ai trả lời, bot) | 8.3 | | | Chưa làm | | |
| T-082 | Theo dõi kênh/luồng; mức thông báo theo kênh | 8.3 | | | Chưa làm | | |
| T-083 | Tab kênh: Tệp | 8.3 | | | Chưa làm | | |
| T-084 | Tab kênh: Ghi chú (soạn chung) | 8.3 | | | Chưa làm | | |
| T-085 | Tab kênh: Trang web (danh sách miền được phép) | 8.3 | | | Chưa làm | | |
| T-086 | Tab kênh: ứng dụng | 8.3 | | | Chưa làm | | |
| T-087 | Địa chỉ email của kênh | 8.3 | | | Ngoài phạm vi | Đặc tả 8.3, 11: cần máy chủ thư, để khi tích hợp BlueMail | |
| T-088 | Họp kênh (chat của họp nằm trong kênh) | 8.3 | | | Chưa làm | | |

### 8.4 Hoạt động và thông báo

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-090 | Dòng hoạt động (nhắc tên, trả lời, cảm xúc, gọi nhỡ, mời họp, thêm vào nhóm, kênh theo dõi, ứng dụng) | 8.4 | | | Chưa làm | | |
| T-091 | Lọc hoạt động theo loại | 8.4 | | | Chưa làm | | |
| T-092 | Đánh dấu đã đọc từng mục / tất cả | 8.4 | | | Chưa làm | | |
| T-093 | Thông báo đẩy Web Push (trình duyệt, PWA) | 8.4 | | | Chưa làm | | |
| T-094 | Thông báo trên màn hình khi đang mở ứng dụng; âm báo bật/tắt | 8.4 | | | Chưa làm | | |
| T-095 | Giờ yên lặng (theo giờ/ngày) | 8.4 | | | Chưa làm | | |
| T-096 | Tắt thông báo theo chat/kênh | 8.4 | | | Chưa làm | | |
| T-097 | Thư tóm tắt hoạt động bị bỏ lỡ, tần suất tuỳ chọn | 8.4 | | | Chưa làm | | |

### 8.5 Trạng thái hiện diện

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-100 | Trạng thái chọn tay (Sẵn sàng, Bận, Không làm phiền, Sẽ quay lại, Đi vắng, Ẩn/ngoại tuyến) | 8.5 | | | Chưa làm | | |
| T-101 | Trạng thái tự động (Đang họp, Đang gọi, Đang trình bày, Không hoạt động sau N phút) | 8.5 | | | Chưa làm | | |
| T-102 | Đặt trạng thái có thời hạn | 8.5 | | | Chưa làm | | |
| T-103 | Lời nhắn trạng thái có hạn, "hiện khi người khác nhắn cho tôi" | 8.5 | | | Chưa làm | | |
| T-104 | Vắng mặt có thời gian và lời nhắn | 8.5 | | | Chưa làm | | |
| T-105 | Danh sách được phép làm phiền khi Không làm phiền | 8.5 | | | Chưa làm | | |
| T-106 | Gộp trạng thái nhiều thiết bị theo quy tắc ưu tiên | 8.5 | | | Chưa làm | | |

### 8.6 Tìm kiếm

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-110 | Ô tìm chung (`Ctrl+E`): tin, người, nhóm/kênh, tệp; gợi ý khi gõ | 8.6 | | | Chưa làm | | |
| T-111 | Bộ lọc (người gửi, thời gian, kênh, có tệp, nhắc tên tôi) | 8.6 | | | Chưa làm | | |
| T-112 | Mở kết quả tới tin trong ngữ cảnh | 8.6 | | | Chưa làm | | |
| T-113 | Chỉ trả kết quả người tìm có quyền xem | 8.6 | | | Chưa làm | | |
| T-114 | Lệnh gõ tắt trong ô tìm | 8.6 | | | Chưa làm | | |

### 8.7 Tệp

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-120 | Tải lên (tiếp tục khi đứt mạng), tải xuống | 8.7 | | | Chưa làm | | |
| T-121 | Xem trước ảnh, PDF, video, âm thanh, văn bản | 8.7 | | | Chưa làm | | |
| T-122 | Phiên bản, đổi tên, xoá, sao chép liên kết | 8.7 | | | Chưa làm | | |
| T-123 | Màn hình Tệp chung (gần đây, tôi đã chia sẻ, trong các nhóm) | 8.7 | | | Chưa làm | | |
| T-124 | Quét tệp: chữ ký byte, chặn loại nguy hiểm, giới hạn cỡ theo chính sách | 8.7 | | | Chưa làm | | |
| T-125 | Mở/sửa tài liệu Office trực tuyến | 8.7 | | | Ngoài phạm vi | Đặc tả 8.7, 11: để khi tích hợp BlueOffice/BlueDrive | |

### 9.1 Cuộc gọi

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-130 | Gọi thoại/video 1-1 từ chat | 9.1 | | | Chưa làm | | |
| T-131 | Gọi nhóm từ chat | 9.1 | | | Chưa làm | | |
| T-132 | Đổ chuông mọi thiết bị; nhận / từ chối / nhắn lại | 9.1 | | | Chưa làm | | |
| T-133 | Cuộc gọi nhỡ vào chat và dòng hoạt động | 9.1 | | | Chưa làm | | |
| T-134 | Thêm người vào cuộc gọi | 9.1 | | | Chưa làm | | |
| T-135 | Chuyển thoại sang video | 9.1 | | | Chưa làm | | |
| T-136 | Chia sẻ màn hình, chat trong cuộc gọi | 9.1 | | | Chưa làm | | |
| T-137 | Giữ máy (hold) có âm báo | 9.1 | | | Chưa làm | | |
| T-138 | Lịch sử cuộc gọi, quay số nhanh | 9.1 | | | Chưa làm | | |
| T-139 | PSTN, hộp thư thoại, chuyển cuộc gọi kiểu tổng đài | 9.1 | | | Ngoài phạm vi | Đặc tả 9.1, 11 | |

### 9.2 Lên lịch họp

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-140 | Tạo cuộc họp (tiêu đề, người dự bắt buộc/tuỳ chọn, trong/ngoài tổ chức, giờ, múi giờ, nội dung) | 9.2 | | | Chưa làm | | |
| T-141 | Họp lặp lại (RRULE) | 9.2 | | | Chưa làm | | |
| T-142 | Họp trong kênh | 9.2 | | | Chưa làm | | |
| T-143 | Lời mời iCalendar qua email; cập nhật và huỷ đúng `SEQUENCE` | 9.2 | | | Chưa làm | | |
| T-144 | Trả lời lời mời (qua liên kết trong thư) | 9.2 | | | Chưa làm | | |
| T-145 | Màn hình Lịch họp ngày/tuần/tháng, tham gia một nút | 9.2 | | | Chưa làm | | |
| T-146 | Họp ngay trong kênh hoặc chat | 9.2 | | | Chưa làm | | |
| T-147 | Tham gia bằng liên kết hoặc mã cuộc họp + mật mã | 9.2 | | | Chưa làm | | |
| T-148 | Tuỳ chọn cuộc họp (bỏ qua phòng chờ, ai trình bày, mic/camera người dự, chat, cảm xúc, tự ghi, khoá, ẩn tên, thông báo vào/ra — đủ danh sách của Teams) | 9.2 | | | Chưa làm | | |

### 9.3 Trong cuộc họp

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-150 | Màn hình trước khi vào (thiết bị, xem trước, mic/camera, tên khách) | 9.3 | | | Chưa làm | | |
| T-151 | Phòng chờ: cho vào từng người / tất cả, từ chối | 9.3 | | | Chưa làm | | |
| T-152 | Vai trò tổ chức / đồng tổ chức / trình bày / người dự; đổi vai trò trong họp | 9.3 | | | Chưa làm | | |
| T-153 | Tắt/bật mic `Ctrl+Shift+M`, camera `Ctrl+Shift+O` | 9.3 | | | Chưa làm | | |
| T-154 | Giơ tay `Ctrl+Shift+K`, thứ tự giơ tay | 9.3 | | | Chưa làm | | |
| T-155 | Cảm xúc bay trên màn hình | 9.3 | | | Chưa làm | | |
| T-156 | Chat trong họp (lưu sau họp) | 9.3 | | | Chưa làm | | |
| T-157 | Danh sách người tham gia (mic/camera/tay) | 9.3 | | | Chưa làm | | |
| T-158 | Chia sẻ màn hình / cửa sổ / tab, có âm thanh; viền và nút dừng; người xem phóng to | 9.3 | | | Chưa làm | | |
| T-159 | Bố cục lưới nhiều trang | 9.3 | | | Chưa làm | | |
| T-160 | Bố cục người nói, tập trung nội dung | 9.3 | | | Chưa làm | | |
| T-161 | Ghim cho riêng mình | 9.3 | | | Chưa làm | | |
| T-162 | Nổi bật (spotlight) cho mọi người | 9.3 | | | Chưa làm | | |
| T-163 | Ẩn video của mình; ẩn người không bật camera | 9.3 | | | Chưa làm | | |
| T-164 | Người tổ chức: tắt mic tất cả, khoá mic/camera người dự (máy chủ không chuyển gói) | 9.3 | | | Chưa làm | | |
| T-165 | Đưa người ra, khoá cuộc họp, kết thúc cho mọi người | 9.3 | | | Chưa làm | | |
| T-166 | Chỉ báo chất lượng mạng | 9.3 | | | Chưa làm | | |
| T-167 | Báo "mic đang tắt" khi đang nói; cảnh báo thiết bị lỗi | 9.3 | | | Chưa làm | | |
| T-168 | Phòng nhóm: tạo tự động/thủ công | 9.3 | | | Chưa làm | | |
| T-169 | Phòng nhóm: hẹn giờ, thông báo tới mọi phòng | 9.3 | | | Chưa làm | | |
| T-170 | Phòng nhóm: chuyển người, mở/đóng, người tổ chức vào từng phòng | 9.3 | | | Chưa làm | | |
| T-171 | Thăm dò trong họp | 9.3 | | | Chưa làm | | |
| T-172 | Hỏi & Đáp có kiểm duyệt, bình chọn câu hỏi | 9.3 | | | Chưa làm | | |
| T-173 | Ghi chú cuộc họp (soạn chung) | 9.3 | | | Chưa làm | | |
| T-174 | Ghi hình, biểu ngữ "đang ghi" | 9.3 | | | Chưa làm | | |
| T-175 | Trình phát bản ghi nhiều luồng | 9.3 | | | Chưa làm | | |
| T-176 | Báo cáo điểm danh, tải CSV | 9.3 | | | Chưa làm | | |
| T-177 | Tổng kết sau họp (bản ghi, chat, ghi chú, điểm danh) | 9.3 | | | Chưa làm | | |
| T-178 | Khách không tài khoản vào bằng tên, qua phòng chờ | 9.3 | | | Chưa làm | | |
| T-179 | Kết nối lại trong 2 phút giữ danh tính | 9.3 | | | Chưa làm | | |

### 9.4 Webinar và town hall

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-185 | Webinar: trang đăng ký (trường tuỳ chỉnh, duyệt, giới hạn số lượng) | 9.4 | | | Chưa làm | | |
| T-186 | Webinar: email xác nhận và nhắc | 9.4 | | | Chưa làm | | |
| T-187 | Webinar: người trình bày / người dự tách biệt; người dự mặc định không mic/camera | 9.4 | | | Chưa làm | | |
| T-188 | Webinar: báo cáo người đăng ký và người dự | 9.4 | | | Chưa làm | | |
| T-189 | Town hall: người dự chỉ xem, quy mô lớn, độ trễ thấp | 9.4 | | | Chưa làm | | |
| T-190 | Town hall: Hỏi & Đáp có kiểm duyệt | 9.4 | | | Chưa làm | | |

### Giao diện

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-200 | Phím tắt chính; bảng phím tắt `Ctrl+.` | 7.1 | | | Chưa làm | | |
| T-201 | Chủ đề sáng/tối/tương phản cao; mật độ; cỡ chữ | 7.1 | | | Chưa làm | | |
| T-202 | Chạy ở 1366×768 và 375×812 (PWA) | 7.1 | | | Chưa làm | | |

### 10.1 Ứng dụng và tích hợp

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-210 | Webhook đến (văn bản hoặc thẻ) | 10.1 | | | Chưa làm | | |
| T-211 | Webhook đi (khi @nhắc, ký HMAC, trả lời đăng lại) | 10.1 | | | Chưa làm | | |
| T-212 | Bot API: sự kiện tin nhắn/cài đặt, gửi tin và thẻ, cập nhật thẻ, nhắn chủ động | 10.1 | | | Chưa làm | | |
| T-213 | Tab ứng dụng có SSO, danh sách miền hợp lệ | 10.1 | | | Chưa làm | | |
| T-214 | Quản lý ứng dụng trong tenant (duyệt, chặn, ghim) | 10.1 | | | Chưa làm | | |
| T-215 | API công khai + đăng ký nhận thay đổi | 10.1 | | | Chưa làm | | |

### 10.2 Trung tâm quản trị

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-230 | Quản lý nhóm (thành viên, kênh, chủ, lưu trữ, xoá/khôi phục, nhóm không có chủ) | 10.2 | | | Chưa làm | | |
| T-231 | Chính sách nhắn tin | 10.2 | | | Chưa làm | | |
| T-232 | Chính sách họp | 10.2 | | | Chưa làm | | |
| T-233 | Chính sách cuộc gọi | 10.2 | | | Chưa làm | | |
| T-234 | Chính sách webinar / town hall | 10.2 | | | Chưa làm | | |
| T-235 | Chính sách nhóm (ai tạo nhóm, kênh riêng/chia sẻ) | 10.2 | | | Chưa làm | | |
| T-236 | Chính sách khách | 10.2 | | | Chưa làm | | |
| T-237 | Chính sách ứng dụng (quyền, thiết lập) | 10.2 | | | Chưa làm | | |
| T-238 | Gán chính sách theo người/nhóm, mặc định tổ chức | 10.2 | | | Chưa làm | | |
| T-239 | Truy cập bên ngoài (tất cả / cho phép / chặn) | 10.2 | | | Chưa làm | | |
| T-240 | Khách mời | 10.2 | | | Chưa làm | | |
| T-241 | Báo cáo sử dụng, xuất Excel/CSV | 10.2 | | | Chưa làm | | |
| T-242 | Bảng chất lượng cuộc gọi | 10.2 | | | Chưa làm | | |
| T-243 | Nhật ký kiểm toán | 10.2 | | | Chưa làm | | |

### 10.3 Tuân thủ

| Mã | Chức năng | Mục | Hành vi của Teams | Nguồn | BlueChat | Khác biệt | Bằng chứng |
|---|---|---|---|---|---|---|---|
| T-250 | Lưu giữ tin nhắn, tệp, bản ghi | 10.3 | | | Chưa làm | | |
| T-251 | Giữ pháp lý theo người/nhóm (giữ mọi phiên bản) | 10.3 | | | Chưa làm | | |
| T-252 | eDiscovery: tìm và xuất kèm kê khai SHA-256 | 10.3 | | | Chưa làm | | |
| T-253 | Rào thông tin | 10.3 | | | Chưa làm | | |
| T-254 | Ngăn rò rỉ dữ liệu theo mẫu | 10.3 | | | Chưa làm | | |

### 11 Ngoài phạm vi

| Mã | Chức năng | BlueChat | Lý do |
|---|---|---|---|
| T-300 | Ứng dụng di động gốc | Ngoài phạm vi | Cần bộ WebRTC gốc; bản này dùng PWA |
| T-301 | Phụ đề trực tiếp, chép lời, dịch, trợ lý AI | Ngoài phạm vi | Đặc tả 11 |
| T-302 | Nền ảo / làm mờ nền | Ngoài phạm vi | Cần mô hình học máy |
| T-303 | Bảng trắng | Ngoài phạm vi | Đặc tả 11 |
| T-304 | PowerPoint Live | Ngoài phạm vi | Đặc tả 11 |
| T-305 | Thành phần cộng tác kiểu Loop | Ngoài phạm vi | Đặc tả 11 |
| T-306 | Chế độ Together | Ngoài phạm vi | Đặc tả 11 |
| T-307 | Planner / Approvals / Shifts | Ngoài phạm vi | Đặc tả 11 |
| T-308 | Liên thông Teams / Slack / Skype | Ngoài phạm vi | Đặc tả 11 |
| T-309 | Mã hoá đầu-cuối | Ngoài phạm vi | Giai đoạn sau với SFrame (RFC 9605) |
| T-310 | Bản ghi dạng một video lưới | Ngoài phạm vi | Cần giải mã và mã hoá lại (đặc tả 6.7) |
