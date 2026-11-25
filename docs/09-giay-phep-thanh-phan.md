# 09 — Giấy phép thành phần (Component licences)

Mọi gói NuGet/npm và image chạy thật phải có trong bảng này trước khi dùng.

## Thư viện hệ thống gọi qua P/Invoke

| Thành phần | Phiên bản | Giấy phép | Dùng cho |
|---|---|---|---|
| OpenSSL (libssl, libcrypto) của hệ điều hành | ≥ 3.0 (đã thử 3.0.13, 3.2.3) | Apache-2.0 | Chỉ bắt tay DTLS và xuất khoá SRTP (docs/00 Z3). Bản cài Windows phải kèm DLL OpenSSL 3.x chính thức |

## Gói .NET

| Gói | Phiên bản | Giấy phép | Dùng ở |
|---|---|---|---|
| Microsoft.NET.Test.Sdk | 18.12.0 | MIT | kiểm thử |
| xunit, xunit.runner.visualstudio | 2.9.3 / 3.1.5 | Apache-2.0 | kiểm thử |
| FluentAssertions | 7.2.0 | Apache-2.0 (bản 7.x; không nâng lên 8 thương mại) | kiểm thử |

## Chỉ dùng trong kiểm thử (không có trong image chạy thật)

| Thành phần | Phiên bản | Giấy phép |
|---|---|---|
| Playwright (npm) | 1.65.1 | Apache-2.0 |
| Pion WebRTC (Go) | v4.2.29 | MIT |
| aiortc (Python) | 1.16.0 | BSD-3-Clause |
| coturn (Debian bookworm) | 4.6.1 | BSD-3-Clause |
| tshark / Wireshark (Debian bookworm) | 4.0.17 | GPL-2.0 |
| iproute2 (tc netem) | 6.1.0 | GPL-2.0 |
