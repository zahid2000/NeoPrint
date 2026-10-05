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
| `AWSSDK.S3` | 4.0.103.3 | Apache-2.0 | Amazon (rəsmi) | S3 SigV4 imzalama (istənilən S3-uyğun provayder) |
| `MailKit` | 4.18.0 | MIT | Jeffrey Stedfast | SMTP; daxili `SmtpClient` köhnəlmiş hesab olunur |
| `MediatR` | **12.5.0** | Apache-2.0 | Jimmy Bogard | CQRS / mediator. **v13+ kommersiyadır — yüksəldilmir** |
| `MassTransit`, `MassTransit.RabbitMQ`, `MassTransit.MongoDb` | **8.5.10** | Apache-2.0 | MassTransit | Mesajlaşma, transactional outbox / inbox. **v9+ kommersiyadır — yüksəldilmir** |
| `Hangfire.Core`, `Hangfire.AspNetCore`, `Hangfire.NetCore` | 1.8.25 | **LGPL-3.0** | Hangfire OÜ | Background / planlanmış işlər, dashboard. Hangfire **çoxlisenziyalıdır**: LGPL-3.0 (pulsuz) və ya pullu kommersiya lisenziyası. Biz pulsuz LGPL-3.0 variantını seçirik və paketləri **dəyişdirmədən**, şəxsi fork olmadan istifadə edirik — bu halda abunə tələb olunmur. Hangfire Pro paketləri istifadə edilmir. `allowlist.json`-da adla istisna |
| `Hangfire.Mongo` | 1.16.3 | MIT | sergeyzwezdin | Hangfire üçün Mongo storage. Paketin nuspec-ində lisenziya elan olunmur; MIT layihə deposundakı `LICENSE` faylı ilə təsdiqləndi və `allowlist.json` → `packageLicenses`-ə yazıldı |
| `QuestPDF` | 2026.9.0 | **Community** | QuestPDF | Qəbz və hesabat PDF-ləri. **Şərt:** illik gəliri 1M USD-dən az olan şirkətlər üçün pulsuzdur. Bu şərt pozulduqda kommersiya lisenziyası alınmalıdır — `allowlist.json`-da adla istisna |
| `PDFsharp` | 6.2.4 | MIT | empira (rəsmi) | PDF səhifə əməliyyatları, tərtibat PDF-i (`NeoPrint.Layout` — backend və kiosk eyni renderer, Addım 6) |
| `SkiaSharp`, `SkiaSharp.NativeAssets.Linux` | 2.88.9 | MIT | Microsoft | Şəkil emalı, şəkil → PDF, preview; `NeoPrint.Layout`-da şəkillərin 300 DPI-a endirilməsi və EXIF oriyentasiyası. Avalonia 11.3 ilə eyni versiya |
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
| `Testcontainers(.MongoDb/.Redis/.RabbitMq)` | 4.15.0 | MIT | Testcontainers | Integration testlər. Obyekt saxlaması üçün modul yoxdur: RustFS ümumi `ContainerBuilder` ilə qaldırılır. `Testcontainers.Minio` istinadı çıxarılıb (image-lər anonim çəkilmir) |
| `Microsoft.AspNetCore.Mvc.Testing` | 8.0.31 | MIT | Microsoft | API integration testləri |
| `Avalonia.Headless.XUnit` | 11.3.22 | MIT | AvaloniaUI | Kiosk ViewModel / UI testləri |

> **Mock kitabxanası yoxdur** (Bölmə 11) — bütün test ikiliyi əl ilə yazılmış fake-lərdir.

## Mobile (pub)

Birbaşa asılılıqlar (`mobile/pubspec.yaml`), hamısı plan 2.0-dakı siyahıdan və pinlidir. Tranzitiv
asılılıqları `tools/license-check` yoxlayır. Qeyd: `file_picker`-in Linux implementasiyası `dbus`
(Canonical, **MPL-2.0**) gətirir — plan 2.0-da icazəlidir; alətin onu AGPL kimi təsnif etməsi baq idi və
düzəldildi (`DECISIONS.md`).

| Paket | Versiya | Lisenziya | İstifadə |
|---|---|---|---|
| `flutter_riverpod` | 2.6.1 | MIT | State management |
| `go_router` | 14.6.2 | BSD-3-Clause (Flutter komandası) | Naviqasiya, deep link-lər |
| `dio` | 5.7.0 | MIT | HTTP, token yeniləmə, yükləmə proqresi |
| `web_socket_channel` | 3.0.1 | BSD-3-Clause (Dart komandası) | Öz SignalR klientimizin nəqliyyatı |
| `intl` | 0.20.3 | BSD-3-Clause (Dart komandası) | Lokalizasiya |
| `file_picker` | 13.1.0 | MIT | Fayllarım: yüklənəcək faylın seçimi (Addım 2) |
| `camera` | 0.12.1 | BSD-3-Clause (Flutter komandası) | Fayllarım: şəkil çəkib yükləmə (Addım 2); kioskun ödəniş QR-ını oxumaq üçün kamera axını (Addım 4) |
| `google_mlkit_barcode_scanning` | 0.16.1 (+ `google_mlkit_commons` 0.13.0) | MIT | Kioskun ödəniş QR-ını telefonun özündə oxumaq — Google ML Kit, şəbəkəsiz (Addım 4, plan Bölmə 2.0). Yalnız Android və iOS; web-də ödəniş `neoprint://pay/{id}` linki ilə açılır |
| `image` | 4.10.1 (+ tranzitiv `archive`, `crypto`, `posix` — MIT / BSD-3) | MIT | Skanın səhifələri skanerdən sonra: kəsmə, fırlatma, filtr, parlaqlıq və real ölçü üçün 300 DPI JPEG (Addım 6, plan Bölmə 2.0, 2.11). Saf Dart, fon isolate-də |
| `flutter_map` | 8.3.2 | BSD-3-Clause | Kiosklar xəritəsi — OpenStreetMap, Google Maps açarı / ödənişi yoxdur (Addım 5, plan 2.0 istisnası). Tile ünvanı `NEOPRINT_MAP_TILE_URL` ilə; ictimai OSM serveri yalnız development-də (T-061) |
| `latlong2` | 0.10.1 | Apache-2.0 | `flutter_map` API-sinin koordinat tipi (`LatLng`); flutter_map-in öz asılılığıdır, kodda birbaşa istifadə üçün pinlənib (2026-10-02 təsdiqlənib) |
| `geolocator` | 14.1.1 | MIT | "Yaxınımda" — istifadəçinin yeri (Addım 5); Android / iOS / web implementasiyaları Baseflow-un öz paketləridir |
| `firebase_core` | 4.15.0 | BSD-3-Clause (Google, FlutterFire) | Push üçün Firebase-in işə salınması (Addım 5, T-146, plan 2.0). Konfiqurasiya `lib/firebase_options.dart`-dadır (public identifikatorlar); Gradle `google-services` plagini istifadə olunmur |
| `firebase_messaging` | 16.7.0 | BSD-3-Clause (Google, FlutterFire) | Push qəbulu: FCM token, ön planda gələn mesaj, toxunulan push (Addım 5, T-146). Göndərmə backend-in öz FCM HTTP v1 klientindədir — FirebaseAdmin yoxdur |
| `flutter_lints` | 5.0.0 | BSD-3-Clause | Lint qaydaları (dev) |

**Firebase JS SDK 12.19.0** (Apache-2.0, Google) — paket deyil, mobil tətbiqin **web build-i** onu iş vaxtı
`www.gstatic.com/firebasejs/`-dən yükləyir: səhifədə `firebase_core_web` özü, push service worker-ində
(`mobile/web/firebase-messaging-sw.js`) `firebase-app-compat.js` və `firebase-messaging-compat.js`. Versiya
`firebase_core_web`-in dəstəklədiyi ilə eynidir və pinlidir; FlutterFire yeniləndikdə hər ikisi birlikdə dəyişir.

HEIC → JPEG çevrilməsi paket deyil — platformanın öz API-si (Android `ImageDecoder`, iOS `UIImage`), `MainActivity.kt` və `AppDelegate.swift`-də.

## Admin (npm)

Birbaşa asılılıqlar (`admin/package.json`), versiyalar pinli.

| Paket | Versiya | Lisenziya | İstifadə |
|---|---|---|---|
| `@angular/common` | 21.2.23 | MIT | Angular |
| `@angular/compiler` | 21.2.23 | MIT | Angular |
| `@angular/core` | 21.2.23 | MIT | Angular |
| `@angular/forms` | 21.2.23 | MIT | Angular |
| `@angular/platform-browser` | 21.2.23 | MIT | Angular |
| `@angular/router` | 21.2.23 | MIT | Angular |
| `@angular/cdk` | 21.2.14 | MIT | Material-in əsası |
| `@angular/material` | 21.2.14 | MIT | UI komponentləri, token-lərə bağlı tema |
| `@angular/service-worker` | 21.2.23 | MIT | Angular |
| `@microsoft/signalr` | 9.0.6 | MIT | `/hubs/admin` klienti |
| `@ngx-translate/core` | 17.0.0 | MIT | az / en / ru |
| `chart.js` | 4.5.0 | MIT | Dashboard qrafikləri |
| `leaflet` | 1.9.4 | BSD-2-Clause (plan 2.0-da istisna) | Kiosk xəritəsi (OpenStreetMap) |
| `rxjs` | 7.8.2 | Apache-2.0 | Angular asılılığı |
| `tslib` | 2.8.1 | 0BSD | TypeScript runtime köməkçiləri |
| `@angular/build` | 21.2.24 | MIT | Angular |
| `@angular/cli` | 21.2.24 | MIT | Angular |
| `@angular/compiler-cli` | 21.2.23 | MIT | Angular |
| `@types/leaflet` | 1.9.20 | MIT | Leaflet tipləri |
| `angular-eslint` | 21.1.0 | MIT | Tərtibat / lint / test |
| `eslint` | 9.39.0 | MIT | Tərtibat / lint / test |
| `jsdom` | 28.0.0 | MIT | Testlərin DOM mühiti |
| `prettier` | 3.8.1 | MIT | Tərtibat / lint / test |
| `typescript` | 5.9.2 | Apache-2.0 | Tərtibat / lint / test |
| `typescript-eslint` | 8.46.0 | MIT | Tərtibat / lint / test |
| `vitest` | 4.0.8 | MIT | Unit testlər |

Material Symbols Rounded şrifti self-host edilib və istifadə olunan ikonlara subset edilib (`admin/scripts/icon-subset.mjs`).

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
| RustFS (`rustfs/rustfs:1.0.0`, digest ilə pinlənib) | Apache-2.0 | **Development, test və pilotun default obyekt saxlaması** + migrasiya / mirror hədəfi (ikinci instansiya). Non-root (UID 10001). Bucket-ləri yaradan `rustfs-init` də eyni image-dir: image-dəki `curl` S3 sorğusunu özü imzalayır (`--aws-sigv4`), ikinci image lazım deyil. Risk: 1.0 GA 16.09.2026 — layihə gəncdir |
| MinIO | AGPL-3.0 / kommersiya | **Yalnız müştərinin öz ödənişli lisenziyası olduqda.** Image-lər 2026-cı ilin sentyabrından anonim çəkilmir (Docker Hub 12 sentyabr, quay.io 24 sentyabr); arxivləşdirilmiş MinIO image-ləri istifadə edilmir |
| Zenko CloudServer | Apache-2.0 | **İstifadə edilmir** (2026-09-27-dən) — image-lər uzun müddətdir yenilənmir, istifadəsi çətindir (`DECISIONS.md`) |
| OpenBao | MPL-2.0 | Secret idarəsi (Linux Foundation) |
| LibreOffice (headless) | MPL-2.0 | Office → PDF |
| Ghostscript (+ GhostPCL, GhostXPS) | AGPL-3.0 | PS / EPS / PCL / PRN / XPS → PDF (ayrıca proses, dəyişdirilmədən) |
| PDFium native kitabxanası (`bblanchon/pdfium-binaries`, `chromium/8066`; backend Dockerfile-da, CI-də və kiosk build-ində (`NeoPrint.Kiosk.csproj`) pinlənib, hər yükləmə SHA-256 ilə yoxlanılır — T-100) | BSD-3 / Apache-2.0 (Google PDFium) | Səhifə sayı və səhifə şəkilləri. Yalnız hazır binary götürülür; wrapper öz kodumuzdur (`NeoPrint.Pdfium`) |
| CUPS | Apache-2.0 | Linux çap |
| Mailpit | MIT | Development-də email |

---

## CC-BY ilə lisenziyalı məlumat dəstləri (atribusiya)

Hər ikisi yalnız build / lint zamanı oxunur, brauzer bundle-ına düşmür.

| Paket | Lisenziya | Müəllif / mənbə | İstifadə |
|---|---|---|---|
| `caniuse-lite` | CC-BY-4.0 | "caniuse.com" — Alexis Deveria və töhfə verənlər | `browserslist` üçün brauzer dəstək məlumatı (Angular build) |
| `spdx-exceptions` | CC-BY-3.0 | Linux Foundation (SPDX Workgroup) | SPDX lisenziya istisna identifikatorlarının rəsmi siyahısı (lint alətləri) |

---

## Şrift faylları (paket deyil, repo-da daşınan asılılıq)

Paket meneceri ilə deyil, birbaşa fayl kimi daşınır: bu, heç bir npm/pub
asılılığı əlavə etmir və runtime-da üçüncü tərəfə sorğu getmir (offline şəbəkədə
də işləyir, istifadəçinin IP-si CDN-ə getmir).

| Fayl | Lisenziya | Müəllif / mənbə | İstifadə |
|---|---|---|---|
| `admin/public/fonts/inter-{latin,latin-ext,cyrillic}.woff2` | SIL OFL 1.1 | The Inter Project Authors (rsms) | Admin və operator panelinin şrifti. Subset: latin (əsas), latin-ext (`ə`, `ğ`, `ş` — Azərbaycan hərfləri), cyrillic (`ru` lokalı). Cəmi 152 KB |
| `admin/public/fonts/material-symbols-rounded.woff2` | Apache-2.0 | Google | Panelin ikonları. Yalnız istifadə olunan 62 ikona subset edilib — 8.7 KB (tam variabel şrift ~3.5 MB-dır) |
| `mobile/assets/fonts/Inter-Variable.ttf` | SIL OFL 1.1 | The Inter Project Authors (rsms) | Mobil tətbiqin şrifti. Variabel fayl, `wght` oxu 400/500/600 üçün. Lisenziya mətni yanındadır: `mobile/assets/fonts/OFL.txt` |
| `backend/src/NeoPrint.Infrastructure/Receipts/Fonts/Inter-Variable.ttf` | SIL OFL 1.1 | The Inter Project Authors (rsms) | Qəbz PDF-inin şrifti (QuestPDF). Mobil tətbiqin eyni faylı (SHA-256 `29160a80…c559031`), assembly-yə resurs kimi daxil edilir; lisenziya mətni yanındadır (`OFL.txt`) və build çıxışına `licenses/Inter-OFL.txt` kimi kopyalanır |

Kiosk Inter-i `Avalonia.Fonts.Inter` paketi ilə alır (yuxarıdakı cədvəldə), ona
görə orada ayrıca fayl yoxdur. Kioskun ikonları isə `StreamGeometry` kimi
`desktop/src/NeoPrint.Kiosk/Styles/Icons.axaml`-dadır (Material Symbols
konturları, Apache-2.0) — kiosk maşınına şrift quraşdırmamaq üçün.
