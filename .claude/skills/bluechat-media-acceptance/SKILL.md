---
name: bluechat-media-acceptance
description: Use this skill when running BlueChat media acceptance or interop tests — DtlsProbe, the MediaLoopback browser loopback (Chromium/Firefox via Playwright, UDP / ICE-TCP / ICE restart), Linux-container media node runs, the interop rigs (Pion, aiortc, coturn turnutils, libsrtp, tshark, tc netem), or hunting flaky media tests with ONLY/REPEAT. Triggers: "chạy vòng lặp media", "thử với trình duyệt thật", "đối chứng Pion/coturn/libsrtp", "loopback test", "run-z4", "run-turn", "flaky ICE-TCP".
---

# BlueChat media acceptance

## Khi nào dùng
- Sau khi sửa bất kỳ thư viện media (`src/BlueChat.Media.*`, `src/BlueChat.Turn`) — chạy lại phép thử với trình duyệt thật và cài đặt độc lập trước khi commit (đặc tả bài học 5, 15).
- Khi một phép thử media chập chờn: tìm nguyên nhân bằng bộ đếm, không bỏ qua.

## Quy trình

1. **Unit + vector + đối chứng đã ghi sẵn** (nhanh, không cần mạng):
   `BC_OPENSSL_DIR="C:/Program Files/Git/mingw64/bin" dotnet test BlueChat.slnx`
2. **Vòng lặp trình duyệt** (nghiệm thu Phase 5) — dùng script, nó tự dừng nút cũ, build, chạy, đợi, thử, dừng:
   - nút chạy thẳng trên Windows: `sh .claude/skills/bluechat-media-acceptance/scripts/loopback.sh host`
   - nút trong container Linux (OpenSSL 3.0, giống chạy thật): `sh .claude/skills/bluechat-media-acceptance/scripts/loopback.sh linux`
   - lặp một kịch bản: `ONLY=firefox:tcp REPEAT=25 sh …/loopback.sh host` (giá trị ONLY: `chromium`, `firefox`, `<trình duyệt>:udp|tcp|udp-ice-restart`, phân cách bằng dấu phẩy)
   - kết quả từng lượt: `tests/phase0/out/loopback-*.json`; lượt không kết nối có `pairs` (cặp ứng viên phía trình duyệt) và `node` (bộ đếm máy chủ).
3. **Rig đối chứng độc lập** (Docker):
   - Pion + aiortc + tshark + netem với nguyên mẫu DTLS: `docker compose -f tests/interop/compose.yml up -d --build && sh tests/interop/run-z4.sh`, dọn bằng `docker compose -f tests/interop/compose.yml down`.
   - TURN với coturn + Pion: `docker compose -f tests/interop/turn-compose.yml up -d && sh tests/interop/run-turn.sh`, dọn bằng `… down`.
   - SRTP với libsrtp: vector đã nằm trong `tests/BlueChat.Media.Srtp.Tests/Interop/`; sinh lại theo README của thư viện đó.
4. **Ghi kết quả**: thêm/cập nhật dòng ở `docs/06-kich-ban-kiem-thu.md`, bảng bằng chứng ở `docs/07-giao-thuc-media.md`; lỗi (kể cả của mình hay của môi trường) vào `docs/08-so-loi.md`.

## Đọc chẩn đoán
- `GET http://127.0.0.1:5299/node`: `tcpAccepted`, `tcpOpen`, `tcpFrames`, `tcpClosedByProtocolError`, `stunUnrouted`.
  - Trình duyệt hỏng nhưng `tcpAccepted` không tăng → kết nối không tới máy chủ (mạng/proxy), không phải mã.
  - `stunUnrouted` tăng → kiểm tra có tới nhưng không khớp ufrag/mật khẩu → lỗi ở SDP answer hoặc ICE.
- `GET /status?session=<id>`: `state`, `ice`, `srtp`, `received`, `srtpRejected` (> 0 là lỗi khoá SRTP), `echoed`, `pliForwarded`, `nackForwarded`.

## Lưu ý / Pitfalls
- **Docker Desktop làm mất thỉnh thoảng một lệnh kết nối TCP** qua cổng chuyển tiếp; Firefox không thử lại ICE-TCP → hỏng ~1/15 lượt khi nút chạy trong container (BUG-006). So với nút chạy thẳng trên máy trước khi nghi mã.
- **Git Bash đổi đường dẫn tuyệt đối** truyền vào container: đặt `MSYS_NO_PATHCONV=1` **chỉ cho lệnh docker**. Đặt toàn
  cục thì chương trình Windows gốc (`curl.exe -o /dev/null`) nhận nguyên `/dev/null` và báo lỗi dù máy chủ đã trả lời.
  Khi mount thư mục vào Docker Desktop dùng đường dẫn Windows (`pwd -W` → `D:/…`), không dùng `/d/…`.
- **`curl --retry` không đáng tin để đợi máy chủ trên Windows**: dùng vòng lặp mỗi lần một lệnh curl (script đã làm).
- **`jq`/`grep` ở cuối ống `docker compose exec`** chạy trên máy host, không trong container.
- **tshark không chấm được thân SRTP/SRTCP** (đã mã hoá): chỉ chấm STUN/DTLS và gói của chính mình.
- **`turnutils_uclient` chọn số kênh ngẫu nhiên** 0x4000–0x7FFE: 400 (ngoài dải RFC 8656) và chuyển tiếp sạch (trong dải) đều đúng (BUG-007).
- **Firefox cần secure context** cho `getUserMedia`: dùng trang trên `http://127.0.0.1`, không dùng `about:blank`.
- **Firefox max-bundle** gửi `m=video 0` + `a=bundle-only` — phải được trả lời là chấp nhận (BUG-005).
- **Tiến trình `DtlsProbe`/`MediaLoopback` còn chạy sẽ khoá `bin/`**: dừng trước khi build (script đã làm).
- Ảnh chụp/kết quả chứa dữ liệu thật không đưa vào kho mã; thư mục `out/` đã nằm trong `.gitignore`.
