# Üçüncü tərəf asılılıqlar

Bu fayl Bölmə 2.0 (Kitabxana siyasəti) tələbidir: **hər asılılıq** ad, versiya, lisenziya,
mənbə və səbəblə burada qeyd olunur.

**Qaydalar**
- Yalnız Bölmə 2.0-dakı təsdiqlənmiş siyahıdan paket əlavə olunur.
- Versiyalar pinlənir (`Directory.Packages.props`, `package.json`, `pubspec.yaml`).
- **MediatR 12.x, MassTransit 8.x, FluentAssertions 7.x** — bunlar həmin kitabxanaların son
  **pulsuz** major versiyalarıdır. Major versiya **heç vaxt** yüksəldilmir; `tools/license-check`
  bunu bloklayır.
- İcazəli lisenziyalar: MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, MPL-2.0, ISC.
  Adla istisnalar: `tools/license-check/allowlist.json`.

---

## Backend və paylaşılan .NET kitabxanaları (NuGet)

| Paket | Versiya | Lisenziya | Mənbə | Niyə lazımdır |
|---|---|---|---|---|
| `Microsoft.AspNetCore.*` (JwtBearer, SignalR, DataProtection, HealthChecks, Identity.Core, Caching.StackExchangeRedis) | 8.0.31 | MIT | Microsoft | Platforma: auth, real-time, keş, sağlamlıq, şifrə hash-i |
| `Microsoft.Extensions.Hosting(.WindowsServices/.Systemd/.Http)` | 8.0.1 | MIT | Microsoft | Host, Agent servisi, `HttpClient` fabriki |
| `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | 8.0.31 | MIT | Microsoft | SignalR backplane (çoxlu API instansiyası) |
| `MongoDB.Driver` | 3.12.0 | Apache-2.0 | MongoDB Inc. (rəsmi) | Verilənlər bazası drayveri; MIT alternativi yoxdur |
| `StackExchange.Redis` | 2.13.17 | MIT | StackExchange | Keş, lock, sessiya, SignalR backplane |
| `AWSSDK.S3` | 4.0.103.3 | Apache-2.0 | Amazon (rəsmi) | S3 SigV4 imzalama (MinIO / Scality) |
| `MailKit` | 4.18.0 | MIT | Jeffrey Stedfast | SMTP; daxili `SmtpClient` köhnəlmiş hesab olunur |
| `MediatR` | **12.5.0** | Apache-2.0 | Jimmy Bogard | CQRS / mediator. **v13+ kommersiyadır — yüksəldilmir** |
| `MassTransit`, `MassTransit.RabbitMQ`, `MassTransit.MongoDb` | **8.5.10** | Apache-2.0 | MassTransit | Mesajlaşma, transactional outbox / inbox. **v9+ kommersiyadır — yüksəldilmir** |
| `Hangfire.Core`, `Hangfire.AspNetCore`, `Hangfire.NetCore` | 1.8.25 | **LGPL-3.0** | Hangfire OÜ | Background / planlanmış işlər, dashboard. Hangfire **çoxlisenziyalıdır**: LGPL-3.0 (pulsuz) və ya pullu kommersiya lisenziyası. Biz pulsuz LGPL-3.0 variantını seçirik və paketləri **dəyişdirmədən**, şəxsi fork olmadan istifadə edirik — bu halda abunə tələb olunmur. Hangfire Pro paketləri istifadə edilmir. `allowlist.json`-da adla istisna |
| `Hangfire.Mongo` | 1.16.3 | MIT | sergeyzwezdin | Hangfire üçün Mongo storage. Paketin nuspec-ində lisenziya elan olunmur; MIT layihə deposundakı `LICENSE` faylı ilə təsdiqləndi və `allowlist.json` → `packageLicenses`-ə yazıldı |
| `QuestPDF` | 2026.9.0 | **Community** | QuestPDF | Qəbz və hesabat PDF-ləri. **Şərt:** illik gəliri 1M USD-dən az olan şirkətlər üçün pulsuzdur. Bu şərt pozulduqda kommersiya lisenziyası alınmalıdır — `allowlist.json`-da adla istisna |
| `PDFsharp` | 6.2.4 | MIT | empira (rəsmi) | PDF səhifə əməliyyatları, tərtibat PDF-i |
| `SkiaSharp`, `SkiaSharp.NativeAssets.Linux` | 2.88.9 | MIT | Microsoft | Şəkil emalı, şəkil → PDF, preview. Avalonia 11.3 ilə eyni versiya |
| `QRCoder` | 1.8.0 | MIT | Raffael Herrmann | QR generasiya |
| `Swashbuckle.AspNetCore` | 7.3.2 | MIT | domaindrivendev | Swagger UI — **yalnız Development mühitində** qeydiyyatdan keçir |
| `System.Management` | 8.0.0 | MIT | Microsoft | Windows WMI (kiosk telemetriyası) |

## Kiosk (Avalonia)

| Paket | Versiya | Lisenziya | Mənbə | Niyə lazımdır |
|---|---|---|---|---|
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Diagnostics` | 11.3.22 | MIT | AvaloniaUI | Kiosk UI (Windows + Linux eyni kod bazası) |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | Microsoft | MVVM |
| `Microsoft.AspNetCore.SignalR.Client` | 8.0.31 | MIT | Microsoft | Real-time |
| `Lextm.SharpSnmpLib` | 12.5.7 | MIT | Lex Li | Şəbəkə printerlərinin SNMP telemetriyası |

## Test

| Paket | Versiya | Lisenziya | Mənbə | Niyə lazımdır |
|---|---|---|---|---|
| `xunit` | 2.9.3 | Apache-2.0 | .NET Foundation | Test framework |
| `xunit.runner.visualstudio` | 2.8.2 | MIT | .NET Foundation | Test runner |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | Microsoft | Test host |
| `FluentAssertions` | **7.2.2** | Apache-2.0 | Dennis Doomen | Assertion-lar. **v8+ kommersiyadır — yüksəldilmir** |
| `coverlet.collector` | 6.0.4 | MIT | coverlet | Coverage (Domain üçün ≥ 80% tələbi) |
| `Testcontainers(.MongoDb/.Redis/.RabbitMq/.Minio)` | 4.15.0 | MIT | Testcontainers | Integration testlər |
| `Microsoft.AspNetCore.Mvc.Testing` | 8.0.31 | MIT | Microsoft | API integration testləri |
| `Avalonia.Headless.XUnit` | 11.3.22 | MIT | AvaloniaUI | Kiosk ViewModel / UI testləri |

> **Mock kitabxanası yoxdur** (Bölmə 11) — bütün test ikiliyi əl ilə yazılmış fake-lərdir.

## Mobile (pub) və Admin (npm)

Bu asılılıqlar müvafiq addımlarda (`mobile/pubspec.yaml`, `admin/package.json`) əlavə olunanda
bu fayla yazılır.

---

## Öz kodumuzla yazılanlar (kitabxana əvəzinə)

Bölmə 2.0 qaydası 1-ə görə aşağıdakılar üçün paket **əlavə edilmir**:

| Ehtiyac | Həll |
|---|---|
| Secret idarəsi klienti | `NeoPrint.Infrastructure/SecretStore` — `HttpClient` ilə OpenBao AppRole + KV v2 |
| Health check-lər (Mongo, Redis, RabbitMQ, OpenBao) | Öz `IHealthCheck` implementasiyalarımız |
| Validasiya, mapping | DataAnnotations + öz validator-lar, əl ilə mapping |
| Fayl şifrələmə | BCL `AesGcm` (`NeoPrint.Crypto`) |
| Push (FCM) | FCM HTTP v1 — `HttpClient` + service account JWT (BCL `RSA`) |
| Lisenziya yoxlaması | `tools/license-check` (Node standart kitabxanası) |
| Test mock | Əl ilə yazılmış fake-lər |

---

## Xarici alətlər (ayrıca proses / server, koda qoşulmur)

| Alət | Lisenziya | İstifadə |
|---|---|---|
| MongoDB Community Server 7 | SSPL | Verilənlər bazası (öz serverimizdə pulsuz) |
| Redis 7.2.x | BSD-3 | Keş, lock, SignalR backplane. 7.2 BSD lisenziyalı son xəttdir |
| RabbitMQ 3.13 / 4.x | MPL-2.0 | Mesajlaşma |
| MinIO | AGPL-3.0 | Obyekt saxlama (dəyişdirilmədən, server kimi) |
| Scality Zenko CloudServer | Apache-2.0 | Development/test-də Scality S3 əvəzi |
| OpenBao | MPL-2.0 | Secret idarəsi (Linux Foundation) |
| LibreOffice (headless) | MPL-2.0 | Office → PDF |
| Ghostscript (+ GhostPCL, GhostXPS) | AGPL-3.0 | PS / EPS / PCL / PRN / XPS → PDF (ayrıca proses, dəyişdirilmədən) |
| CUPS | Apache-2.0 | Linux çap |
| Mailpit | MIT | Development-də email |

---

## CC-BY ilə lisenziyalı məlumat dəstləri (atribusiya)

Hər ikisi yalnız build / lint zamanı oxunur, brauzer bundle-ına düşmür.

| Paket | Lisenziya | Müəllif / mənbə | İstifadə |
|---|---|---|---|
| `caniuse-lite` | CC-BY-4.0 | "caniuse.com" — Alexis Deveria və töhfə verənlər | `browserslist` üçün brauzer dəstək məlumatı (Angular build) |
| `spdx-exceptions` | CC-BY-3.0 | Linux Foundation (SPDX Workgroup) | SPDX lisenziya istisna identifikatorlarının rəsmi siyahısı (lint alətləri) |
