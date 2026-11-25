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
| Pion rtp / rtcp / interceptor (Go) | v1.10.6 / v1.2.20 / v0.1.51 | MIT |
| Pion TURN (Go) | v4.1.4 | MIT |
| pylibsrtp (libsrtp) | 1.1.0 | BSD-3-Clause |
| Playwright image (WebKit trên Linux) | `mcr.microsoft.com/playwright:v1.65.1-noble` | Apache-2.0 |
| aiortc (Python) | 1.16.0 | BSD-3-Clause |
| coturn (Debian bookworm) | 4.6.1 | BSD-3-Clause |
| tshark / Wireshark (Debian bookworm) | 4.0.17 | GPL-2.0 |
| iproute2 (tc netem) | 6.1.0 | GPL-2.0 |

## Gói .NET của Phase 1 (máy chủ)

| Gói | Phiên bản | Giấy phép | Dùng ở |
|---|---|---|---|
| Microsoft.EntityFrameworkCore, Microsoft.EntityFrameworkCore.Relational, Microsoft.EntityFrameworkCore.Design | 10.0.15 | MIT | ORM |
| Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.4 | PostgreSQL License | Trình điều khiển PostgreSQL |
| EFCore.NamingConventions | 10.0.1 | Apache-2.0 | snake_case |
| MediatR | 12.5.0 | Apache-2.0 (bản 12.x; không nâng ≥ 13 thương mại) | CQRS |
| FluentValidation, FluentValidation.DependencyInjectionExtensions | 12.2.0 | Apache-2.0 | Kiểm dữ liệu vào |
| Serilog.AspNetCore, Serilog.Sinks.File | 10.0.0 / 7.0.0 | Apache-2.0 | Log JSON |
| StackExchange.Redis | 3.4.35 | MIT | Máy khách Valkey |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.15 | MIT | Kiểm token BlueID |
| Microsoft.AspNetCore.SignalR.StackExchangeRedis | 10.0.15 | MIT | Backplane SignalR qua Valkey |
| Swashbuckle.AspNetCore | 10.3.1 | MIT | OpenAPI (chỉ môi trường phát triển) |
| Testcontainers, Testcontainers.PostgreSql | 4.17.0 | MIT | kiểm thử (image `postgres:16.16-alpine`, `postgres:16-alpine`, `valkey/valkey:8.1.12-alpine3.24`) |
| Microsoft.AspNetCore.Mvc.Testing, Microsoft.AspNetCore.SignalR.Client | 10.0.15 | MIT | kiểm thử |

## Image chạy thật

| Thành phần | Phiên bản | Giấy phép | Lý do |
|---|---|---|---|
| PostgreSQL (`postgres:16.16-alpine`) | 16.16 | PostgreSQL License | CSDL |
| Valkey (`valkey/valkey:8.1.12-alpine3.24`) | 8.1.12 | BSD-3-Clause | Đệm, sự kiện, backplane |
| Garage (`dxflrs/garage:v2.5.0`) | 2.5.0 | AGPL-3.0 — chạy nguyên bản, dịch vụ riêng, không sửa (docs/00 Z5) | Kho S3 |
| Caddy (`caddy:2.10.2-alpine`) | 2.10.2 | Apache-2.0 | HTTPS |
