# NeoPrint — Tam İcra Spesifikasiyası

> **Claude üçün təlimat (oxu və əməl et):**
> - Bu sənəd layihənin **yeganə mənbəyidir**. Bütün qərarlar artıq verilib — istifadəçiyə sual vermə.
> - Sənəddə açıq qeyd olunmayan kiçik detal çıxarsa, **Bölmə 2 (Qəbul edilmiş qərarlar)** ruhuna uyğun ən sadə və təhlükəsiz variantı seç, `docs/DECISIONS.md` faylına bir sətirlə yaz və davam et.
> - **Kitabxana siyasəti (Bölmə 2.0) məcburidir.** Siyahıda olmayan heç bir paket əlavə etmə. Ehtiyac yaranarsa əvvəlcə öz kodunu yaz; bu mümkün deyilsə, Bölmə 2.0 meyarlarına uyğun paket seç, `docs/THIRD_PARTY.md` və `docs/DECISIONS.md`-də əsaslandır.
> - İş **Faza 1 (MVP) — Addım 1–7** ardıcıllığı ilə görülür (Bölmə 12). Bir addımı bitirmədən (Definition of Done) növbəti addıma keçmə.
> - **Faza 2 (Addım 8–9) və Faza 3 (Addım 10)** yalnız istifadəçi ayrıca göstəriş verdikdə başlanır. MVP-də onlar üçün yalnız interface / genişlənmə nöqtələri qoyulur.
> - Hər addımın sonunda build + test + lisenziya yoxlaması keçməlidir, sonra commit + push (Bölmə 13).
> - Real bank, fiskal (e-kassa) API-ləri **yoxdur**: bütün xarici provayderlər interface + Mock implementasiya ilə yazılır.
> - Backend-in bütün gizli açarları (secret) **OpenBao-dan** oxunur (Bölmə 2.13). Environment variable-da yalnız OpenBao-ya qoşulma məlumatı olur. Kiosk tətbiqi **universal build**-dir — heç bir kiosk üçün ayrıca build, env və ya secret yoxdur (Bölmə 7.11). Repo-ya heç bir real secret yazılmır.
> - Kod dili: İngilis (class, method, dəyişən, commit). UI mətnləri: lokalizasiya faylları ilə (az — default, en, ru).
> - Bu sənədlə birlikdə `docs/` qovluğundakı qayda sənədləri də məcburidir (prioritet: `docs/README.md`): `ARCHITECTURE_RULES.md`, `STATE_MACHINES.md`, `IDEMPOTENCY.md`, `PAYMENT_FLOWS.md`, `FAILURE_RECOVERY.md`, `OFFLINE_BEHAVIOR.md`, `SECURITY_THREAT_MODEL.md`, `DOMAIN_RULES.md`, `DATA_RETENTION.md`, `OBSERVABILITY.md`, `CLAUDE_CODE_EXECUTION_RULES.md`. Ziddiyyət olarsa **bu plan üstündür**; uyğunsuzluğu `docs/DECISIONS.md`-ə yaz.
> - Arxitektura səviyyəli qərarlar üçün `docs/adr/TEMPLATE.md` formatında ADR yaz.
> - Bölmə 14-dəki hüquqi məsələlər istifadəçinin hüquqşünasla həll edəcəyi işlərdir; Claude onları konfiqurasiya edilə bilən parametrlər kimi qurur, dayanmır.

---

## 1. Layihə haqqında

NeoPrint — çoxfunksiyalı terminal (kiosk) printer platformasıdır. 4 hissədən ibarətdir:

| Qovluq | Nədir | Texnologiya |
|---|---|---|
| `backend/` | Mərkəzi API, real-time, ödəniş, fiskal, bildiriş | .NET 8 |
| `desktop/` | Kiosk tətbiqi (sensor ekranlı terminal, Windows və Linux) | Avalonia UI 11 |
| `mobile/` | İstifadəçi mobil tətbiqi | Flutter 3 (stable) |
| `admin/` | Admin web paneli + Operator paneli (mobil brauzer / PWA) | Angular (son stable) |

İstifadəçi növləri:
- **Anonim kiosk istifadəçisi** — hesabı yoxdur, flash kartdan çap edir, bank QR ilə ödəyir.
- **Qeydiyyatlı istifadəçi** — mobil tətbiqdə hesabı, balansı, faylları, qəbzləri var.
- **Admin istifadəçiləri** — `admin/` Angular web tətbiqi ilə sistemi idarə edir. Rollar və icazələr admin paneldən dinamik yaradılır (Bölmə 8A.2). Bütün admin imkanları web paneldən icra olunur; Swagger yalnız development üçündür.
- **Operatorlar (texniki işçilər)** — sahədə kiosklara xidmət edir, eyni Angular tətbiqin mobil-yönümlü **Operator panelindən** istifadə edir (Bölmə 8B).

---

## 2. Qəbul edilmiş qərarlar (dəyişməz)

### 2.0 Kitabxana siyasəti (məcburi)

**Qaydalar**
1. Üçüncü tərəf kitabxanadan **mümkün qədər istifadə edilmir**. Əvvəlcə platformanın daxili imkanları (.NET BCL, ASP.NET Core, Flutter SDK, Angular) və ya öz kodumuz seçilir.
2. Kitabxana yalnız **real ehtiyac** olduqda (öz yazılması qeyri-proporsional çətin və ya təhlükəlidir — məs. PDF render, S3 imzalama, verilənlər bazası drayveri) istifadə olunur.
3. Kitabxana **etibarlı mənbədən** olmalıdır: rəsmi vendor (Microsoft, Google, Amazon, MongoDB, Flutter/Dart komandası və s.) və ya uzun müddət aktiv inkişaf edən, geniş istifadə olunan layihə.
4. **Lisenziya: MIT** üstünlükdür. MIT alternativi olmayan hallarda yalnız **rəsmi vendorun** icazəli (permissive) lisenziyası (Apache-2.0, BSD) qəbul olunur və aşağıdakı siyahıda açıq göstərilir.
5. **Pullu versiya istifadə olunmur.** Sonradan kommersiya lisenziyasına keçmiş kitabxanaların **son pulsuz versiyası** pinlənir (MediatR 12.x, MassTransit 8.x, FluentAssertions 7.x) və major versiya yüksəldilmir. LGPL kitabxana (Hangfire.Core) dəyişdirilmədən paket kimi istifadə olunur; GPL/AGPL alətlər yalnız **ayrıca proses / server** kimi, dəyişdirilmədən (aşağıda "Xarici alətlər").
6. Versiyalar pinlənir. Hər asılılıq `docs/THIRD_PARTY.md`-də qeyd olunur: ad, versiya, lisenziya, mənbə, niyə lazımdır.
7. CI-də lisenziya yoxlaması: öz skriptimiz (`tools/license-check`) NuGet (`dotnet list package --include-transitive --format json` + nuspec), npm (`node_modules/*/package.json`) və pub (`pubspec.lock` + paket LICENSE) lisenziyalarını icazə siyahısı ilə yoxlayır; uyğunsuzluqda build dayanır. İcazə siyahısı: MIT, Apache-2.0, BSD-2/3-Clause, MPL-2.0, ISC; adla istisnalar `tools/license-check/allowlist.json`-da (`Hangfire.Core` — LGPL-3.0, `QuestPDF` — Community). Pinlənmiş major versiyaların (MediatR 12, MassTransit 8, FluentAssertions 7) yüksəldilməsi də bu skriptlə bloklanır.

**Təsdiqlənmiş siyahı**

| Hissə | Paket | Lisenziya | Səbəb |
|---|---|---|---|
| Backend | ASP.NET Core 8 (SignalR, DI, Logging, RateLimiting, HealthChecks, DataProtection, JwtBearer, Identity `PasswordHasher`) | MIT (Microsoft) | Platforma |
| Backend | `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | MIT (Microsoft) | SignalR backplane |
| Backend | `MongoDB.Driver` | Apache-2.0 (MongoDB Inc., rəsmi) | Rəsmi drayver, MIT alternativi yoxdur |
| Backend | `StackExchange.Redis` | MIT | Redis klienti |
| Backend | `AWSSDK.S3` | Apache-2.0 (Amazon, rəsmi) | S3 SigV4 imzalama (MinIO / Scality) |
| Backend | `MailKit` | MIT | SMTP (daxili `SmtpClient` köhnəlmiş hesab olunur) |
| Backend | `MediatR` **12.x** (pinlənmiş) | Apache-2.0 | CQRS / mediator. v13+ kommersiya lisenziyalıdır — yüksəldilmir |
| Backend | `MassTransit` + `MassTransit.RabbitMQ` + `MassTransit.MongoDb` **8.x** (pinlənmiş) | Apache-2.0 | Mesajlaşma, transactional outbox / inbox. v9+ kommersiyadır — yüksəldilmir |
| Backend | `Hangfire.Core`, `Hangfire.AspNetCore` + `Hangfire.Mongo` | LGPL-3.0 (pulsuz) + MIT | Background və planlanmış işlər, dashboard. Pullu Hangfire Pro paketləri istifadə olunmur |
| Backend | `QuestPDF` (Community lisenziya) | Pulsuz (illik gəliri 1M USD-dən az olan şirkətlər üçün) | Qəbz və hesabat PDF-ləri. Şərt `docs/THIRD_PARTY.md`-də qeyd olunur |
| Backend + Kiosk | `PDFsharp` (6.x, empira — rəsmi) | MIT | PDF səhifə əməliyyatları, tərtibat PDF-i |
| Backend + Kiosk | `SkiaSharp` (+ `SkiaSharp.NativeAssets.Linux`) | MIT (Microsoft) | Şəkil emalı, şəkil → PDF, preview |
| Backend + Kiosk | PDFium native binary-ləri (`bblanchon/pdfium-binaries`) + **öz P/Invoke wrapper-imiz** (`NeoPrint.Pdfium`) | BSD-3 / Apache-2.0 (Google PDFium) | PDF render və səhifə sayı |
| Backend + Kiosk | `QRCoder` | MIT | QR generasiya |
| Backend (dev) | `Swashbuckle.AspNetCore` | MIT | Yalnız Development-də Swagger UI |
| Backend + Kiosk test | `xUnit` | Apache-2.0 | Test framework |
| Backend + Kiosk test | `FluentAssertions` **7.x** (pinlənmiş) | Apache-2.0 | Assertion-lar. v8+ kommersiyadır — yüksəldilmir |
| Backend test | `Testcontainers` | MIT | Integration test |
| Kiosk | `Avalonia`, `Avalonia.Desktop` | MIT | UI |
| Kiosk | `CommunityToolkit.Mvvm` | MIT (Microsoft) | MVVM |
| Kiosk | `Microsoft.AspNetCore.SignalR.Client` | MIT | Real-time |
| Kiosk | `System.Management` | MIT (Microsoft) | Windows WMI |
| Kiosk | `Lextm.SharpSnmpLib` | MIT | Şəbəkə printerləri SNMP |
| Mobile | Flutter SDK, `intl`, `flutter_localizations`, `camera`, `web_socket_channel`, `go_router` | BSD-3 (Flutter/Dart komandası, rəsmi) | Platforma |
| Mobile | `firebase_core`, `firebase_messaging` | BSD-3 (Google, rəsmi) | Push |
| Mobile | `flutter_riverpod` | MIT | State management |
| Mobile | `dio` | MIT | HTTP |
| Mobile | `geolocator` | MIT | Məkan |
| Mobile | `file_picker` | MIT | Fayl seçimi |
| Mobile | `image` | MIT | Şəkil emalı (kəsmə, filtr) |
| Mobile | `google_mlkit_barcode_scanning` | MIT | QR oxuma (`camera` ilə) |
| Mobile | `flutter_map` | BSD-3 | OpenStreetMap xəritəsi (Google Maps API açarı / ödənişi tələb etmir); MIT alternativi yoxdur — **istisna** |
| Admin | Angular, Angular Material / CDK, Angular Service Worker | MIT (Google) | Platforma, PWA |
| Admin | `@microsoft/signalr` | MIT (Microsoft) | Real-time |
| Admin | `@ngx-translate/core` | MIT | Runtime dil dəyişmə |
| Admin | `chart.js` | MIT | Qrafiklər |
| Admin | `leaflet` | BSD-2 | Xəritə; MIT alternativi yoxdur — **istisna** |

**Öz kodumuzla yazılanlar (kitabxana əvəzinə)**
| Ehtiyac | Həll |
|---|---|
| Validasiya, mapping | DataAnnotations + öz validator-lar, əl ilə mapping |
| Push (FCM) | FCM HTTP v1 API — `HttpClient` + service account JWT imzası (BCL `RSA`) |
| Mobil SignalR klienti | SignalR JSON hub protokolu `web_socket_channel` üzərində |
| Mobil QR şəkli | Backend `QRCoder` ilə PNG qaytarır (`GET users/me/qr.png`) |
| Mobil PDF preview | Backend PDFium ilə səhifə şəkilləri qaytarır (`GET files/{id}/pages/{n}.png`) |
| Tərtibat PDF-i | Paylaşılan .NET kitabxanası `NeoPrint.Layout` (PDFsharp) — kiosk və backend eyni renderer; mobil yalnız JSON göndərir |
| Mobil secure storage, paylaşım, sənəd skanı | Öz platform channel-larımız: Android Keystore / iOS Keychain; Android `ACTION_SEND` / iOS `UIActivityViewController`; Android ML Kit Document Scanner / iOS VisionKit |
| Sistem telemetriyası | Windows: WMI / Performance counters; Linux: `/proc`, `/sys` |
| Linux CLI çağırışları (CUPS) | BCL `Process` |
| Test mock | Əl ilə yazılmış fake-lər |
| Fayl şifrələmə | BCL `AesGcm` (Bölmə 2.13) |
| Virtual (ekran) klaviatura | Öz Avalonia komponentimiz |

**Xarici alətlər və serverlər** (ayrıca proses, koda qoşulmur, pulsuz)
| Alət | Lisenziya | İstifadə |
|---|---|---|
| MongoDB Community Server 7 | SSPL (öz serverimizdə istifadə pulsuzdur) | Verilənlər bazası |
| Redis **7.2.x** (pinlənmiş) | BSD-3 | Keş, lock, sessiya, SignalR backplane. 7.2 BSD lisenziyalı son xəttdir |
| RabbitMQ 3.13 / 4.x | MPL-2.0 | Mesajlaşma |
| MinIO (pinlənmiş versiya) | AGPL-3.0 (dəyişdirilmədən, server kimi) | Obyekt saxlama. Community edition-in paylanma şərtləri dəyişə bildiyi üçün versiya pinlənir; Scality əsas alternativdir |
| Scality Zenko CloudServer | Apache-2.0 | Development/test-də Scality S3 əvəzi |
| LibreOffice (headless) | MPL-2.0 | Office → PDF konvertasiyası |
| Ghostscript (+ GhostPCL, GhostXPS) | AGPL-3.0 (ayrıca proses, dəyişdirilmədən) | PS / EPS / PCL / PRN / XPS → PDF, preview və səhifə sayı |
| Collabora Online CODE (Faza 2) | MPL-2.0 (pulsuz edition; eyni anda istifadəçi / sənəd limitləri var) | Office mətn redaktəsi (Addım 9) |
| CUPS | Apache-2.0 | Linux çap |
| OpenBao (pinlənmiş versiya) | MPL-2.0 (Linux Foundation, tam açıq mənbə) | Secret idarəsi (Bölmə 2.13). HashiCorp OpenBao BSL lisenziyalı olduğu üçün seçilməyib |
| Mailpit | MIT | Development-də email |
| Faza 2: ClamAV, Prometheus, Grafana, OpenTelemetry Collector, GlitchTip | GPL-2.0 / Apache-2.0 / AGPL-3.0 / Apache-2.0 / MIT | Server kimi, dəyişdirilmədən (Bölmə 12, Addım 8) |

**Lisenziyaya görə istifadə edilməyənlər**
| Paket / alət | Səbəb | Əvəzi |
|---|---|---|
| MediatR v13+, MassTransit v9+, FluentAssertions v8+ | Kommersiya lisenziyası | Son pulsuz versiyalar pinlənir (12.x / 8.x / 7.x) |
| Hangfire Pro paketləri | Pullu | Hangfire.Core (pulsuz) kifayətdir |
| Magick.NET, Serilog, FluentValidation, Mapster, Refit, CliWrap, PDFtoImage, PdfSharpCore, FirebaseAdmin | MIT olmayan və ya lazımsız asılılıq | Yuxarıdakı öz həllər |
| Google Maps (mobil) | API açarı, ödəniş | `flutter_map` + OpenStreetMap |

### 2.1 Platforma
| Mövzu | Qərar |
|---|---|
| Kiosk OS | **Linux — Ubuntu 24.04 LTS Server (minimal, masaüstü mühiti yoxdur) — tövsiyə olunan**; **Windows 11 IoT Enterprise LTSC** (və ya Enterprise) — yalnız printer drayveri Linux-da olmadıqda. Shell Launcher və Keyboard Filter yalnız bu edition-larda var; **Windows Pro dəstəklənmir**. Hər ikisi eyni kod bazası ilə, platformaya aid hər şey interface arxasındadır (Bölmə 7.6) |
| Platforma seçimi | Runtime-da `OperatingSystem.IsWindows()` / `IsLinux()` ilə DI-də müvafiq implementasiya qeydiyyatdan keçir. Avtomatik aşkarlanır, konfiqurasiya tələb olunmur |
| Kiosk lockdown | **Provisioning skripti** ilə qurulur (Bölmə 7.12). Windows: Shell Launcher v2 + Keyboard Filter + bildiriş/yeniləmə siyasətləri. Linux: `cage` (Wayland kiosk compositor), masaüstü və bildiriş servisi yoxdur, TTY keçidləri söndürülür |
| Kiosk komponentləri | `NeoPrint.Kiosk` (UI, məhdud kiosk istifadəçisi ilə) + `NeoPrint.Kiosk.Agent` (sistem servisi: watchdog, yeniləmə, uzaqdan əmrlər, provisioning təmiri) (Bölmə 7.12) |
| Printer | Hər kioskda eyni anda 1 aktiv printer. Kiosk qoşulu printerləri avtomatik aşkarlayıb backend-ə göndərir, **aktiv (default) printeri admin web paneldən seçir** (Bölmə 7.7). Lokal konfiqurasiyada printer adı yoxdur. Windows: Windows spooler. Linux: CUPS |
| QR reader | USB HID (klaviatura emulyasiyası) skaner; tətbiq Avalonia qlobal `KeyDown` buferindən oxuyur (`Enter` ilə bitən, simvollar arası < 50 ms daxil edilən sətir). Hər iki OS-də eyni |
| Flash kart | Windows: `DriveInfo` + `DriveType.Removable`, 2 san polling. Linux: `udisks2` avtomatik mount, `/proc/mounts` (`/media/*`, `/run/media/*`) 2 san polling |
| Valyuta | AZN, məbləğlər `decimal`, 2 onluq |
| Dillər | az (default), en, ru |
| Vaxt | Server UTC saxlayır; UI `Asia/Baku` göstərir |
| Admin panel | Ayrıca Angular web tətbiqi (`admin/`), Angular Material, standalone components + signals. Eyni tətbiqdə `/operator` bölməsi — mobil-yönümlü Operator paneli (PWA) |
| Bank / kart ödənişi | `IBankQrProvider`, `ICardPaymentProvider` interface + `Mock*Provider`. Hər iki interface-də `RefundAsync(providerRef, amount)` və `GetRefundStatusAsync` var (tam və hissəvi refund). Mock provider test üçün `POST /api/dev/payments/{id}/simulate-success` endpoint-i verir (yalnız Development) |
| Fiskal qəbz | `IFiscalProvider` interface + `MockFiscalProvider` (Bölmə 2.12). Hər uğurlu satış və refund fiskallaşdırılır |
| Email | MailKit (SMTP). Development-də Mailpit konteyneri |
| Push | FCM HTTP v1 (öz klientimiz). FCM konfiqi yoxdursa `LoggingPushSender` işləyir (xəta vermir) |
| Admin təcili bildirişi | Admin/Operator panelində real-time (SignalR `/hubs/admin`, səsli bildiriş + brauzer Notification API) + Email + Telegram bot (`NEOPRINT_TELEGRAM_BOT_TOKEN` boşdursa söndürülür) |
| Fayl saxlama | MinIO və ya Scality — **aktiv provayder admin web paneldən seçilir**, aralarında migrasiya və davamlı sinxronizasiya var (Bölmə 2.10). Hər ikisi S3 API ilə (`AWSSDK.S3`). **Bütün obyektlər tətbiq səviyyəsində şifrələnir** (Bölmə 2.13) |
| Feature flag | Öz sistemimiz, kiosk / platforma / istifadəçi səviyyəsində (Bölmə 2.16) |

### 2.2 Fayllar

#### Niyə fayllar çapdan əvvəl PDF-ə çevrilir?
Printerlər DOCX, XLSX, PPTX kimi faylları **anlamır**. Beynəlxalq standartlara (IPP / PWG 5100) görə printer yalnız "Page Description Language" (PDL) qəbul edir: **PDF, PostScript, PCL, PWG Raster, Apple URF, JPEG** (bəzilərində XPS). Windows-da "DOCX-i birbaşa çapa göndərmək" əslində Word-un faylı açıb driver vasitəsilə PDL-ə çevirməsidir — yəni gizli konvertasiya, üstəlik MS Office lisenziyası tələb edir və Linux-da yoxdur. Əlavə olaraq sistem **qiymət üçün dəqiq səhifə sayını**, **çapla eyni görünən preview-nu**, səhifə aralığı / səhifədə səhifə / real ölçü kimi parametrləri yalnız render edilmiş sənəd üzərində təmin edə bilir. Qayda:
- Printerin **native dəstəklədiyi** format (printer `document-format-supported` siyahısında varsa: PDF, PS, PCL, PWG, URF, JPEG, XPS) **və** faylda redaktə / aralıq / səhifədə səhifə tətbiq olunmayıbsa → **orijinal fayl birbaşa printerə** göndərilir.
- Digər bütün hallarda → fayl PDF-ə çevrilir və PDF göndərilir.
- Preview və səhifə sayı həmişə PDF-dən (PDFium) alınır.

#### Dəstəklənən formatlar (MVP)
| Qrup | Formatlar | Emal |
|---|---|---|
| PDF | PDF, PDF/A | Birbaşa |
| Mətn sənədləri | DOC, DOCX, DOCM, DOT, DOTX, ODT, OTT, RTF, WPS, TXT, HTML/HTM, XML | LibreOffice → PDF |
| Cədvəllər | XLS, XLSX, XLSM, XLTX, ODS, CSV | LibreOffice → PDF (hər vərəq "səhifəyə sığdır — en üzrə") |
| Təqdimatlar | PPT, PPTX, PPS, PPSX, POTX, ODP | LibreOffice → PDF (1 slayd = 1 səhifə) |
| Şəkillər | JPG, JPEG, PNG, BMP, GIF (ilk kadr), WEBP | SkiaSharp → PDF |
| Şəkillər (yalnız mobil) | HEIC / HEIF | Mobil cihazda platforma API-si ilə JPEG-ə çevrilib yüklənir |
| Printer PDL-ləri (IPP/PWG standartı) | PS, EPS, PCL, PRN, XPS, OXPS | Ghostscript / GhostPCL / GhostXPS → preview PDF; printer native dəstəkləyirsə orijinal göndərilir |

**Addım 9-a (Faza 2) saxlanılan formatlar:** TIFF, SVG, kioskda HEIC.

- İcazəli format siyahısı `Files.AllowedFormats` admin paneldən idarə olunur (format söndürülə / yandırıla bilər). Default: MVP siyahısının hamısı.
- Fayl tipi uzantı ilə yox, **məzmun imzası (magic bytes)** ilə yoxlanılır. Makrolu fayllarda (DOCM, XLSM, PPSX) makrolar heç vaxt icra olunmur (LibreOffice `--norestore`, makro təhlükəsizliyi "Very High").
- Konvertasiya ayrıca proses kimi, vaxt limiti (60 san) ilə işləyir. Tam izolyasiya (şəbəkəsiz konteyner / məhdud istifadəçi) Addım 8-dədir.
- Şifrəli / zədələnmiş fayl → istifadəçiyə aydın xəta mesajı ("Fayl şifrələnib, açmaq mümkün deyil").

#### Limitlər (admin tənzimləyir)
| Limit | Default | Kiosk üzrə override |
|---|---|---|
| Maksimum fayl ölçüsü | `Files.DefaultMaxSizeMb` = **50 MB** | `kiosks.MaxFileSizeMb` (boşdursa default) |
| Maksimum səhifə sayı | `Files.DefaultMaxPages` = **200** | `kiosks.MaxPages` (boşdursa default) |

- Admin həm default dəyərləri (Tənzimləmələr), həm də hər kioskun öz dəyərini (Kiosk redaktə / detal) dəyişə bilir. Kioskda "Default istifadə et" seçimi override-ı silir.
- Effektiv limit: `kiosk.X ?? settings.Files.DefaultX`. Kiosk `GET kiosks/me/config` ilə alır.
- Tətbiq yerləri: kiosk FlashBrowser (limitdən böyük fayl boz görünür, səbəb yazılır), mobil tətbiqdə kiosk seçiləndə və job yaradılanda, backend `POST print-jobs` validasiyası (əsas qoruma).
- Mobil yükləmə limiti (kiosk seçilməmiş): `max(Files.DefaultMaxSizeMb, bütün kiosk override-larının maksimumu)`. İstifadəçi kvotası: `Files.UserQuotaGb` = 1 GB.

#### Redaktə (MVP)
| Fayl tipi | Kiosk və mobil redaktə imkanı |
|---|---|
| PDF və Office faylları (PDF-ə çevrildikdən sonra) | Səhifə seçimi, fırlatma, silmə, sıra dəyişmə (PDFsharp) |
| TXT | Mətn redaktəsi (öz sadə redaktor; kioskda ekran klaviaturası) |
| Şəkillər | Fırlatma, kəsmə, ağ-qara, parlaqlıq/kontrast, **Səhifədə yerləşdir** (Bölmə 2.11) |

- **Office (Word, Excel, PowerPoint) mətn redaktəsi Faza 2-yə (Addım 9) saxlanılıb** — Collabora Online CODE + backend WOPI host. MVP-də interface (`IDocumentEditor`) yeri ayrılır.
- PS / PCL / XPS faylları redaktə olunmur, yalnız səhifə seçimi.
- Redaktə olunmuş versiya yeni fayl versiyası kimi saxlanılır (`files.Versions[]`, son `Files.VersionsToKeep` versiya), orijinal toxunulmaz qalır.

#### Saxlama müddəti
- Anonim kiosk faylları: kioskda lokal, şifrəli spool-da; job bitdikdən və ya sessiya bağlandıqdan sonra dərhal silinir.
- İstifadəçi faylları: istifadəçi silənə qədər və ya faylın avtomatik silinmə qaydasına görə (Bölmə 2.13).
- Çevrilmiş preview / çap PDF-ləri: 24 saat (lazım olanda yenidən yaradılır).

#### Flash kartdan daxil edilən fayllar
- FlashBrowser, FilePreview, PrintOptions və Receipt ekranlarında faylın yanında **"USB" badge** göstərilir. Badge-ə toxunduqda info pəncərəsi: *"Flash kartdan daxil edilən sənədlər sistemdə saxlanılmır. Çap üçün müvəqqəti emal olunur və sonra silinir. NeoPrint mobil tətbiqi ilə ödəsəniz, sənəd və qəbz hesabınıza əlavə olunacaq."*
- Fayl kioskda lokal emal olunur (konvertasiya, preview, çap). Backend-ə yalnız metadata gedir.
- **Mobil tətbiqlə ödənilərsə** (Bölmə 9.2): ödəniş uğurlu olan kimi kiosk faylın **orijinalını** (redaktə edilibsə son versiyasını) və çap PDF-ini backend-ə yükləyir → fayl istifadəçinin "Fayllarım" siyahısına `Source=Flash` ilə əlavə olunur, job və qəbz hesabına bağlanır. Yükləmə uğursuz olarsa kiosk şifrəli spool-da saxlayıb 24 saat ərzində təkrar cəhd edir; çap buna görə gözləmir.
- Bank QR ilə (anonim) ödənilərsə fayl heç yerdə saxlanılmır.

### 2.3 Print parametrləri və qiymət

| Parametr | Seçimlər |
|---|---|
| Nüsxə sayı | 1–50 |
| Səhifə aralığı | Hamısı / aralıq (`1-3,5,8-10`) |
| Rəng | Ağ-qara / Rəngli |
| Tərəf | Birtərəfli / İkitərəfli |
| Kağız | A4 / A3 |
| Oriyentasiya | Portret / Landşaft / Avtomatik |
| Səhifədə səhifə | 1 / 2 / 4 |
| Ölçüləmə | Səhifəyə sığdır (default) / Real ölçü 100% (tərtibat faylları üçün məcburi) |

**Qiymət profilləri** (`pricing_profiles`)
- Sahələr: `Id`, `Name` (məs. "Default", "Universitet", "Metro", "Biznes mərkəzi"), `IsDefault`, `BwPricePerSide`, `ColorPricePerSide`, `PaperMultipliers {A4: 1, A3: 2}`, `MinOrder`, `VolumeTiers[] {MinSides, DiscountPercent}`, `UpdatedBy`, `UpdatedAt`, dəyişiklik tarixçəsi.
- Sistemdə həmişə 1 `IsDefault` profil var. Kiosk `PricingProfileId` ilə profilə bağlanır; boşdursa default profil tətbiq olunur. Kiosk üçün xüsusi qiymət lazımdırsa admin yeni profil yaradır (məs. "KSK-0005 xüsusi").
- Admin paneldə: profil CRUD, bir neçə kiosku eyni anda profilə bağlama, nümunə kalkulyator, hansı kioskların hansı profildə olduğu.

Default profil dəyərləri:
| Parametr | Dəyər |
|---|---|
| Ağ-qara, bir üz | 0.10 AZN |
| Rəngli, bir üz | 0.30 AZN |
| A3 əmsalı | x2 |
| Minimum sifariş | 0.20 AZN |
| Həcm endirimi | 50+ üz: 10%, 100+ üz: 15% (admin dəyişə bilər, pillə əlavə/sil) |

Formul:
```
sides      = ceil(selectedPages / pagesPerSheet) * copies
sheets     = duplex ? ceil(sides / 2) : sides          # kağız sayğacı üçün
base       = sides * pricePerSide(color) * paperMultiplier(paper)
tier       = ən böyük VolumeTier where sides >= MinSides
price      = max(MinOrder, base * (1 - tier.DiscountPercent/100))
```
- Pul üçün `Money` value object (`Amount`, `Currency=AZN`) istifadə olunur; yuvarlaqlaşdırma yalnız mərkəzi `Money` tipində (bank rounding, 2 onluq).
- Qiymət job yaradılanda **snapshot** kimi saxlanılır (`PriceBreakdown`: pages, sides, sheets, copies, color, paper, basePrice, volumeDiscount, promoDiscount, bonusPages, minimumApplied, finalPrice, currency); sonrakı profil dəyişiklikləri mövcud job-lara təsir etmir. Planlanmış çaplar planlama anındakı qiymətlə qalır.
- Mobil tətbiqdə kiosk detalında həmin kioskun qiymətləri göstərilir.

### 2.4 Tənzimləmələr (`AdminSettings`, default dəyərlər — hamısı admin paneldən dəyişdirilir)
| Açar | Default | Məna |
|---|---|---|
| `Queue.DisplayLimit` | 5 | Kioskda göstərilən queue sayı (3 və ya 5) |
| `Payment.TimeoutMinutes` | 5 | Ödənilməmiş intent-in bitmə vaxtı; ödənilməmiş job (anonim kiosk job-ları daxil) bu müddətdən sonra `Expired` olur. Kioskdakı geri sayım bu dəyərdən alınır |
| `KioskSession.TtlMinutes` | 10 | Mobil ↔ kiosk sessiyası fəaliyyətsizlik müddəti |
| `Kiosk.IdleResetSeconds` | 90 | Toxunulmasa ana ekrana qayıdış |
| `UserQr.RotationSeconds` | 60 | Şəxsi QR token-in yenilənmə müddəti |
| `Schedule.ReminderLeadMinutes` | 0 | Planlanmış vaxtda bildiriş |
| `Schedule.AutoCancelHours` | 24 | Cavab verilməyən planlanmış çap avtomatik ləğv + balansa refund |
| `Referral.BonusPages` | 10 | Dəvət edənə verilən bonus səhifə |
| `Referral.InviteeBonusPages` | 0 | Dəvət olunana verilən bonus |
| `Referral.MaxBonusPagesPerJob` | 5 | Bir çapda istifadə oluna bilən maksimum bonus səhifə |
| `Refund.VoucherValidDays` | 90 | Anonim refund voucher-in etibarlılığı (yalnız bank refund-u mümkün olmadıqda) |
| `Refund.ProviderMaxAttempts` | 3 | Bank/kart refund-unun avtomatik təkrar cəhd sayı |
| `Job.SuspectedAfterMinutes` | 15 | `Printing` / `Queued` job üçün kioskdan bu müddətdə status və ya heartbeat gəlməzsə job `Suspected` olur (Bölmə 2.5, ssenari B). Avtomatik refund **edilmir** |
| `Job.SuspectedAutoResolveHours` | 72 | `Suspected` job bu müddətdə həll olunmazsa adminə xatırlatma bildirişi gedir. Avtomatik heç nə edilmir — qərar adminindir |
| `Kiosk.HeartbeatSeconds` | 15 | Kiosk heartbeat intervalı |
| `Kiosk.OfflineAfterSeconds` | 60 | Heartbeat gəlməzsə offline sayılır |
| `Kiosk.TelemetrySeconds` | 30 | Kiosk telemetriya göndərmə intervalı |
| `Kiosk.TelemetryRetentionDays` | 30 | Telemetriya tarixçəsinin saxlanma müddəti |
| `Files.DefaultMaxSizeMb` | 50 | Default maksimum fayl ölçüsü (kiosk override edə bilər) |
| `Files.DefaultMaxPages` | 200 | Default maksimum səhifə sayı (kiosk override edə bilər) |
| `Files.UserQuotaGb` | 1 | İstifadəçi başına saxlama kvotası |
| `Files.AllowedFormats` | Bölmə 2.2 MVP siyahısı | İcazəli formatlar |
| `Files.VersionsToKeep` | 5 | Redaktə versiyalarının sayı |
| `Storage.SyncParallelism` | 4 | Sinxronizasiyada paralel obyekt sayı |
| `Storage.SourceRetentionDays` | 30 | Migrasiyadan sonra mənbədə obyektlərin saxlanma müddəti |
| `Storage.ReconciliationCron` | `0 3 * * *` | Mirror rejimində gecəlik uyğunlaşdırma (Hangfire recurring job) |
| `Layout.DefaultMarginMm` | 10 | Tərtibat redaktorunda default kənar boşluq |
| `Privacy.SensitiveAutoDeleteDefault` | `AfterPrint` | Həssas (şəxsi sənəd) skanlar üçün default avtomatik silinmə |
| `Privacy.AccountDeletionGraceHours` | 24 | Hesab silmə sorğusundan sonra faylların fiziki silinmə müddəti |
| `Fiscal.Enabled` | true | Fiskallaşdırma aktivdir |
| `Fiscal.TopUpReceiptMode` | `OnTopUp` | Balans artırılanda fiskal çek (`OnTopUp`) və ya xidmət göstəriləndə (`OnService`) — hüquqşünas təsdiqi ilə seçilir (Bölmə 14) |
| `Fiscal.MaxPendingMinutes` | 15 | Bu müddətdə fiskallaşmayan satış üçün incident (High) |
| `Paper.BlockWhenInsufficient` | true | Kağız sayğacı job üçün kifayət etmirsə sifariş qəbul olunmur |
| `Service.ForecastLeadHours` | 24 | Proqnoza görə bitməsinə bu qədər qalanda avtomatik servis tapşırığı |
| `Service.TaskOverdueEscalationMinutes` | 60 | Critical tapşırıq bu müddətdə başlanmasa adminlərə eskalasiya |
| `Analytics.RetentionDays` | 180 | Analitika hadisələrinin saxlanma müddəti |
| `Fiscal.OnlineTerminalId` | boş | Mobil (onlayn) satışlar üçün fiskal terminal ID |
| `Kiosk.UpdateWindow` | `02:00-05:00` | Kiosk avtomatik yenilənməsinin icazəli saat aralığı ("İndi yenilə" əmri bu aralığı nəzərə almır, amma yenə boş vaxtı gözləyir) |
| `Update.CheckIntervalMinutes` | 15 | Agent-in yeni versiya yoxlama intervalı |
| `Update.HealthTimeoutMinutes` | 5 | Yeniləmədən sonra UI bu müddətdə sağlam olmasa rollback |
| `Update.KeepVersions` | 3 | Kioskda saxlanılan versiya sayı (rollback üçün) |
| `Enrollment.TokenTtlHours` | 72 | Qeydiyyat (enrollment) token-inin etibarlılıq müddəti |
| `Kiosk.ConfigCacheTtlHours` | 24 | Kioskun lokal konfiqurasiya keşinin etibarlılığı; bu müddətdən köhnədirsə kiosk `Degraded` rejimə keçir və yeni sifariş qəbul etmir |
| `Payment.ReconcileAfterMinutes` | 10 | `Expired` / naməlum ödənişlərin provider statusu ilə uzlaşdırılması |
| `Kiosk.CrashLoopThreshold` | 3 | 10 dəqiqədə bu qədər UI çöküşü → təhlükəsiz rejim + Critical incident |

### 2.5 Refund qaydaları

**İki ayrı ssenari — avtomatik refund yalnız birində olur**

Fərq bir sualdadır: **sistem nə baş verdiyini bilirmi?**

| | **A — Printer xətası, kiosk serverlə əlaqədədir** | **B — Kiosk ilə server arasında əlaqə itib** |
|---|---|---|
| Nümunə | Printer offline, kağız qurtarıb, sıxışma, çap yarımçıq qalıb, toner bitib | Elektrik kəsilib, şəbəkə itib, kompüter sıradan çıxıb, tətbiq çökmüş və qalxmayıb |
| Sistem nəyi bilir | Kiosk nəyin baş verdiyini **bildirib**: `print_attempts`, printer sayğacı, xəta kodu var | **Heç nə** — sonuncu məlum vəziyyət `Printing` və ya `Queued`-dir, nəticə naməlumdur |
| Job | `Interrupted` | **`Suspected`** (təsdiq gözləyir) |
| Ödəniş | Ləğv olunur / qaytarılır | **`Succeeded` qalır** |
| Refund | **Avtomatik** — anonim bank ödənişi dərhal karta, qeydiyyatlıya aşağıdakı matris üzrə | **Avtomatik refund yoxdur.** Yalnız adminin müraciət əsasında verdiyi qərarla |

**Səbəb:** B ssenarisində çapın olub-olmadığı bilinmir. Avtomatik qaytarmaq
çap olunmuş işi pulsuz verməkdir; avtomatik qaytarmamaq isə çap olmayıbsa
müştərinin pulunu udmaqdır. Hər iki səhv pul itkisidir, ona görə qərar
məlumatı olan adama — adminə — verilir. Kiosk sonradan qalxıb nəticəni
göndərsə, qərar artıq lazım deyil: sistem özü həll edir (aşağıda).

**B ssenarisinin axını**

1. `StuckJobWatchdogJob` (hər dəqiqə) `Job.SuspectedAfterMinutes` (default 15)
   müddətində kioskdan status və ya heartbeat gəlməyən `Printing` / `Queued`
   job-u **`Suspected`** edir. **Avtomatik ləğv yoxdur, avtomatik refund yoxdur.**
2. Incident (High) yaranır və job admin panelində **"Baxış gözləyən job-lar"**
   siyahısına düşür (Bölmə 8A.3).
3. **Kiosk sonradan qalxıb nəticə göndərsə job avtomatik həll olunur:**
   - çap tamamlanıbsa → `Completed`, refund yoxdur;
   - çap olmayıbsa və ya yarımçıqdırsa → **A ssenarisinə keçir** və avtomatik
     refund işə düşür.
4. `Job.SuspectedAutoResolveHours` (default 72) keçsə də kiosk qayıtmazsa:
   yalnız **xatırlatma bildirişi**. Avtomatik heç nə edilmir — admin qərarı
   tələb olunur.

**Qaytarılma istiqaməti (default) — A ssenarisi və istifadəçi ləğvi üçün**
| Ödəniş necə edilib | Refund səbəbi | Default istiqamət |
|---|---|---|
| Kioskda **bank QR / bank kartı** (anonim) | **A** — printer xətası (kiosk əlaqədədir, çap yarımçıq) | **Avtomatik, dərhal karta / bank hesabına** (`IBankQrProvider.RefundAsync`) |
| İstənilən metod | **B** — kiosk ilə əlaqə itib, nəticə naməlum | **Avtomatik refund yoxdur.** Job `Suspected`, ödəniş `Succeeded` qalır; yalnız admin qərarı ilə |
| Kioskda **bank QR / bank kartı** (anonim) | İstifadəçi job kodu ilə ləğv edir | Karta / bank hesabına |
| Mobil tətbiq — Wallet | İstənilən | Wallet |
| Mobil tətbiq — Kart | İstifadəçi ləğvi, planlanmış çap ləğvi | Wallet (istifadəçi ləğv zamanı "Karta qaytar" seçə bilər) |
| Mobil tətbiq — Kart | Sistem xətası | Wallet (istifadəçi 24 saat ərzində "Karta qaytar" seçə bilər) |
| Bonus səhifələr | İstənilən | Bonus balansa |
| Admin tərəfindən manual | İstənilən | Admin seçir (aşağıda) |

- **Qeyd:** "Karta qaytar" yalnız **konkret ödənişin** refund-u üçündür və yalnız həmin ödəniş kartla edilibsə mümkündür. Wallet-ə düşmüş məbləğ artıq balansdır və **geri çıxarıla bilməz** (Bölmə 2.14).
- Qarışıq ödənişdə (bonus + wallet + kart) hər hissə öz mənbəyinə qaytarılır.
- **Bank refund-u uğursuz olarsa**: `Refund.ProviderMaxAttempts` dəfə eksponensial retry → hələ də uğursuzdursa refund `Failed`, adminə incident (High). Admin paneldən təkrar cəhd və ya alternativ istiqamət (qeydiyyatlıya Wallet, anonimə voucher) seçilir. Voucher yalnız bu fallback halında yaradılır (`RF-XXXX-XXXX`, ekranda QR + kod, email verilibsə emailə).
- Kioskda bank ilə ödənilmiş job xəta ilə dayananda ekranda göstərilir: *"Çap tamamlanmadı. X AZN kartınıza qaytarılır. Bankınızdan asılı olaraq 1–3 iş günü çəkə bilər."* + refund arayışı (QR ilə status səhifəsi `neoprint.az/refund/{id}`) + email daxil etmə imkanı.
- Hissəvi çap olunmuş job: çap olunmamış tərəflərin dəyəri qaytarılır (`PrintedSides` əsasında; məlum deyilsə tam məbləğ).
- `Queued` statusundakı job istənilən vaxt ləğv edilə bilər (sahibi mobil tətbiqdən, kioskda job kodu ilə, admin paneldən).
- Promokod endirimi refund-da geri qaytarılmır, promokod istifadəsi ləğv olunur.
- Hər refund fiskal **qaytarma çeki** ilə müşayiət olunur (Bölmə 2.12).

**Müştəri müraciəti (B ssenarisi üçün)**

Avtomatik refund olmadığına görə müraciət kanalı axının bir hissəsidir, əlavə
deyil:

- **Anonim ödəniş:** qəbzdəki və ekrandakı **refund status QR-ı**
  (`neoprint.az/refund/{id}`) job kodunu müəyyənləşdirir. Müştəri həmin
  səhifədən müraciət yaradır və ya qaynar xəttə job kodunu deyir.
- **Qeydiyyatlı istifadəçi:** mobil tətbiqdə job detalında **"Problem barədə
  bildir"** düyməsi — job seçilir, qısa mətn yazılır. Müraciət admin panelində
  həmin job-un araşdırma kartına bağlanır.
- Hər müraciət `audit_logs`-a düşür; cavab müddəti `docs/legal/REFUND_POLICY.az.md`-dədir.

**Refund modeli** (`refunds` kolleksiyası)
`Id`, `PaymentId`, `JobId`, `UserId?`, `Amount`, `BonusPages`, `Destination (OriginalMethod/Wallet/Voucher/Bonus)`, `Reason (SystemError/UserCancel/ScheduleCancel/AdminManual/PartialPrint)`, `InitiatedBy (System/User/Admin + id)`, `Status (Pending/Processing/Succeeded/Failed)`, `ProviderRefundRef`, `FiscalRefundReceiptId?`, `Attempts`, `LastError`, `Note`, `CreatedAt`, `CompletedAt`. Bir ödənişə bir neçə refund ola bilər; cəmi ödəniş məbləğini keçə bilməz.

**Admin tərəfindən ləğv və refund**
- Admin ödəniş və ya job detalından "Job-u ləğv et və qaytar" / "Qaytar" seçir: tam və ya hissəvi məbləğ, səbəb (məcburi).
- İstiqamət seçimi:
  - Ödənişə bağlı **account varsa** və ödəniş kartla edilibsə: "İstifadəçi hesabına (Wallet)" və ya "Bank kartına (ilkin ödəniş metodu)".
  - **Account yoxdursa** (anonim): "Bank kartına / hesabına"; bank refund-u mümkün deyilsə "Voucher".
  - Ödəniş Wallet-dən edilibsə yalnız Wallet.
- İcra `refunds`-a yazılır, statusu real-time yenilənir, `audit_logs`-a düşür, istifadəçiyə (varsa) push + email.

**`Suspected` job üzrə admin qərarı (B ssenarisi)**

Araşdırma ekranı Bölmə 8A.3-dədir. Mümkün qərarlar — hər biri üçün səbəb və
qeyd **məcburidir**, hamısı `audit_logs`-a düşür:

| Qərar | Nəticə |
|---|---|
| **Tam qaytar** | Job `Cancelled`, tam məbləğ Bölmə 2.5 istiqamət matrisi üzrə qaytarılır |
| **Hissəvi qaytar** | Admin məbləği daxil edir (məsələn yalnız çap olunmamış səhifələr); job `Completed` (hissəvi) |
| **Qaytarma yoxdur (çap olunub)** | Job `Completed`, refund yoxdur |
| **Yenidən çapa göndər** | Job yeni `Queued` job kimi növbəyə qayıdır, əlavə ödəniş alınmır |

### 2.6 Referral qaydası
- Hər istifadəçinin unikal referral kodu var (`NP` + 6 simvol). Paylaşım: kod, link (`https://neoprint.az/r/{code}`), QR.
- Dəvət olunan qeydiyyatdan keçib **ilk ödənişli çapını tamamladıqda** dəvət edənə `Referral.BonusPages` əlavə olunur.
- Bonus səhifələr print parametrlərindən (rəng, A3, ikitərəfli) asılı olmayaraq 1 bonus = 1 çap olunan üz.
- Bir job-da maksimum `Referral.MaxBonusPagesPerJob` bonus istifadə olunur, qalanı adi ödənişlə.
- Özünü dəvət etmək və eyni cihazdan təkrar referral bloklanır (device id yoxlaması).
- Bonus səhifələr pul deyil, geri çıxarıla bilməz, hesab silinəndə itir.

### 2.7 Promokod
- Tip: faiz (%) və ya sabit (AZN). Sahələr: kod, dəyər, başlama/bitmə tarixi, ümumi limit, istifadəçi başına limit, minimum məbləğ, tətbiq olunduğu kiosklar / qiymət profilləri (boş = hamısı).
- Anonim kiosk istifadəçisi də promokod daxil edə bilər (istifadəçi başına limit tətbiq olunmur, ümumi limit tətbiq olunur).
- Bir sifarişdə 1 promokod. Hesablama ardıcıllığı: profil qiyməti → həcm endirimi → bonus səhifələr → promokod (qalan məbləğə).

### 2.8 Autentifikasiya və razılıqlar
- Mobil: email + şifrə qeydiyyatı, email təsdiq kodu (6 rəqəm). JWT access 15 dəq, refresh 30 gün (rotation). Refresh token cihazda öz secure storage channel-ımızla (Keystore/Keychain) saxlanılır.
- Qeydiyyatda **Məxfilik siyasəti** və **İstifadə şərtləri** (Wallet-in geri qaytarılmaması bəndi daxil) qəbul edilir (`consents`). Sənədlərin yeni versiyası dərc olunanda istifadəçi növbəti girişdə yenidən qəbul edir.
- Kiosk: **cihaz açarı** ilə (Bölmə 7.11) — kiosk cihazda yaradılan ECDSA P-256 açarı ilə backend-in challenge-ni imzalayır → cihaz aktivdirsə kiosk JWT (rol `Kiosk`, `KioskId` claim, 1 saat, avtomatik yenilənir); aktivləşdirmə gözləyirsə məhdud `Provisioning` token. Kioskda ApiKey, env və ya əl ilə daxil edilən kimlik yoxdur.
- Admin web: email + şifrə, dinamik rollar və icazələr (Bölmə 8A.2). Access token (15 dəq) yaddaşda, refresh token `HttpOnly; Secure; SameSite=Strict` cookie-də (`POST auth/admin/login`, `POST auth/admin/refresh`). 5 uğursuz cəhddən sonra 15 dəq blok.
- Seed: `SuperAdmin` sistem rolu və bu rola malik ilk istifadəçi — `NEOPRINT_SEED_ADMIN_EMAIL` / `NEOPRINT_SEED_ADMIN_PASSWORD` (ilk girişdə şifrə dəyişmək məcburidir).
- Şifrə: ASP.NET Core Identity `PasswordHasher`.

### 2.9 QR formatları
| QR | Məzmun | Qeyd |
|---|---|---|
| Kiosk QR (sabit) | `neoprint://kiosk/{kioskCode}` | Yalnız **footer**-də (sol tərəfdə) göstərilir, header-də təkrarlanmır. Bütün ekranlarda həmişə görünür, altında `kioskCode` yazılır |
| Mobil ödəniş QR | `neoprint://pay/{paymentIntentId}` | Kioskda göstərilir, mobil scan edir, `Payment.TimeoutMinutes` sonra etibarsız |
| Bank QR | Provider-in qaytardığı string | Mock provider-də `MOCKBANK:{paymentIntentId}` |
| Şəxsi istifadəçi QR | `NPU:{userId}:{timestamp}:{hmac}` | HMAC-SHA256 (`NEOPRINT_QR_HMAC_KEY`), `UserQr.RotationSeconds` müddətində keçərli |
| Referral QR | `https://neoprint.az/r/{code}` | |
| Refund voucher QR | `neoprint://voucher/{code}` | |
| Refund status QR | `https://neoprint.az/refund/{refundId}` | Anonim istifadəçi üçün refund statusu səhifəsi |
| Cihaz aktivləşdirmə QR | `neoprint://activate/{deviceId}?code={pairingCode}` | Aktivləşdirilməmiş kioskun ekranında; Operator panelindən scan edilir (Bölmə 7.11) |
| Fiskal çek QR | Fiskal provayderin verdiyi yoxlama linki | Qəbzdə göstərilir |

### 2.10 Fayl saxlama provayderləri (MinIO / Scality)
**Provayderlər**
- Tiplər: `MinIO`, `Scality` (RING S3 Connector / Zenko CloudServer). Hər ikisi `S3StorageProvider` ilə (`AWSSDK.S3`, `ForcePathStyle=true`); tipə görə yalnız kiçik fərqlər (health endpoint, checksum başlıqları) ayrıca adapterdə.
- `storage_providers` kolleksiyası: `Id`, `Name`, `Type`, `Endpoint`, `Region`, `UseSsl`, `AccessKey`, `SecretKey` (OpenBao-dan gələn `NEOPRINT_STORAGE_SECRETS_KEY` ilə AES-GCM şifrələnmiş), `Buckets {Files, Receipts, Temp}`, `Role (Primary/Secondary/Inactive)`, `HealthStatus`, `LastCheckedAt`.
- Eyni anda **1 Primary** olur. Mirror rejimində əlavə **1 Secondary**.
- İlk provayder OpenBao-dakı `NEOPRINT_STORAGE_SEED_*` dəyərləri ilə (MinIO) yaradılır. Sonrakı bütün dəyişikliklər yalnız admin paneldən (`storage.manage` icazəsi).
- Konfiqurasiya Redis-də keşlənir; dəyişiklik `StorageConfigChanged` event-i ilə bütün API/Worker instansiyalarına yayılır, restart tələb olunmur.
- Hər obyekt yazısında `files.StorageProviderId` (və qəbzlərdə eyni sahə) + `Sha256` (şifrələnmiş məzmunun) saxlanılır. Oxuma həmişə obyektin qeyd olunmuş provayderindən edilir → aktiv provayder dəyişsə də köhnə fayllar əlçatan qalır.
- Obyektlər şifrəli saxlanıldığı üçün sinxronizasiya şifrəli məzmunu olduğu kimi kopyalayır (açarlar provayderdən asılı deyil).

**Admin əməliyyatları**
- Provayder əlavə et / redaktə et → **"Bağlantını yoxla"** (bucket-lərin mövcudluğu, test obyektinin put/get/delete) → uğurlu olmadan aktivləşdirmək olmur.
- **Primary et**: yeni yazılar dərhal yeni provayderə gedir, köhnə obyektlər yerində qalır (istəyə görə migrasiya başladılır).

**Sinxronizasiya rejimləri** (`storage_sync_jobs`)
| Rejim | Davranış |
|---|---|
| **Migrasiya** (birdəfəlik) | Mənbədəki bütün obyektlər hədəfə kopyalanır. Başlayan andan etibarən **dual-write** aktivdir (yeni yazılar hər ikisinə), beləliklə boşluq yaranmır. Hər obyekt ölçü + SHA-256 ilə yoxlanılır, uğurlu olanda `StorageProviderId` hədəfə dəyişir. Tamamlanıb yoxlanandan sonra "Bitəndə hədəfi Primary et" seçilibsə avtomatik keçid olur. Mənbədəki obyektlər `Storage.SourceRetentionDays` sonra admin təsdiqi ilə silinir |
| **Mirror** (davamlı) | Primary-yə hər yazı / silmə `ObjectStored` / `ObjectDeleted` event-i ilə Secondary-yə təkrarlanır. Gecəlik (`Storage.ReconciliationCron`) `StorageReconciliationJob` hər iki tərəfin siyahısını müqayisə edib fərqləri düzəldir. Primary health check-dən keçməzsə oxumalar avtomatik Secondary-yə yönəlir və Critical incident yaranır |

- İstiqamət hər iki tərəfə mümkündür (MinIO → Scality və Scality → MinIO).
- `storage_sync_jobs`: `Id`, `Mode`, `SourceId`, `TargetId`, `Status (Pending/Running/Paused/Verifying/Completed/Failed/Cancelled)`, `TotalObjects`, `CopiedObjects`, `FailedObjects`, `TotalBytes`, `CopiedBytes`, `Checkpoint (bucket + son key)`, `SwitchPrimaryOnComplete`, `StartedBy`, `StartedAt`, `FinishedAt`.
- Uğursuz obyektlər `storage_sync_failures`-a yazılır (key, xəta, cəhd sayı). 3 dəfə eksponensial retry, sonra admin paneldən "Uğursuzları təkrarla".
- İş restart-dan sonra checkpoint-dən davam edir. Eyni anda yalnız 1 aktiv migrasiya işi ola bilər.
- Silinmə (istifadəçi faylı, hesab silmə, avtomatik silinmə) **bütün provayderlərdə** (Primary, Secondary, migrasiya mənbəyi) icra olunur.
- Paneldə: canlı progress (SignalR `StorageSyncProgress`), sürət, təxmini qalan vaxt, Pause / Resume / Cancel.

### 2.11 Mobil sənəd skanı və səhifə tərtibatı
**Skan (mobil)**
- Öz platform channel-ımız: Android — Google ML Kit Document Scanner API (Google Play Services, pulsuz); iOS — VisionKit `VNDocumentCameraViewController`. Kənar aşkarlama, perspektiv düzəlişi, çoxsəhifəli skan.
- Skandan sonra (`image` paketi ilə): kəsmə, fırlatma, filtr (Orijinal / Qrayskeyl / Ağ-qara / Kontrastlı), parlaqlıq.
- **Sənəd tipi və real ölçü** seçimi (skan zamanı və ya sonra dəyişdirilə bilər):

| Preset | Ölçü (mm) | Həssas sənəd |
|---|---|---|
| Şəxsiyyət vəsiqəsi / sürücülük vəsiqəsi / bank kartı (ISO/IEC 7810 ID-1) | 85.6 × 53.98 | Bəli |
| ID-2 | 105 × 74 | Bəli |
| Pasport səhifəsi (ID-3) | 125 × 88 | Bəli |
| Foto 3×4 | 30 × 40 | Xeyr |
| Foto 10×15 | 100 × 150 | Xeyr |
| A6 / A5 / A4 | 105×148 / 148×210 / 210×297 | Xeyr (istifadəçi "Həssas" işarələyə bilər) |
| Xüsusi | istifadəçi daxil edir (mm) | İstifadəçi seçir |

- İlk skan zamanı məlumat pəncərəsi (bir dəfəlik razılıq): şəxsi sənədlərin şifrəli saxlanıldığı, avtomatik silinmə seçimi və məxfilik siyasətinə link.
- Skan faylı `files`-a `Source=Scan`, `PhysicalWidthMm`, `PhysicalHeightMm`, `IsSensitive` metadata ilə şəkil (JPEG, 300 DPI) kimi yazılır. Həssas fayllar üçün default avtomatik silinmə `Privacy.SensitiveAutoDeleteDefault` (Bölmə 2.13).

**Səhifə tərtibatı redaktoru (mobil və kiosk)**
- Açılış: skan/şəkil faylında "Səhifədə yerləşdir", və ya "Yeni tərtibat" → faylları seç (skanlar, şəkillər, PDF səhifələri şəkil kimi).
- Kağız: A4 / A3 (+ A5), portret / landşaft, kənar boşluq (`Layout.DefaultMarginMm`).
- Elementlər: sürüşdürmə, ölçü dəyişmə (nisbət kilidi default açıq), 90° və sərbəst fırlatma, **"Real ölçüyə qaytar"**, dublikat (məs. eyni vəsiqə × 4), sil, ön/arxa plan.
- Köməkçilər: mm xətkeşləri, 5 mm grid, kənara / mərkəzə / bir-birinə snap, düzləndirmə düymələri, çap sahəsindən kənara çıxma xəbərdarlığı (printer kənarı: printer bildirirsə o, yoxdursa 4 mm).
- Hazır şablonlar: "Vəsiqə ön + arxa (bir səhifədə, mərkəzdə, üst-alt)", "2 × 2", "8 kart (2 × 4)", "Foto 3×4 × 6", "Pasport səhifəsi mərkəzdə".
- Default davranış: ID preset-li skan A4-ə əlavə olunanda **real ölçüdə** yerləşdirilir (böyüdülmür). İstifadəçi istəsə böyüdə bilər.
- Nəticə: `LayoutProjectJson` (elementlər, mm koordinatları, fayl istinadları). PDF-i **paylaşılan .NET `NeoPrint.Layout` kitabxanası** (PDFsharp) yaradır: mobil üçün backend-də (`POST files/layout`), kioskda lokal. Şəkillər maks. 300 DPI. `files`-a `Source=Layout` kimi yazılır, "Tərtibatı redaktə et" ilə istənilən vaxt yenidən açılır. Tərtibatda həssas fayl varsa tərtibat da həssas sayılır.
- Redaktor UI: mobil — Flutter `CustomPainter` + jestlər; kiosk — Avalonia + Skia.
- Çap: tərtibat faylları üçün `Ölçüləmə = Real ölçü 100%` və kağız ölçüsü tərtibatdakı ilə **məcburidir** (PrintOptions-da dəyişdirilə bilməz, səbəbi yazılır). Windows: `PrintDocument` scaling söndürülür; Linux: `-o print-scaling=none -o fit-to-page=false`.
- Kiosk: kamera skanı yoxdur; flash-dan gələn şəkillər üçün eyni redaktor. Anonim istifadəçinin tərtibat layihəsi saxlanılmır.

### 2.12 Fiskal qəbz (e-kassa)
Azərbaycanda nağdsız ödəniş qəbul edən terminal adətən onlayn kassa / fiskal çek tələb edir. NeoPrint-in öz elektron qəbzi **məlumat qəbzidir, fiskal deyil**. Ona görə arxitektura fiskal çeki əvvəlcədən nəzərə alır (konkret provayder və tələblər hüquqşünasla dəqiqləşdirilir — Bölmə 14).

- **`IFiscalProvider`** interface:
  - `IssueSaleAsync(FiscalSaleRequest)` — satış çeki (məhsul/xidmət sətirləri, məbləğ, ƏDV məlumatı, ödəniş növü, terminal/kassa ID)
  - `IssueRefundAsync(FiscalRefundRequest)` — qaytarma çeki (ilkin fiskal sənədə istinadla)
  - `GetStatusAsync(fiscalDocumentId)`
  - `HealthCheckAsync()`
- MVP-də `MockFiscalProvider` (fiskal nömrə, fiskal ID, yoxlama linki generasiya edir). Real provayder adapteri konfiqurasiya ilə qoşulur (`NEOPRINT_FISCAL_PROVIDER`, `NEOPRINT_FISCAL_*` env).
- Hər kiosk fiskal terminal/kassa ilə əlaqələndirilir: `kiosks.FiscalTerminalId` (admin paneldə). Mobil satışlar üçün ayrıca "onlayn satış" terminalı (`Fiscal.OnlineTerminalId`, admin paneldə).
- **Nə vaxt fiskal çek:**
  - Çap ödənişi (kiosk və ya mobil, Wallet / kart / bank QR) uğurlu olanda → satış çeki.
  - Refund → qaytarma çeki.
  - Wallet artırılması → `Fiscal.TopUpReceiptMode`-a görə: `OnTopUp` (artırılanda avans çeki) və ya `OnService` (Wallet-dən ödənişlə xidmət göstəriləndə). Default `OnTopUp`; qərar hüquqşünasla verilir.
  - Bonus səhifə ilə ödənilən hissə — 0 AZN, fiskal çekə düşmür (hüquqşünas təsdiqi, Bölmə 14).
- `fiscal_receipts` kolleksiyası: `Id`, `Type (Sale/Refund/TopUp)`, `PaymentId?`, `RefundId?`, `KioskId?`, `TerminalId`, `Amount`, `Lines[]`, `Status (Pending/Issued/Failed)`, `FiscalDocumentNumber`, `FiscalId`, `VerificationUrl`, `IssuedAt`, `Attempts`, `LastError`.
- Fiskallaşdırma **asinxron** işləyir (MassTransit outbox + `FiscalizationConsumer`), uğursuzluqda eksponensial retry. `Fiscal.MaxPendingMinutes` keçərsə incident (High). Ödəniş axını fiskal provayder gözlənilmədən davam edir (provayder uzun müddət işləmirsə admin `Fiscal.Enabled` və ya kiosku dayandırmaq barədə qərar verir).
- NeoPrint elektron qəbzi (PDF) fiskal məlumatları (nömrə, fiskal ID, yoxlama QR-ı) daxil edir; fiskal çek hələ hazır deyilsə qəbzdə "Fiskal çek hazırlanır" yazılır, hazır olanda qəbz yenilənir və email verilibsə yenidən göndərilir.
- Kiosk Receipt ekranında fiskal çekin QR-ı göstərilir (hazırdırsa).
- Admin panel: "Fiskal çeklər" səhifəsi — siyahı, status filtri, uğursuzları təkrarla, ödəniş/refund-a keçid.

### 2.13 Şifrələmə, secret-lər və fərdi məlumatların qorunması

**Secret-lər — OpenBao**
- Backend (API və Worker-lər) bütün secret-ləri **OpenBao**-dan oxuyur (MPL-2.0, Linux Foundation — HashiCorp Vault-un açıq mənbəli fork-u, API-si Vault ilə uyğundur, tamamilə pulsuzdur). OpenBao Docker-də ayrıca servis kimi qaldırılır (Bölmə 4). Klient kodu yalnız standart HTTP API-dən istifadə edir.
- **Klient:** öz yüngül klientimiz (`NeoPrint.Infrastructure/SecretStore`, `HttpClient` ilə) — AppRole login, KV v2 oxuma, token yenilənməsi (renew), token bitəndə yenidən login. Əlavə kitabxana yoxdur.
- **Konfiqurasiya provayderi:** `OpenBaoConfigurationProvider` (`IConfigurationProvider`) tətbiq başlayanda OpenBao-dakı dəyərləri `IConfiguration`-a yükləyir; açar adları aşağıdakı cədvəldəki kimidir, kodun qalan hissəsi dəyərin haradan gəldiyini bilmir. Hər 5 dəqiqədən bir yenidən oxuyur (`IOptionsMonitor` ilə rotasiya olunan secret-lər — məs. SMTP, Telegram, provayder açarları — restart-sız yenilənir).
- **Environment variable-da qalanlar (yalnız bootstrap):** `BAO_ADDR`, `BAO_ROLE_ID`, `BAO_SECRET_ID_FILE` (secret_id Docker secret faylı kimi mount olunur, env-də açıq yazılmır), `BAO_KV_MOUNT` (default `neoprint`), `BAO_CACERT` (Production TLS).
- **Autentifikasiya:** AppRole. Rollar: `neoprint-api`, `neoprint-worker`. Hər rolun policy-si yalnız öz yollarını **oxuya** bilər (write yoxdur). Token TTL 1 saat, renew edilə bilən.
- **Başlanğıc davranışı:** OpenBao əlçatmazdırsa 60 saniyə ərzində eksponensial retry; sonra Production-da tətbiq **başlamır**. Məcburi açar OpenBao-da yoxdursa tətbiq başlamır və hansı açarın (dəyər olmadan) çatışmadığını log-a yazır. İş zamanı OpenBao qopsa son yüklənmiş dəyərlərlə davam edir, incident (High) yaradılır.
- **Audit:** OpenBao audit device (file) aktivdir.

**OpenBao yolları (KV v2, mount `neoprint`)**
| Yol | Açarlar |
|---|---|
| `neoprint/backend/core` | `NEOPRINT_MONGO_URI`, `NEOPRINT_REDIS`, `NEOPRINT_RABBITMQ_URI`, `NEOPRINT_JWT_SIGNING_KEY` |
| `neoprint/backend/crypto` | `NEOPRINT_KEK_CURRENT_ID`, `NEOPRINT_KEK__{id}` (base64, 256-bit; köhnə açarlar da saxlanılır), `NEOPRINT_STORAGE_SECRETS_KEY`, `NEOPRINT_DATA_PROTECTION_KEY`, `NEOPRINT_QR_HMAC_KEY` |
| `neoprint/backend/providers` | `NEOPRINT_SMTP_*`, `NEOPRINT_FCM_SERVICE_ACCOUNT_JSON`, `NEOPRINT_TELEGRAM_BOT_TOKEN`, `NEOPRINT_FISCAL_*`, `NEOPRINT_BANK_*` |
| `neoprint/backend/seed` | `NEOPRINT_STORAGE_SEED_*`, `NEOPRINT_SEED_ADMIN_EMAIL`, `NEOPRINT_SEED_ADMIN_PASSWORD` |
| `neoprint/backend/backup` (Faza 2) | `NEOPRINT_BACKUP_KEY` |

- Policy-lər: `neoprint-api` → `core`, `crypto`, `providers`, `seed` (read); `neoprint-worker` → `core`, `crypto`, `providers` (read), Faza 2-də `backup`.
- Storage provayderlərinin admin paneldən daxil edilən açarları Mongo-da (`NEOPRINT_STORAGE_SECRETS_KEY` ilə şifrəli) qalır — onları admin idarə edir, OpenBao-ya yazılmır.
- **KEK rotasiyası:** yeni `NEOPRINT_KEK__{id}` OpenBao-ya əlavə edilir, `NEOPRINT_KEK_CURRENT_ID` dəyişdirilir → provayder 5 dəqiqə ərzində oxuyur → `KeyRewrapJob` başladılır.

**Development**
- `docker compose up` zamanı `openbao` (server rejimi, fayl storage, volume-da) və birdəfəlik `openbao-init` konteyneri qalxır. `openbao-init`:
  1. OpenBao inisializasiya olunmayıbsa init edir, unseal açarını və root token-i **yalnız lokal** `./.openbao/` qovluğuna yazır (`.gitignore`-da);
  2. Hər başlanğıcda unseal edir;
  3. KV v2 mount `neoprint`, AppRole, policy-lər, audit device qurur;
  4. `.env.openbao-seed` faylındakı development dəyərlərini müvafiq yollara yazır (yalnız yol boşdursa; mövcud dəyəri üstələmir);
  5. `neoprint-api` / `neoprint-worker` üçün role_id-ni və secret_id faylını `./.openbao/` qovluğuna yazır — `api` konteyneri onları mount edir.
- Repo-da yalnız `.env.openbao-seed.example` (saxta dəyərlərlə) var; `.env.openbao-seed` və `.openbao/` `.gitignore`-dadır.

**Production** (`docs/OPENBAO.md`)
- OpenBao ayrıca server / klaster (Raft storage), TLS məcburi, auto-unseal (Transit və ya KMS) və ya manual unseal proseduru, unseal açarlarının bölüşdürülməsi (Shamir, 3/5).
- Secret-lər əməliyyat komandası tərəfindən `bao kv put` ilə yazılır; `openbao-init` Production-da işləmir.
- secret_id qısa ömürlü (CIDR bağlı, istifadə sayı limitli) yaradılır və deploy zamanı Docker secret kimi ötürülür.
- Snapshot backup (`bao operator raft snapshot save`) və bərpa təlimatı.

**Kiosk kimliyi**
- Kiosklar OpenBao-ya qoşulmur və **heç bir secret env-i istifadə etmir**. Kimlik cihazın öz yaratdığı açar cütüdür (Bölmə 7.11): private açar cihazdan çıxmır, backend-də yalnız public açar saxlanılır.
- Kioskun bütün konfiqurasiyası (limitlər, qiymətlər, printer, admin PIN hash-i, flag-lar və s.) backend-dən `GET kiosks/me/config` ilə gəlir; kioska heç bir backend secret-i ötürülmür.
- Cihaz admin paneldən istənilən vaxt ləğv edilə bilər (`Cihazı ayır`) — dərhal etibarsız olur.

**Mobil və admin web** — heç bir secret saxlamır (yalnız istifadəçi token-ləri).

- Log-larda secret, token, fayl məzmunu və şəxsi məlumat (email-dən başqa) yazılmır.

**Faylların şifrələnməsi (at rest)**
- **Bütün** obyektlər (istifadəçi faylları, skanlar, tərtibatlar, versiyalar, preview PDF-lər, qəbzlər, temp fayllar) storage-a yazılmazdan əvvəl tətbiq səviyyəsində şifrələnir — **envelope encryption**:
  - Hər obyekt üçün təsadüfi 256-bit DEK; məzmun **AES-256-GCM** ilə 1 MB-lıq chunk-lar şəklində (hər chunk-un öz nonce-u və auth tag-ı; chunk sırası AAD-a daxildir) — böyük fayllar stream kimi şifrələnir/açılır.
  - DEK aktiv KEK ilə AES-GCM ilə "wrap" olunur; obyekt başlığında: format versiyası, `KekId`, wrapped DEK, chunk ölçüsü.
  - `files.Encryption {KekId, Algorithm}` metadata-da saxlanılır.
- **Açar rotasiyası:** yeni `NEOPRINT_KEK__{id}` əlavə edilib `NEOPRINT_KEK_CURRENT_ID` dəyişdirilir → yeni yazılar yeni açarla; `KeyRewrapJob` köhnə obyektlərin yalnız DEK-lərini yenidən wrap edir (məzmun yenidən şifrələnmir). Köhnə KEK bütün obyektlər keçənə qədər env-də qalır.
- Presigned URL istifadə olunmur (məzmun şifrəlidir): fayllar yalnız backend vasitəsilə (icazə yoxlaması + deşifrə, stream) verilir.
- Kiosk: lokal spool / temp fayllar AES-GCM ilə şifrələnir; açar cihazda qorunan lokal açardır (Windows DPAPI `LocalMachine`, Linux `600` fayl, sahibi `neoprint`) — beləliklə restart / yeniləmədən sonra `Queued` job-ların faylları itmir (Bölmə 7.12, bərpa axını). Fayllar job bitən kimi silinir; job-a bağlı olmayan köhnə spool faylları (24 saatdan köhnə) agent tərəfindən silinir.
- MongoDB-də həssas konfiqurasiya sahələri (storage secret-ləri, fiskal credential-lar) AES-GCM ilə şifrəlidir.

**Fərdi məlumatlar**
- **Razılıq:** qeydiyyatda məxfilik siyasəti + istifadə şərtləri; ilk sənəd skanında əlavə məlumat/razılıq; kioskda ödəniş ekranında qısa məlumat və məxfilik siyasətinə QR link. Sənədlər versiyalı (`legal_documents`), qəbullar `consents`-də (`UserId`, `DocType`, `Version`, `AcceptedAt`, `Ip`, `DeviceId`).
- **Avtomatik silinmə:** hər fayl üçün seçim — `Heç vaxt` / `Çapdan sonra` / `N gün sonra`. Həssas fayllar üçün default `Privacy.SensitiveAutoDeleteDefault` (`AfterPrint`). `Çapdan sonra` seçilmiş fayl həmin faylla bağlı job `Completed` olan kimi (planlanmış çaplar varsa sonuncudan sonra) silinir; `FileAutoDeleteJob`.
- **Admin fayl məzmununa baxa bilmir:** admin API-də istifadəçi fayllarını endirmə / baxış endpoint-i yoxdur, yalnız metadata (ad maskalanmış, ölçü, tip, tarix).
- **"Məlumatlarımı yüklə":** mobil tətbiqdə profil, ödəniş tarixçəsi, qəbzlər (JSON + PDF) və fayllar ZIP kimi hazırlanır (asinxron, hazır olanda push + 24 saat keçərli link).
- **"Hesabımı sil":**
  - Şərt: aktiv `Queued`/`Printing` job və ya aktiv planlanmış çap yoxdur (əvvəlcə ləğv edilməlidir — ekranda göstərilir).
  - Xəbərdarlıq: **Wallet balansı və bonus səhifələr itir, geri qaytarılmır** (Bölmə 2.14); təsdiq üçün şifrə + "SİL" yazmaq.
  - Nəticə: hesab dərhal deaktiv olur, sessiyalar/FCM token-lər silinir; `Privacy.AccountDeletionGraceHours` ərzində bütün fayllar, skanlar, versiyalar bütün storage provayderlərindən fiziki silinir; profil anonimləşdirilir (email → hash, ad silinir).
  - Maliyyə qeydləri (ödənişlər, refund-lar, fiskal çeklər, qəbzlər) qanuni saxlama üçün anonimləşdirilmiş şəkildə qalır.
- Sənəd layihələri: `docs/legal/PRIVACY_POLICY.az.md`, `docs/legal/TERMS.az.md` (qaralama, "hüquqşünas təsdiqi tələb olunur" qeydi ilə) — tətbiqlərdə və kioskda göstərilir.

### 2.14 Balans (Wallet) qaydaları
- **Ledger prinsipi:** həqiqət mənbəyi `wallet_transactions` (ledger) sətirləridir; `wallets.Balance` yalnız hesablanmış (materialized) dəyərdir və hər əməliyyatda ledger ilə birlikdə, eyni transaction daxilində yenilənir. `Balance += x` tipli birbaşa dəyişiklik qadağandır; uzlaşdırma job-u fərq tapsa incident yaradır.
- Wallet balansı **əvvəlcədən ödənilmiş xidmət krediti (avans)** kimi işləyir və **heç bir halda geri qaytarılmır / nağdlaşdırılmır / karta çıxarılmır** — nə istifadəçi, nə admin tərəfindən.
- Wallet-ə düşən refund-lar da balansın bir hissəsi olur və eyni qaydaya tabedir.
- Bu qayda İstifadə şərtlərində açıq yazılır və balans artırma ekranında göstərilir ("Balansa yüklənən məbləğ geri qaytarılmır").
- Admin `wallets/{userId}/adjust` yalnız balansı düzəldə bilər (xəta düzəlişi, kompensasiya) — səbəb məcburi, audit log-a düşür; kənara ödəniş yaratmır.
- Hesab silinəndə balans itir (Bölmə 2.13).
- Wallet artırılmasının vergi / fiskal tərəfi `Fiscal.TopUpReceiptMode` ilə idarə olunur (Bölmə 2.12, 14).
- Voucher-lər (anonim fallback refund) aktivləşdirilən kimi Wallet-ə köçür və bu qaydaya tabe olur.

### 2.15 Kağız, sərf materialları və servis modulu

**Kağız sayğacı**
- Hər kioskda kağız ölçüsünə görə sayğac: `kiosks.PaperStock {A4: {Capacity, Current, LastRefillAt, LastRefillBy}, A3: {...}}`.
- Hər job tamamlananda (və ya hissəvi) kiosk faktiki çap olunmuş **vərəq** sayını (`PrintedSheets` — ikitərəflidə 2 üz = 1 vərəq) göndərir; sayğac azalır. Test çaplar da sayılır.
- Printer lotok səviyyəsini bildirirsə (telemetriya), paneldə sayğacla yanaşı göstərilir; fərq böyükdürsə xəbərdarlıq.
- **Job qəbulundan əvvəl yoxlama:** job üçün lazım olan vərəq > `Current` olarsa və `Paper.BlockWhenInsufficient = true` isə sifariş qəbul edilmir ("Bu kioskda hazırda kifayət qədər kağız yoxdur"), mobil tətbiqdə kiosk seçimində göstərilir, incident yaranır.
- **Doldurma:** Operator panelində "Kağız doldur" — əlavə edilən vərəq sayı, "Tam doldur" (Capacity-yə qədər) və ya "Dəqiq say" (əl ilə sayılmış). Hər əməliyyat `supply_events`-ə və anbar hərəkətinə yazılır. Kapasiteti admin kiosk redaktəsində təyin edir.

**Xəbərdarlıq qaydaları (admin yaradır)** — `supply_alert_rules`
- Sahələr: `Id`, `Name`, `Scope (Global / KioskIds[])`, `Resource (PaperA4 / PaperA3 / Toner:{color} / Drum / WasteToner / Other)`, `Unit (Sheets / Percent)`, `WarningThreshold`, `CriticalThreshold`, `Channels (AdminPanel / OperatorPanel / Email / Telegram)`, `CreateServiceTask (bool)`, `TaskPriority`, `IsActive`.
- Kiosk üçün qayda varsa qlobal qaydanı əvəz edir.
- Hədd keçiləndə: `Warning` və ya `Critical` bildiriş (dublikat yoxdur — səviyyə dəyişənə qədər təkrar göndərilmir), istəyə görə avtomatik servis tapşırığı. Səviyyə hədddən yuxarı qalxanda bildiriş avtomatik bağlanır.
- Seed qaydaları: Kağız A4 — Warning 200, Critical 50 vərəq; Toner — Warning 15%, Critical 3%.

**Proqnoz**
- Son 14 günün gündəlik sərfi (vərəq/gün, toner %/vərəq) əsasında çəkili orta → **təxmini bitmə vaxtı** (kağız və hər toner üçün).
- Admin və Operator panelində göstərilir. Bitməyə `Service.ForecastLeadHours` qalanda avtomatik servis tapşırığı yaradılır (hədd hələ keçilməsə də).

**Servis tapşırıqları** — `service_tasks`
- Sahələr: `Id`, `KioskId`, `Type (RefillPaper/ReplaceToner/ReplaceDrum/PrinterRepair/Cleaning/Other)`, `Priority (Low/Normal/High/Critical)`, `Status (Open/Assigned/InProgress/Done/Cancelled)`, `Source (AlertRule/Forecast/Incident/Manual)`, `AssignedTo?`, `DueAt`, `Notes`, `CompletionData {SheetsAdded?, InventoryItemId?, Quantity?}`, `CreatedAt`, `StartedAt`, `CompletedAt`.
- Təyinat: kiosk əhatəsinə (kiosk scope) həmin kiosk düşən və `service.tasks.complete` icazəsi olan operatorlardan ən az aktiv tapşırığı olana avtomatik; yoxdursa ümumi hovuzda qalır. Admin əl ilə yenidən təyin edə bilər.
- Bildiriş: təyin olunan operatora Operator panelində (real-time + səs), email, Telegram.
- `Critical` tapşırıq `Service.TaskOverdueEscalationMinutes` ərzində başlanmasa adminlərə eskalasiya.
- Tapşırıq tamamlananda: kağız sayğacı / toner qeydi yenilənir, anbardan çıxılır.

**Anbar (inventar)** — `inventory_items`, `inventory_movements`
- `inventory_items`: `Id`, `Sku`, `Name`, `Type (PaperA4Ream/PaperA3Ream/Toner/Drum/Other)`, `CompatiblePrinterModels[]`, `Unit`, `SheetsPerUnit?`, `MinStock`, `Stock`, `Location (Warehouse)`.
- `inventory_movements`: `ItemId`, `Quantity`, `Type (Purchase/IssueToOperator/UsedAtKiosk/Adjustment/WriteOff)`, `KioskId?`, `TaskId?`, `By`, `At`, `Note`.
- `Stock < MinStock` → admin panelində xəbərdarlıq (+ email).
- Toner dəyişdiriləndə printer modeli ilə uyğun kartric siyahısı təklif olunur.

### 2.16 Feature flag-lar
- `feature_flags`: `Key`, `Description`, `DefaultEnabled`, `Platforms[] (Kiosk/Mobile/Admin/Backend)`, `KioskOverrides {kioskId: bool}`, `UserAllowlist[]`, `RolloutPercent` (mobil istifadəçilər, `hash(userId+key)` ilə sabit), `UpdatedBy`, `UpdatedAt`.
- Qiymətləndirmə backend-də: kiosk `GET kiosks/me/config` ilə alır (+ SignalR `FeatureFlagsChanged`), mobil `GET features`, admin `GET admin/features/effective`. Backend endpoint-ləri `[RequireFeature("key")]` ilə qorunur.
- MVP flag-ları (default hamısı açıq, pilotda lazım olanlar söndürülə bilər): `mobile_payment_at_kiosk`, `user_qr_print`, `mobile_session_print`, `scheduled_print`, `file_sharing`, `referral`, `promo_codes`, `layout_editor`, `document_scan`, `txt_editor`, `data_export`.
- Admin panelində "Feature flag-lar" səhifəsi: kiosk-kiosk açma/bağlama, faiz, allowlist, dəyişiklik tarixçəsi (audit).

### 2.17 Pilot və analitika
- **Pilot planı:** 1 kiosk, 1 məkan, 2–3 həftə. Pilot başa çatmadan kiosk sayı artırılmır.
- Pilotda cavab axtarılan suallar: hansı formatlar gəlir, konvertasiya harada uğursuz olur, istifadəçi hansı ekranda imtina edir, ödəniş harada ilişir, orta əməliyyat müddəti, kağız/toner sərfi.
- **Öz analitikamız** (üçüncü tərəf analitika yoxdur): `analytics_events` (Mongo time-series, TTL `Analytics.RetentionDays`): `Timestamp`, `Source (Kiosk/Mobile)`, `KioskId?`, `UserId?` (anonim üçün yox), `SessionId`, `Event`, `Props`.
- Hadisələr: `screen_view`, `file_selected {format, sizeMb, pages}`, `conversion_failed {format, error}`, `options_set`, `price_shown`, `payment_started {method}`, `payment_succeeded/failed/expired {method, seconds}`, `print_completed/failed {sheets}`, `flow_abandoned {lastScreen, seconds}`, `refund_created {reason}`.
- Admin panelində **"Pilot hesabatı"**: format paylanması, konvertasiya xətaları, ekran-ekran funnel (itki faizləri), ödəniş metodlarına görə uğur/xəta, orta müddətlər, gündəlik job/gəlir, kağız sərfi. CSV export.
- `docs/PILOT.md`: quraşdırma yoxlama siyahısı, gündəlik baxış siyahısı, uğur meyarları, pilotdan sonra qərar şablonu.

---

## 3. Repo strukturu

```
NeoPrint/
├── backend/
│   ├── NeoPrint.sln
│   ├── src/
│   │   ├── NeoPrint.Api/              # Controllers, SignalR Hubs, Middleware, Program.cs
│   │   ├── NeoPrint.Application/      # CQRS (MediatR 12), validator-lar, interface-lər
│   │   ├── NeoPrint.Domain/           # Entities, Enums, ValueObjects, DomainEvents
│   │   ├── NeoPrint.Infrastructure/   # Mongo, Redis, S3, MassTransit (RabbitMQ + Mongo outbox), FCM, Email, Crypto, Providers
│   │   ├── NeoPrint.Workers/          # Hangfire job-ları, MassTransit consumer-ləri
│   │   └── NeoPrint.Contracts/        # Paylaşılan DTO və mesaj kontraktları (desktop istifadə edir)
│   ├── shared/
│   │   ├── NeoPrint.Pdfium/           # Öz PDFium P/Invoke wrapper-i (backend + kiosk)
│   │   ├── NeoPrint.Layout/           # Tərtibat → PDF renderer (backend + kiosk)
│   │   ├── NeoPrint.Pricing/          # Qiymət hesablaması (backend + kiosk preview)
│   │   └── NeoPrint.Crypto/           # Envelope encryption (AES-GCM chunked)
│   ├── tests/
│   │   ├── NeoPrint.Domain.Tests/
│   │   ├── NeoPrint.Application.Tests/
│   │   ├── NeoPrint.Shared.Tests/
│   │   ├── NeoPrint.Api.IntegrationTests/   # Testcontainers
│   │   └── fixtures/formats/                # Hər format qrupundan nümunə fayllar
│   ├── Dockerfile
│   └── docker-compose.yml
├── desktop/
│   ├── NeoPrint.Kiosk.sln
│   ├── src/
│   │   ├── NeoPrint.Kiosk/            # Avalonia App, Views, ViewModels, ekran klaviaturası
│   │   ├── NeoPrint.Kiosk.Core/       # ApiClient (HttpClient), SignalR client, USB, QR reader, Settings, Telemetry, Analytics
│   │   ├── NeoPrint.Kiosk.Printing/   # Render, redaktə, konvertasiya (OS-dən asılı deyil)
│   │   ├── NeoPrint.Kiosk.Platform.Abstractions/ # IPrinterService, IRemovableDriveWatcher, IKioskLockdown, IAppPaths, ISystemControl, IDocumentConverter, ISystemMetrics
│   │   ├── NeoPrint.Kiosk.Platform.Windows/      # net8.0-windows implementasiyaları
│   │   └── NeoPrint.Kiosk.Platform.Linux/        # CUPS, mount watcher, systemd implementasiyaları
│   │   └── NeoPrint.Kiosk.Agent/      # Sistem servisi: watchdog, updater, əmrlər, log yükləmə (Windows Service / systemd)
│   ├── provisioning/
│   │   ├── windows/provision.ps1, prepare-image.ps1, policies/ (registry, Shell Launcher, Keyboard Filter)
│   │   └── linux/provision.sh, prepare-image.sh, systemd/ (neoprint-agent.service, kiosk autologin), polkit/, logind.conf.d/
│   └── tests/NeoPrint.Kiosk.Tests/
├── mobile/
│   ├── pubspec.yaml
│   ├── android/ ios/                  # Öz platform channel-lar: secure_storage, share, document_scanner
│   ├── lib/
│   │   ├── main.dart
│   │   ├── core/        # di, router, network, auth, secure_storage, signalr (öz klient), theme, l10n, analytics, features (flag-lar)
│   │   ├── shared/      # widgets, models
│   │   └── features/
│   │       ├── auth/  consents/  kiosk_search/  session/  files/  print/  payment/
│   │       ├── schedule/  wallet/  receipts/  referral/  promo/  share/
│   │       ├── scanner/  layout_editor/  privacy/
│   │       └── notifications/  profile/
│   └── test/
├── admin/                    # Angular admin web + operator paneli
│   ├── package.json
│   ├── angular.json
│   ├── Dockerfile            # build + nginx
│   ├── nginx.conf
│   └── src/app/
│       ├── core/             # auth (interceptor, guard, permission directive), api, signalr, i18n, layout
│       ├── shared/           # components, pipes, models
│       ├── features/
│       │   ├── dashboard/  kiosks/  printers-overview/  queue/  print-jobs/  payments/  refunds/
│       │   ├── fiscal/  users/  wallets/  pricing-profiles/  settings/  promos/  referrals/
│       │   ├── incidents/  receipts/  vouchers/  supply-rules/  service-tasks/  inventory/
│       │   ├── feature-flags/  analytics/  legal-documents/  audit-logs/  admin-users/  roles/
│       │   └── storage/  file-formats/
│       └── operator/         # Operator paneli (mobil-yönümlü, PWA)
├── tools/
│   └── license-check/        # Lisenziya yoxlama skripti
├── docs/
│   ├── README.md (prioritet)  NeoPrint-PLAN.md  DECISIONS.md  THIRD_PARTY.md  OPENBAO.md  API.md
│   ├── KIOSK_SETUP.md  KIOSK_PROVISIONING.md  ADMIN_GUIDE.md  OPERATOR_GUIDE.md  PILOT.md
│   ├── ARCHITECTURE_RULES.md  STATE_MACHINES.md  IDEMPOTENCY.md  PAYMENT_FLOWS.md  FAILURE_RECOVERY.md
│   ├── OFFLINE_BEHAVIOR.md  SECURITY_THREAT_MODEL.md  DOMAIN_RULES.md  DATA_RETENTION.md  OBSERVABILITY.md
│   ├── CLAUDE_CODE_EXECUTION_RULES.md  adr/TEMPLATE.md  adr/ADR-XXXX-*.md
│   └── legal/PRIVACY_POLICY.az.md  legal/TERMS.az.md
├── .env.openbao-seed.example   # Development OpenBao seed dəyərləri (saxta)
├── openbao/                    # openbao-init skripti, policy-lər (HCL), server konfiqi
├── .github/workflows/ci.yml
├── .gitignore
└── README.md
```

---

## 4. Texnologiya

Paketlər yalnız Bölmə 2.0 siyahısından. Burada hər hissənin ümumi quruluşu:

- **Backend:** .NET 8, ASP.NET Core Web API, SignalR (Redis backplane), MediatR 12, MassTransit 8 (RabbitMQ + Mongo outbox), Hangfire (+ Hangfire.Mongo), MongoDB.Driver, StackExchange.Redis, AWSSDK.S3, MailKit, QuestPDF, PDFsharp, SkiaSharp, NeoPrint.Pdfium, QRCoder. Docker image: LibreOffice + Ghostscript (+ GhostPCL, GhostXPS) + fontlar (`fonts-dejavu`, `fonts-liberation`, `fonts-crosextra-carlito`, `fonts-crosextra-caladea`) + PDFium binary.
- **Kiosk:** UI (`NeoPrint.Kiosk`) + Agent (`NeoPrint.Kiosk.Agent`, .NET Worker Service — Windows Service / systemd). Avalonia 11, CommunityToolkit.Mvvm, SignalR client, PDFsharp, SkiaSharp, NeoPrint.Pdfium, QRCoder, System.Management (Windows), Lextm.SharpSnmpLib. Kioskda quraşdırılır: LibreOffice, Ghostscript (+ GhostPCL, GhostXPS), (Linux) CUPS, udisks2, cage, fontlar.
- **Mobile:** Flutter 3 / Dart 3, flutter_riverpod, go_router, dio, web_socket_channel (öz SignalR klienti), camera + google_mlkit_barcode_scanning, geolocator, flutter_map, file_picker, image, firebase_core, firebase_messaging, intl; öz platform channel-lar.
- **Monorepo paylaşımı:** `desktop/` layihələri `backend/src/NeoPrint.Contracts` və `backend/shared/*` layihələrinə `ProjectReference` ilə bağlanır (ayrıca NuGet paketi yoxdur).
- **Admin:** Angular (standalone, signals, new control flow, TypeScript strict), Angular Material/CDK, Angular Service Worker (Operator PWA), @microsoft/signalr, @ngx-translate/core, chart.js, leaflet. Test: Angular CLI-nin default test runner-i; lint: angular-eslint.

### Docker Compose (development)
| Servis | Image | Port |
|---|---|---|
| api | local build | 5080 |
| openbao | `openbao/openbao` (pinlənmiş tag; server rejimi, fayl storage, volume) | 8200 |
| openbao-init | `openbao/openbao` (birdəfəlik: init, unseal, mount, AppRole, policy, dev seed) | — |
| mongo | `mongo:7` — **tək node-lu replica set** (`--replSet rs0` + init skripti; transaction-lar və MassTransit outbox üçün məcburidir) | 27017 |
| redis | `redis:7.2` | 6379 |
| minio | `minio/minio` (pinlənmiş tag) | 9000 / 9001 |
| scality | `zenko/cloudserver` (Scality S3 server, development/test üçün) | 8000 |
| rabbitmq | `rabbitmq:3-management` | 5672 / 15672 |
| mailpit | `axllent/mailpit` | 1025 / 8025 |
| admin | local build (nginx) | 8080 (development-də `ng serve` 4200, `proxy.conf.json` ilə API-yə) |

Backend secret-ləri OpenBao-dan; `api` konteyneri yalnız `BAO_ADDR`, role_id və secret_id faylını alır, `openbao-init` tamamlanandan sonra başlayır (`depends_on: condition: service_completed_successfully`). Mongo, Redis, RabbitMQ, MinIO konteynerlərinin öz development parolları `.env.openbao-seed`-dən götürülür. Faza 2-də əlavə olunur: `clamav`, `otel-collector`, `prometheus`, `grafana`, `glitchtip`, `collabora`.

---

## 5. Domen modeli (MongoDB kolleksiyaları)

| Kolleksiya | Əsas sahələr |
|---|---|
| `users` | `Id (Guid)`, `PublicId (NP-XXXXXX)`, `Email`, `PasswordHash`, `FullName`, `ReferralCode`, `ReferredBy`, `EmailConfirmed`, `DeviceIds[]`, `FcmTokens[]`, `Language`, `Status (Active/Blocked/Deleted)`, `DeletedAt?`, `CreatedAt` |
| `consents` | `UserId`, `DocType (Privacy/Terms/ScanNotice)`, `Version`, `AcceptedAt`, `Ip`, `DeviceId` |
| `legal_documents` | `DocType`, `Version`, `Language`, `Content (md)`, `PublishedAt`, `PublishedBy` |
| `wallets` | `UserId`, `Balance`, `BonusPages`, `Version` (optimistic concurrency) |
| `wallet_transactions` | `UserId`, `Type (TopUp/Payment/Refund/Bonus/VoucherRedeem/AdminAdjust)`, `Amount`, `BonusPages`, `RefId`, `CreatedAt` |
| `kiosks` | `Id`, `Code (KSK-0001)`, `Name`, `Location {Lat,Lng,Address}`, `Status (NotActivated/NotConfigured/Online/Paused/Offline/Maintenance/Disabled)`, `ActiveDeviceId?`, `AdminPinHash` (PBKDF2), `DefaultLanguage`, `Platform (Windows/Linux)`, `AppVersion`, `AvailablePrinters[] {Name, DriverName, IsOnline, DetectedCapabilities {Color, Duplex, A3}, LastSeenAt}`, `SelectedPrinter {Name, SelectedBy, SelectedAt}`, `CapabilityOverrides {Color?, Duplex?, A3?}`, `EffectiveCapabilities`, `QueueDisplayLimit?`, `MaxFileSizeMb?`, `MaxPages?`, `PricingProfileId?`, `PaperStock {A4, A3: {Capacity, Current, LastRefillAt, LastRefillBy}}`, `FiscalTerminalId?`, `LastHeartbeatAt` |
| `files` | `Id`, `OwnerId`, `OriginalName`, `Format`, `ContentType`, `SizeBytes`, `Sha256`, `PageCount`, `Source (Upload/Scan/Layout/Flash/Shared/Edited)`, `StorageProviderId`, `StorageKey`, `Encryption {KekId, Algorithm}`, `PreviewPdfKey`, `Versions[] {StorageKey, Sha256, CreatedAt}`, `PhysicalWidthMm?`, `PhysicalHeightMm?`, `LayoutProjectJson?`, `IsSensitive`, `AutoDelete {Mode (Never/AfterPrint/AfterDays), Days?}`, `DeleteAt?`, `CreatedAt` |
| `kiosk_devices` | `Id`, `PublicKey` (ECDSA P-256, SPKI), `Fingerprint {Hostname, OS, OsVersion, MachineId, MacAddresses[], Cpu, RamGb, ScreenResolution}`, `Platform`, `AppVersion`, `Status (Pending/Active/Revoked/Expired)`, `PairingCodeHash`, `PairingCodeExpiresAt`, `KioskId?`, `ActivatedBy?`, `ActivatedAt?`, `RevokedBy?`, `RevokedAt?`, `RevokeReason?`, `LastSeenAt`, `LastIp`, `ReportedPrinters[]` (aktivləşdirmədən əvvəl), `AgentVersion`, `ProvisioningVersion`, `UpdateStatus {TargetVersion, State (Idle/Downloading/Ready/Applying/HealthCheck/Succeeded/RolledBack/Failed), LastError, UpdatedAt}`, `EnrollmentTokenId?` |
| `enrollment_tokens` | `Id`, `TokenHash`, `KioskId?` (hazır kiosk qeydinə bağlı) / `NewKioskTemplate?`, `ExpiresAt`, `MaxUses (1)`, `UsedByDeviceId?`, `CreatedBy`, `CreatedAt`, `RevokedAt?` |
| `releases` | `Id`, `Version` (SemVer), `Component (App/Agent/Provisioning)`, `Platform (win-x64/linux-x64)`, `Channel (pilot/stable)`, `StorageKey`, `Sha256`, `Signature`, `MinAgentVersion`, `Notes`, `CreatedAt`, `IsWithdrawn` |
| `release_rollouts` | `Id`, `ReleaseId`, `Target (All / KioskIds[] / Percent / Channel)`, `Status (Active/Paused/Completed/RolledBack)`, `CreatedBy`, `CreatedAt` |
| `file_shares` | `Id`, `FileId`, `FromUserId`, `ToUserId`, `Message?`, `CreatedAt` |
| `print_jobs` | `Id`, `JobCode (6 rəqəm)`, `KioskId`, `UserId?`, `FileId?` / `LocalFileName`, `Options`, `PriceBreakdown {ProfileId, Base, VolumeDiscount, BonusPagesUsed, PromoDiscount, Total}`, `RequiredSheets`, `Source (Flash/MobileSession/UserQr/Scheduled)`, `Status`, `PaymentId`, `PrintedSides`, `PrintedSheets`, `ErrorReason?`, `QueuePosition`, `StatusHistory[]` |
| `payments` | `Id`, `JobId`, `UserId?`, `Method (BankQr/MobileApp/Wallet/Card/Bonus/Mixed)`, `Amount`, `BonusPagesUsed`, `PromoCodeId?`, `Discount`, `Status (Pending/Succeeded/Failed/Expired/Refunded/PartiallyRefunded/RefundFailed)`, `Channel (Kiosk/Mobile)`, `KioskId?`, `ProviderRef`, `RefundedAmount`, `FiscalReceiptId?`, `Timeline[] {At, Event, Details}`, `IdempotencyKey`, `ExpiresAt` |
| `refunds` | Bölmə 2.5 |
| `idempotency_keys` | `Key`, `ActorId`, `Operation`, `RequestHash`, `Status`, `ResponseSnapshot`, `CreatedAt`, `ExpiresAt` (24 saat) — eyni key + fərqli body → 409 |
| `provider_events` | `Provider`, `ProviderEventId`, `Type`, `PayloadHash`, `ProcessedAt` — `(Provider, ProviderEventId)` unikal indeks (webhook dublikatları) |
| `print_attempts` | `Id`, `JobId`, `AttemptNo`, `KioskId`, `PrinterName`, `StartedAt`, `FinishedAt`, `SpoolerJobId`, `PrinterCounterBefore`, `PrinterCounterAfter`, `ReportedSides`, `ReportedSheets`, `Status`, `ErrorCode` — "çap olubmu?" sualı bu qeydlər və printer sayğacı ilə cavablandırılır |
| `state_transitions` | `EntityType`, `EntityId`, `FromStatus`, `ToStatus`, `ActorType`, `ActorId`, `DeviceId?`, `Reason`, `CorrelationId`, `CreatedAt` (böyük entity-lərdə `StatusHistory[]` əvəzinə ayrıca kolleksiya) |
| `fiscal_receipts` | Bölmə 2.12 |
| `receipts` | `Id`, `Number (NP-2026-000001)`, `PaymentId`, `UserId?`, `FiscalReceiptId?`, `PdfStorageKey`, `EmailedTo[]` |
| `scheduled_prints` | `Id`, `UserId`, `KioskId`, `FileId`, `Options`, `PriceBreakdown`, `ScheduledAt`, `PaymentId`, `Status (Scheduled/AwaitingUser/Sent/Cancelled/Expired)` |
| `kiosk_sessions` | Redis-də: `session:{id}` → `{UserId, KioskId, ExpiresAt}` |
| `referrals` | `InviterId`, `InviteeId`, `Status (Registered/Rewarded)`, `RewardedAt` |
| `promo_codes` | Bölmə 2.7 sahələri + `UsedCount` |
| `promo_usages` | `PromoCodeId`, `UserId?`, `PaymentId` |
| `refund_vouchers` | `Code`, `Amount`, `PaymentId`, `RedeemedBy?`, `ExpiresAt` |
| `pricing_profiles` | Bölmə 2.3 |
| `incidents` | `Id`, `KioskId`, `Type (PrinterOffline/PaperOut/PaperLow/TonerLow/PaperJam/ConnectionLost/AppCrash/NoPrinterDetected/SelectedPrinterMissing/FiscalFailed/RefundFailed/StorageUnhealthy/Other)`, `Severity (Critical/High/Medium)`, `Status (Open/Acknowledged/Resolved)`, `Details`, `CreatedAt`, `ResolvedAt` |
| `supply_alert_rules`, `supply_alerts`, `supply_events` | Bölmə 2.15 (`supply_alerts`: aktiv Warning/Critical vəziyyətləri; `supply_events`: doldurma, dəyişmə, sayğac düzəlişləri) |
| `service_tasks` | Bölmə 2.15 |
| `inventory_items`, `inventory_movements` | Bölmə 2.15 |
| `feature_flags` | Bölmə 2.16 |
| `analytics_events` | Bölmə 2.17 (time-series) |
| `admin_settings` | Bölmə 2.4 açar-dəyər |
| `admin_roles` | `Id`, `Name`, `Description`, `Permissions[]`, `IsSystem`, `CreatedBy`, `UpdatedAt` |
| `admin_users` | `Id`, `Email`, `FullName`, `PasswordHash`, `RoleIds[]`, `KioskScope (All / KioskIds[])`, `IsActive`, `MustChangePassword`, `FailedLoginCount`, `LockedUntil`, `LastLoginAt`, `TelegramChatId?` |
| `kiosk_commands` | `Id`, `KioskId`, `Type (RefreshPrinters/TestPrint/Pause/Resume/RestartApp/Reboot/UpdateNow/RollbackVersion/UploadLogs/ReapplyProvisioning/RestartAgent)`, `Payload`, `Status (Sent/Acknowledged/Succeeded/Failed/Expired)`, `Result`, `CreatedBy`, `CreatedAt` |
| `kiosk_telemetry` | Mongo time-series (`timeField: Timestamp`, `metaField: KioskId`), TTL `Kiosk.TelemetryRetentionDays`; Bölmə 7.9 sahələri |
| `printer_telemetry` | Mongo time-series (`metaField: {KioskId, PrinterName}`); Bölmə 7.10 sahələri |
| `storage_providers`, `storage_sync_jobs`, `storage_sync_failures` | Bölmə 2.10 |
| MassTransit outbox / inbox | MassTransit.MongoDb tərəfindən idarə olunan kolleksiyalar |
| `audit_logs` | `AdminUserId`, `Action`, `EntityType`, `EntityId`, `Before`, `After`, `Ip`, `CreatedAt` |
| `notifications` | `UserId`, `Type`, `Title`, `Body`, `Data`, `Read`, `CreatedAt` |
| `data_exports` | `UserId`, `Status`, `StorageKey`, `ExpiresAt` |

### PrintJob status maşını
```
Draft ─► PendingPayment ─► Paid ─► Queued ─► Printing ─► Completed
             │                        │          │          ▲
             ▼                        ▼          ▼          │
          Expired                 Cancelled   Interrupted ─► Queued (retry)
                                      │          │
                                      ▼          ▼
                                  Refunded ◄── Cancelled

                    Queued / Printing ─► Suspected ─┬─► Completed
                    (kioskdan xəbər yoxdur)         ├─► Interrupted (A ssenarisi)
                                                    └─► Cancelled + refund
```
`Suspected` — **nəticəsi naməlum** job (Bölmə 2.5, ssenari B): kiosk ilə əlaqə
itib, çapın olub-olmadığı bilinmir. Ödəniş `Succeeded` qalır və **avtomatik
refund edilmir**. Buradan çıxış ya kioskun göndərdiyi nəticə ilə (avtomatik),
ya da adminin araşdırma qərarı ilə olur.
Tam state maşınları (PrintJob, Payment, Refund, Device, Kiosk, Update, Schedule) `docs/STATE_MACHINES.md`-dədir. Keçidlər yalnız explicit domen metodları ilə (`job.Complete(...)`, `job.Interrupt(...)`), icazəsiz keçid `DomainException` atır; raw property assignment qadağandır. Hər keçid `StatusHistory`-yə yazılır və domain event yaradır (MassTransit outbox vasitəsilə yayılır).

---

## 6. Backend API (REST, prefix `/api`)

| Modul | Endpoint-lər |
|---|---|
| Auth | `POST auth/register`, `POST auth/confirm-email`, `POST auth/login`, `POST auth/refresh`, `POST auth/logout`, `POST auth/device-challenge` (deviceId → nonce), `POST auth/device-token` (deviceId, nonce, imza → Kiosk və ya Provisioning token) |
| Legal / consents | `GET legal/{docType}?lang=`, `GET consents/pending`, `POST consents` |
| Users | `GET users/me`, `PUT users/me`, `GET users/me/qr` (rotasiya olunan token), `GET users/me/qr.png`, `GET users/by-public-id/{publicId}`, `POST users/me/fcm-token`, `POST users/me/export`, `GET users/me/export/{id}`, `POST users/me/delete` (şifrə + təsdiq) |
| Features | `GET features` (mobil üçün effektiv flag-lar) |
| Wallet | `GET wallet`, `GET wallet/transactions`, `POST wallet/top-up` (kart), `POST wallet/vouchers/redeem` |
| Kiosks | `GET kiosks?search=&lat=&lng=&radiusKm=`, `GET kiosks/{idOrCode}` (qiymətlər, imkanlar, kağız kifayətliliyi daxil), `GET kiosks/{id}/queue`, `POST kiosks/heartbeat` (Kiosk rolu), `POST kiosks/me/telemetry`, `POST kiosks/me/printers/telemetry` |
| Devices (kiosk tətbiqi, aktivləşdirmədən əvvəl) | `POST devices/register` (publicKey, fingerprint, platform, appVersion, istəyə görə `enrollmentToken` — anonim, IP üzrə rate limit) → `deviceId`, `pairingCode`, `expiresAt` (etibarlı enrollment token varsa cihaz dərhal `Active` olur); `POST devices/me/pairing-code` (Provisioning token — kodu yenilə), `PUT devices/me/printers` (aktivləşdirmədən əvvəl printer siyahısı) |
| Kiosk (kiosk tətbiqi üçün) | `PUT kiosks/me/printers` (aşkarlanmış printer siyahısı), `GET kiosks/me/config` (seçilmiş printer, effektiv capability-lər, queue limiti, fayl limitləri, qiymət profili, feature flag-lar, kağız sayğacı), `GET kiosks/me/commands?status=Sent`, `POST kiosks/me/commands/{id}/result`, `POST kiosks/me/analytics` (batch) |
| Sessions | `POST sessions` (kioskId), `GET sessions/{id}`, `DELETE sessions/{id}` |
| Files | `POST files` (multipart), `GET files`, `GET files/{id}`, `GET files/{id}/content` (deşifrə olunmuş stream), `GET files/{id}/pages/{n}.png`, `POST files/{id}/edit` (PDF səhifə / şəkil / TXT əməliyyatları → yeni versiya), `PUT files/{id}/auto-delete`, `DELETE files/{id}` |
| Scan / Layout | `POST files/scan` (şəkil + preset/ölçü + həssaslıq), `PUT files/{id}/physical-size`, `POST files/layout` (`LayoutProjectJson` → backend PDF yaradır), `PUT files/{id}/layout` |
| Shares | `POST shares` (fileId, toPublicId), `GET shares/sent`, `GET shares/received`, `POST shares/{id}/save` |
| Pricing | `POST pricing/quote` (kioskId, pageCount, options, promoCode?, useBonus?) |
| PrintJobs | `POST print-jobs` (kiosk və ya mobil; limit, format, kağız yoxlaması), `GET print-jobs/my`, `GET print-jobs/{id}`, `POST print-jobs/{id}/cancel`, `POST print-jobs/{id}/status` (Kiosk rolu; `PrintedSides`, `PrintedSheets`, xəta), `GET print-jobs/pending-for-user` (Kiosk rolu, user QR ilə), `POST print-jobs/{id}/flash-upload` (mobil ödənişdən sonra) |
| Payments | `POST payments/intents` (jobId, method), `GET payments/{id}`, `POST payments/{id}/pay-with-app` (Wallet/Card/Bonus), `POST payments/callback/{provider}`, `GET refunds/{id}/public` (anonim refund statusu) |
| Receipts | `GET receipts`, `GET receipts/{id}/pdf`, `POST receipts/{id}/email` |
| Schedules | `POST schedules`, `GET schedules`, `PUT schedules/{id}/time`, `POST schedules/{id}/print-now`, `DELETE schedules/{id}` |
| Referrals | `GET referrals/me` (kod, link, QR, statistika) |
| Promos | `POST promos/validate` |
| Notifications | `GET notifications`, `POST notifications/{id}/read` |
| Incidents | `POST incidents` (Kiosk rolu) |
| Admin Auth | `POST auth/admin/login`, `POST auth/admin/refresh`, `POST auth/admin/logout`, `POST auth/admin/change-password`, `GET admin/me` (effektiv icazələr, kiosk əhatəsi) |
| Admin Dashboard | `GET admin/dashboard`, `GET admin/reports/revenue?from=&to=&kioskId=` |
| Admin Devices | `GET admin/devices?status=Pending` (kod, fingerprint, IP, platforma, printerlər), `POST admin/devices/lookup` (pairingCode və ya QR məzmunu → cihaz), `POST admin/devices/{id}/activate` (mövcud `kioskId` və ya yeni kiosk parametrləri + admin PIN + istəyə görə printer), `POST admin/devices/{id}/revoke` (səbəb), `GET admin/kiosks/{id}/devices` (cihaz tarixçəsi), `POST admin/enrollment-tokens` (kioskId və ya yeni kiosk şablonu, müddət) → token + hazır provisioning əmri, `GET admin/enrollment-tokens`, `DELETE admin/enrollment-tokens/{id}` |
| Agent (Kiosk token) | `GET kiosks/me/update` (hədəf versiya, manifest, endirmə linki), `GET releases/{id}/download` (stream), `POST kiosks/me/update-status`, `POST kiosks/me/logs` (sıxılmış log arxivi), `GET releases/latest?platform=&channel=` (anonim, provisioning üçün — yalnız imzalı paket) |
| Admin Releases | `POST admin/releases` (paket + manifest; CI release token-i ilə də), `GET admin/releases`, `POST admin/releases/{id}/withdraw`, `CRUD admin/rollouts`, `POST admin/rollouts/{id}/pause\|resume\|rollback`, `GET admin/kiosks/versions` |
| Admin Kiosks | `CRUD admin/kiosks`, `POST admin/kiosks/{id}/admin-pin`, `PUT admin/kiosks/{id}/limits`, `PUT admin/kiosks/{id}/pricing-profile`, `PUT admin/kiosks/{id}/fiscal-terminal`, `GET admin/kiosks/{id}/printers`, `POST admin/kiosks/{id}/printers/refresh`, `PUT admin/kiosks/{id}/printer`, `PUT admin/kiosks/{id}/capabilities`, `POST admin/kiosks/{id}/test-print`, `POST admin/kiosks/{id}/commands`, `GET admin/kiosks/{id}/commands`, `GET admin/kiosks/{id}/status`, `GET admin/kiosks/{id}/telemetry?from=&to=&metrics=`, `GET admin/kiosks/{id}/printers/{name}`, `GET admin/kiosks/{id}/printers/{name}/telemetry?from=&to=`, `GET admin/printers` |
| Admin Supplies / Service | `GET admin/kiosks/{id}/supplies` (sayğaclar, səviyyələr, proqnoz), `POST admin/kiosks/{id}/paper/refill` (added / full / exact), `PUT admin/kiosks/{id}/paper/capacity`, `CRUD admin/supply-rules`, `GET admin/supply-alerts`, `CRUD admin/service-tasks`, `POST admin/service-tasks/{id}/assign`, `POST admin/service-tasks/{id}/start`, `POST admin/service-tasks/{id}/complete`, `CRUD admin/inventory/items`, `POST admin/inventory/movements`, `GET admin/inventory/movements` |
| Admin Queue / Jobs | `GET admin/kiosks/{id}/queue`, `GET admin/print-jobs`, `GET admin/print-jobs/{id}`, `POST admin/print-jobs/{id}/cancel` (refund ilə) |
| Admin Payments | `GET admin/payments` (filtr: status, metod, kanal, kiosk, istifadəçi, tarix, "xəta olub", "refund gözləyir", "refund uğursuz"), `GET admin/payments/{id}` (bağlı job-lar, kiosk, istifadəçi, qəbz, fiskal çek, promokod, bonus, provider məlumatı, timeline, refund-lar), `GET admin/payments/{id}/refund-options`, `POST admin/payments/{id}/refund` (amount, destination, reason, cancelJob), `GET admin/refunds`, `POST admin/refunds/{id}/retry`, `POST admin/refunds/{id}/change-destination`, `GET admin/receipts`, `GET admin/vouchers` |
| Admin Fiscal | `GET admin/fiscal-receipts`, `POST admin/fiscal-receipts/{id}/retry`, `GET/PUT admin/fiscal/settings` |
| Admin Users | `GET admin/users`, `GET admin/users/{id}` (fayl metadata-sı, job-lar, wallet, referral — fayl məzmunu yox), `POST admin/users/{id}/block`, `POST admin/users/{id}/unblock`, `POST admin/wallets/{userId}/adjust` |
| Admin Config | `GET/PUT admin/settings`, `CRUD admin/pricing-profiles`, `POST admin/pricing-profiles/{id}/assign` (kioskIds), `CRUD admin/promos`, `GET admin/referrals`, `GET/PUT admin/files/formats`, `CRUD admin/feature-flags`, `CRUD admin/legal-documents` (yeni versiya dərc et) |
| Admin Analytics | `GET admin/analytics/pilot-report?from=&to=&kioskId=`, `GET admin/analytics/funnel`, `GET admin/analytics/export.csv` |
| Admin Incidents | `GET admin/incidents`, `POST admin/incidents/{id}/acknowledge`, `POST admin/incidents/{id}/resolve` |
| Admin System | `CRUD admin/admin-users`, `GET admin/permissions`, `CRUD admin/roles`, `POST admin/roles/{id}/clone`, `GET admin/audit-logs` |
| Admin Storage | `GET/POST/PUT/DELETE admin/storage/providers`, `POST admin/storage/providers/{id}/test`, `POST admin/storage/providers/{id}/set-primary`, `POST admin/storage/providers/{id}/set-secondary`, `POST admin/storage/sync-jobs`, `GET admin/storage/sync-jobs`, `GET admin/storage/sync-jobs/{id}`, `POST admin/storage/sync-jobs/{id}/pause\|resume\|cancel\|retry-failed`, `GET admin/storage/sync-jobs/{id}/failures`, `POST admin/storage/sync-jobs/{id}/purge-source` |
| Dev (yalnız Development) | `POST dev/payments/{id}/simulate-success`, `POST dev/payments/{id}/simulate-fail`, `POST dev/refunds/{id}/simulate-fail`, `POST dev/fiscal/{id}/simulate-fail` |

Ümumi qaydalar:
- Xəta formatı: RFC 7807 `ProblemDetails`.
- Pul hərəkəti olan bütün POST-lar `Idempotency-Key` header tələb edir (Redis-də 24 saat).
- Pagination: `?page=1&pageSize=20`.
- Rate limit (daxili ASP.NET Core): auth endpoint-ləri 10/dəq IP üzrə, digərləri 120/dəq istifadəçi üzrə.
- Admin endpoint-ləri icazə (`[HasPermission]`) və kiosk əhatəsi ilə filtrlənir.
- Hangfire dashboard `/api/admin/hangfire` — yalnız `SuperAdmin` rolu, admin auth ilə.

### SignalR
| Hub | Qrup | Server → Client hadisələri |
|---|---|---|
| `/hubs/provisioning` | `device:{deviceId}` (Provisioning token) | `PairingCodeRenewed`, `DeviceActivated` (→ kiosk token almağa keçir), `DeviceRevoked` |
| `/hubs/kiosk` | `kiosk:{kioskId}` | `QueueUpdated`, `JobAdded`, `JobCancelled`, `PaymentConfirmed`, `SessionOpened`, `SessionClosed`, `KioskStatusChanged`, `PrinterConfigChanged`, `KioskConfigChanged` (limit, qiymət, flag, kağız), `FeatureFlagsChanged`, `KioskCommand` |
| `/hubs/user` | `user:{userId}` | `JobStatusChanged`, `PaymentStatusChanged`, `RefundOffered`, `RefundStatusChanged`, `ScheduleDue`, `FileShared`, `ExportReady` |
| `/hubs/admin` | `admins`, `admin-kiosk:{kioskId}`, `operator:{adminUserId}` | `KioskStatusChanged`, `KioskPrintersUpdated`, `KioskTelemetryUpdated`, `PrinterTelemetryUpdated`, `SupplyAlertRaised`, `SupplyAlertCleared`, `ServiceTaskAssigned`, `ServiceTaskUpdated`, `IncidentRaised`, `IncidentResolved`, `QueueUpdated`, `PaymentSucceeded`, `RefundStatusChanged`, `FiscalStatusChanged`, `CommandResult`, `StorageSyncProgress`, `StorageSyncCompleted`, `StorageHealthChanged`, `PermissionsChanged`, `DeviceRegistered`, `DeviceActivated`, `DeviceRevoked` |

### Event-lər (MassTransit + RabbitMQ, transactional outbox)
`PaymentSucceeded`, `PaymentExpired`, `PrintJobQueued`, `PrintJobStatusChanged`, `PrintJobCompleted`, `PrintJobInterrupted`, `KioskIncidentRaised`, `KioskWentOffline`, `RefundRequested`, `RefundCompleted`, `RefundFailed`, `FiscalizationRequested`, `FiscalReceiptIssued`, `ScheduledPrintDue`, `ReceiptRequested`, `ReferralQualified`, `FileShared`, `FileUploaded`, `FileConversionRequested`, `FileConverted`, `FileDeleted`, `ObjectStored`, `ObjectDeleted`, `StorageConfigChanged`, `StorageSyncProgressed`, `KioskPrintersReported`, `KioskPrinterChanged`, `KioskCommandIssued`, `KioskConfigChanged`, `SupplyLevelChanged`, `SupplyAlertRaised`, `ServiceTaskCreated`, `AccountDeletionRequested`, `AdminActionPerformed`, `AdminPermissionsChanged`, `FeatureFlagsChanged`, `DeviceRegistered`, `DeviceActivated`, `DeviceRevoked`.

- Consumer-lər idempotentdir (MassTransit inbox), uğursuz mesajlar retry → `_error` queue (dead-letter); sayı admin dashboard-da.

### Workers (Hangfire recurring job-ları + MassTransit consumer-ləri)
| Job | Tezlik | İş |
|---|---|---|
| `PaymentExpirationJob` | 1 dəq | Vaxtı keçmiş intent → `Expired`, job → `Expired` |
| `KioskHeartbeatMonitor` | 15 san | `OfflineAfterSeconds` keçibsə → `Offline`, incident (Critical) |
| `ScheduledPrintDueJob` | 1 dəq | Vaxtı çatan → `AwaitingUser`, push + SignalR |
| `ScheduledPrintAutoCancelJob` | 10 dəq | `AutoCancelHours` keçib → ləğv + balansa refund |
| `FileCleanupJob` | 1 saat | Vaxtı keçmiş temp və preview PDF-ləri, artıq versiyaları silir |
| `FileAutoDeleteJob` | 5 dəq + `PrintJobCompleted` | Faylların avtomatik silinmə qaydaları (Bölmə 2.13) |
| `AccountDeletionJob` | 10 dəq | Hesab silmə — fayllar bütün provayderlərdən, anonimləşdirmə |
| `DataExportJob` | event | "Məlumatlarımı yüklə" arxivi |
| `KeyRewrapJob` | admin başladır | KEK rotasiyasında DEK-lərin yenidən wrap-ı |
| `DocumentConversionConsumer` | event | Yüklənən / redaktə olunan faylı PDF-ə çevirir, səhifə sayı, preview |
| `ReceiptConsumer` | event | QuestPDF ilə qəbz (fiskal məlumatla), storage, email |
| `FiscalizationConsumer` | event | Satış / qaytarma / top-up fiskal çeki, retry, incident |
| `RefundConsumer` | event | Refund-u istiqamətinə görə icra edir: provider refund (retry ilə), wallet, bonus, voucher fallback; qaytarma fiskal çekini tələb edir |
| `AutoRefundOnKioskErrorConsumer` | `PrintJobInterrupted` / `KioskIncidentRaised` | Kioskda bank ilə ödənilmiş anonim job-lar üçün dərhal karta refund. **Yalnız A ssenarisi** (Bölmə 2.5): `Suspected` job-a toxunmur |
| `StuckJobWatchdogJob` | 1 dəq | `Job.SuspectedAfterMinutes` müddətində kioskdan status/heartbeat gəlməyən `Printing` / `Queued` job → **`Suspected`** + incident (High) + "Baxış gözləyən job-lar" siyahısı. **Avtomatik ləğv və refund etmir** (Bölmə 2.5, ssenari B). `Job.SuspectedAutoResolveHours` keçəndə yalnız xatırlatma bildirişi göndərir |
| `SuspectedJobResolutionConsumer` | kioskun bərpadan sonra göndərdiyi jurnal | Kiosk nəticəni bildirəndə `Suspected` job-u avtomatik həll edir: çap tamamlanıbsa `Completed`, olmayıbsa `Interrupted` → A ssenarisi (avtomatik refund) |
| `RefundStatusPollingJob` | 5 dəq | `Processing` provider refund-larının statusu |
| `PaymentReconciliationJob` | 5 dəq | `Expired` / naməlum ödənişlərin provider statusu ilə uzlaşdırılması (gec gələn callback-lər üçün) |
| `WalletLedgerReconciliationJob` | gündəlik | Ledger cəmi ilə `wallets.Balance` fərqi → incident |
| `IdempotencyCleanupJob` | 1 saat | Vaxtı keçmiş idempotency açarları |
| `IncidentNotificationConsumer` | event | Admin/Operator paneli + Email + Telegram |
| `AffectedUsersNotificationConsumer` | event | Kiosk dayananda qeydiyyatlı job sahiblərinə refund təklifi |
| `ReferralRewardConsumer` | `PrintJobCompleted` | İlk ödənişli çapda bonus |
| `SupplyEvaluator` | `SupplyLevelChanged` + 5 dəq | Kağız / toner qaydalarını qiymətləndirir, alert və servis tapşırığı yaradır |
| `SupplyForecastJob` | 1 saat | Sərf proqnozu, `ForecastLeadHours` tapşırıqları |
| `ServiceTaskEscalationJob` | 5 dəq | Gecikən Critical tapşırıqlar üçün eskalasiya |
| `InventoryLowStockJob` | 1 saat | `Stock < MinStock` xəbərdarlığı |
| `TelemetryAlertEvaluator` | telemetriya event | Disk, RAM, bağlantı həddləri |
| `StorageMigrationWorker` | sync job | Checkpoint-li paralel kopyalama, yoxlama, Primary keçidi |
| `StorageMirrorConsumer` | `ObjectStored`/`ObjectDeleted` | Secondary-yə təkrarlama |
| `StorageReconciliationJob` | `Storage.ReconciliationCron` | Mirror uyğunlaşdırması |
| `StorageHealthCheckJob` | 1 dəq | Provayder sağlamlığı, fallback, incident |
| `AnalyticsRollupJob` | 1 saat | Pilot hesabatı üçün gündəlik aqreqatlar |
| `PendingDeviceCleanupJob` | 1 saat | 24 saatdan çox aktivləşdirilməyən `Pending` cihazları `Expired` edir |

---

## 7. Kiosk tətbiqi (Avalonia)

### 7.1 Ümumi
- Başlanğıcda: `WindowState = FullScreen`, `Topmost = true`, `SystemDecorations = None`, bağlanma bloklanır (`Closing` cancel). Çıxış yalnız gizli admin jesti ilə (sağ yuxarı küncə 5 dəfə toxunma + admin PIN; PIN admin paneldə kiosk üçün təyin edilir, kioska yalnız PBKDF2 hash-i gəlir və offline keşlənir).
- Lockdown iki səviyyədədir: **OS səviyyəsi** — provisioning skripti (Bölmə 7.12: Shell Launcher, Keyboard Filter, bildiriş siyasətləri / cage, logind); **tətbiq səviyyəsi** — `IKioskLockdown` (Windows-da əlavə low-level keyboard hook, Topmost). Ekranda NeoPrint-dən başqa heç nə görünmür: OS bildirişləri, yeniləmə pəncərələri, xəta dialoqları, toxunma jestləri, USB autoplay söndürülüb.
- `docs/KIOSK_SETUP.md` (OS quraşdırılması, BIOS: elektrik gələndə avtomatik açılma, provisioning skriptinin işə salınması, golden image) və `docs/KIOSK_PROVISIONING.md` (skriptlərin etdiyi hər dəyişikliyin tam siyahısı).
- Qlobal crash handler: log + incident + tətbiqi yenidən başlat.
- Konfiqurasiya: `appsettings.json` yalnız qeyri-gizli dəyərlər (`Api:BaseUrl`, `Kiosk:UseFakePrinter` — yalnız Development build-də `true`, Production build-də həmişə `false`, `LibreOffice:Path` — boşdursa Windows `C:\Program Files\LibreOffice\program\soffice.exe`, Linux `/usr/bin/soffice`). Bu fayl **bütün kiosklar üçün eynidir** (build-in bir hissəsi). Kimlik, PIN və kiosk parametrləri yoxdur — hamısı aktivləşdirmədən sonra backend-dən gəlir (Bölmə 7.11).
- Bütün mətn daxiletmələri (email, promokod, job kodu, TXT redaktəsi) üçün öz **ekran klaviaturası** (az/en/ru layout, rəqəm rejimi).
- Analitika hadisələri (Bölmə 2.17) lokal buferdə toplanıb hər 30 saniyədə `POST kiosks/me/analytics` ilə göndərilir.

### 7.2 Layout (bütün ekranlarda)
```
┌──────────────────────────────────────────────┬───────────────┐
│ HEADER: Logo | Kiosk adı | Dil seçimi | Saat │               │
├──────────────────────────────────────────────┤  QUEUE PANELİ │
│                                              │  (sağ, 280px) │
│              DƏYİŞƏN MƏZMUN                  │  #1 JobCode…  │
│                                              │  #2 …         │
│                                              │  max 3/5      │
├──────────────────────────────────────────────┴───────────────┤
│ FOOTER: [Kiosk QR]  KSK-0001  |  Qaynar xətt / kömək         │
└──────────────────────────────────────────────────────────────┘
```
- Header, footer və queue paneli `MainWindow` səviyyəsindədir; ekranlar yalnız mərkəzi `ContentControl`-da dəyişir. Dialoglar mərkəzi sahədə açılır, footer-i örtmür.
- Kiosk QR yalnız footer-də (Bölmə 2.9).
- Queue elementində: `JobCode`, istifadəçi adının ilk hərfi + `***` (anonim: "Qonaq"), status (Növbədə / Çap olunur), səhifə sayı.

### 7.3 Ekranlar
| # | Ekran | Məzmun |
|---|---|---|
| 0 | Activation | Aktivləşdirilməmiş cihazda: "Bu kiosk aktivləşdirilməyib", böyük pairing kodu (`K7QM-4XPT`), aktivləşdirmə QR-ı, kodun bitmə geri sayımı (avtomatik yenilənir), cihaz məlumatı (hostname, platforma, versiya, aşkarlanan printerlər), backend bağlantı statusu. Footer-də kiosk QR əvəzinə bu ekran göstərilir |
| 1 | Home | "Flash kartdan çap et", "QR kodumu oxut", "Job kodu ilə ləğv et", təlimat |
| 2 | FlashBrowser | Flash taxılan kimi avtomatik açılır (Home-dadırsa). Qovluq naviqasiyası, yalnız icazəli formatlar, fayl ölçüsü/tipi, limitdən böyük fayllar boz + səbəb, "USB" badge. Çıxarılarsa Home-a qayıdış |
| 3 | FilePreview | Səhifə thumbnail-ləri, böyük baxış, "USB" badge, "Redaktə et", "Səhifədə yerləşdir" (şəkillər), "Davam" |
| 4 | FileEditor | PDF səhifə əməliyyatları, şəkil redaktəsi, TXT redaktəsi (Bölmə 2.2) |
| 4a | LayoutEditor | Bölmə 2.11 səhifə tərtibatı (toxunma jestləri ilə) |
| 5 | PrintOptions | Parametrlər (effektiv capability-lərə görə söndürülür), promokod sahəsi, canlı qiymət (kioskun qiymət profili + həcm endirimi), kağız kifayətliliyi yoxlaması |
| 6 | PaymentMethod | "Bank tətbiqi ilə ödə", "NeoPrint mobil tətbiqi ilə ödə" (`mobile_payment_at_kiosk` flag), məxfilik siyasətinə QR link |
| 7 | BankQrPayment | Bank QR, geri sayım (`Payment.TimeoutMinutes`), SignalR `PaymentConfirmed` gözlənilir (+ 3 san polling fallback) |
| 8 | MobileQrPayment | `neoprint://pay/{id}` QR, geri sayım, eyni təsdiq mexanizmi |
| 9 | Receipt | "Ödəniş uğurlu", JobCode, queue mövqeyi, fiskal çek QR-ı (hazırdırsa), email daxil et → qəbz göndər, "Bitir" |
| 10 | UserQrFiles | İstifadəçi QR oxunanda: istifadəçinin kiosk üçün hazırladığı (ödənilmiş və ya ödənilməmiş) job/faylları. Seç → ödənilməyibsə PrintOptions/Payment, ödənilibsə birbaşa queue. 60 san fəaliyyətsizlikdə bağlanır |
| 11 | CancelJob | JobCode daxil et → job məlumatı → ləğv təsdiqi → refund məlumatı (karta qaytarılır; fallback halında voucher) |
| 12 | RefundInfo | Xəta ilə dayanmış bank ödənişli job üçün: qaytarılan məbləğ, "1–3 iş günü" qeydi, refund status QR-ı, email daxil etmə |
| 13 | OutOfService | "Kiosk müvəqqəti dayanmışdır", səbəb (ümumi mətn), refund məlumatı |
| 14 | NotConfigured | "Kiosk quraşdırılır" (printer seçilməyib) |
| 15 | Degraded | Backend əlçatmazdır, lakin lokal konfiqurasiya keşi hələ etibarlıdır (`Kiosk.ConfigCacheTtlHours`): ekran açıqdır, ödənilmiş queue işlənir, **yeni ödəniş qəbul edilmir** ("Onlayn ödəniş müvəqqəti əlçatmazdır") |
| 16 | Offline | İnternet yoxdur və ya keş köhnəlib — yeni sifariş qəbul olunmur |

### 7.4 Çap icrası
- Kiosk `QueueUpdated` alır, növbəti `Queued` job-u özü götürür (lokal ardıcıl çap, eyni vaxtda 1 job).
- Flash job: lokal fayl (şifrəli spool) → (lazımdırsa `IDocumentConverter` → PDF) → redaktə → printerə native format və ya PDF (Bölmə 2.2 qaydası).
- Mobil job: backend `GET files/{id}/content` (deşifrə olunmuş stream) → şifrəli spool → printer.
- Hər mərhələdə `print-jobs/{id}/status` göndərilir (`Printing`, `Completed`, `Interrupted` + `PrintedSides`, `PrintedSheets`, xəta səbəbi).
- Printer statusu `IPrinterService.GetStatusAsync()` ilə 5 saniyədən bir oxunur və vahid `PrinterStatus` modelinə çevrilir (`Online`, `Offline`, `OutOfPaper`, `PaperJam`, `TonerLow`, `Error`):
  - Windows: `System.Printing.PrintQueue` (`IsOffline`, `IsOutOfPaper`, `IsPaperJammed`, `IsTonerLow`, `IsInError`)
  - Linux: `lpstat -p {printer}` + `ipptool get-printer-attributes` (`printer-state`, `printer-state-reasons`: `media-empty`, `media-jam`, `toner-low`, `offline-report`)
- Çap göndərmə: Windows — PDF səhifələri PDFium ilə render edilib `PrintDocument` ilə; Linux — `lp -d {printer} -n {copies} -o sides=... -o media=A4|A3 -o ColorModel=Gray|RGB file.pdf`, job id ilə `lpstat -W not-completed` izlənir. Duplex, səhifədə səhifə və aralıq **əvvəlcədən PDF-ə tətbiq olunur**; printerə yalnız hazır PDF, nüsxə sayı, duplex və rəng rejimi ötürülür.
- Xəta → cari job `Interrupted`, kiosk `Paused`, incident, OutOfService ekranı. Bank ilə ödənilmiş anonim job-lar avtomatik ləğv + karta refund (Bölmə 2.5), ekranda RefundInfo. Problem aradan qalxdıqda kiosk `Online`; qeydiyyatlı istifadəçilərin ləğv olunmamış `Interrupted` job-ları yenidən `Queued`.

### 7.5 Flash və anonim job-un tamamlanması
- Ödənilməmiş anonim job `Payment.TimeoutMinutes` (default 5 dəq, admin paneldən dəyişdirilir) sonra `Expired` olur. Kioskda geri sayım bu dəyərdən alınır.
- Bank QR / kartla ödənilmiş anonim job xəta ilə dayandıqda (printer xətası, kiosk nasazlığı, çap yarımçıq qalması) job `Cancelled` edilir və məbləğ **avtomatik karta qaytarılır** (Bölmə 2.5); kiosk ekranında refund məlumatı göstərilir.
- Mobil tətbiqlə ödənilən flash job: ödəniş təsdiqi gələn kimi `POST print-jobs/{id}/flash-upload` (orijinal + çap PDF-i), uğursuz olarsa şifrəli spool-da saxlanılıb təkrarlanır (Bölmə 2.2).
- Flash çıxarılıbsa və job hələ `Queued`-dirsə: fayl job yaradılan anda kioskun şifrəli spool-una (`IAppPaths.SpoolDir`: Windows `%ProgramData%\NeoPrint\spool`, Linux `/var/lib/neoprint/spool`) kopyalanır, ona görə çap davam edir. Çapdan sonra silinir.

### 7.6 Platforma abstraksiyaları (Windows + Linux)
| Interface | Windows | Linux |
|---|---|---|
| `IPrinterService` (discover, print, status, cancel) | Windows spooler / `System.Printing` | CUPS (`lp`, `lpstat`, `cancel`, `ipptool`) |
| `IRemovableDriveWatcher` | `DriveInfo` polling | `/proc/mounts` polling |
| `IKioskLockdown` | Keyboard hook + Topmost | Topmost (əsas lockdown `cage` səviyyəsində) |
| `IAppPaths` (spool, logs, config) | `%ProgramData%\NeoPrint\*` | `/var/lib/neoprint`, `/var/log/neoprint`, `/etc/neoprint` |
| `ISystemControl` (app restart, reboot) | `Process.Start` + `shutdown /r /t 0` | `systemctl restart neoprint-kiosk`, `systemctl reboot` (sudoers ilə icazə) |
| `IDocumentConverter` (Office / şəkil / PS / PCL / XPS → PDF) | `soffice.exe --headless`, SkiaSharp, `gswin64c`, `gpcl6win64`, `gxpswin64` | `soffice --headless`, SkiaSharp, `gs`, `gpcl6`, `gxps` |
| `IPrinterFormatSupport` (native formatlar) | Driver capability-ləri | IPP `document-format-supported` |
| `ISystemMetrics` (CPU, RAM, disk, şəbəkə) | WMI / Performance counters | `/proc`, `/sys` |

Qaydalar:
- `NeoPrint.Kiosk`, `NeoPrint.Kiosk.Core`, `NeoPrint.Kiosk.Printing` — `net8.0`, platformaya aid API istifadə etmir.
- `Platform.Windows` — `net8.0-windows`; `Platform.Linux` — `net8.0`. `win-x64` publish-ə Windows, `linux-x64` publish-ə Linux implementasiyası daxil edilir (MSBuild şərti `RuntimeIdentifier` üzrə); DI runtime-da OS-ə görə qeydiyyat edir.
- Hər interface üçün `Fake*` implementasiya var (`Kiosk:UseFakePrinter=true` → çap faylı `spool/printed/` qovluğuna PDF kimi yazılır, status həmişə `Online`, status faylı ilə xəta simulyasiya olunur). Printeri olmayan maşında və CI-də tam axın yoxlanılır.
- Yollar həmişə `Path.Combine`; fayl adları case-sensitive qəbul olunur.
- Linux-da tətbiq `neoprint` sistem istifadəçisi ilə işləyir, `lpadmin` qrupuna daxildir.

### 7.7 Printer aşkarlanması və admin tərəfindən seçim
**Aşkarlama (kiosk)**
- `IPrinterService.DiscoverAsync()`:
  - Windows: `LocalPrintServer.GetPrintQueues()` → ad, driver, online status; capability-lər `PrintQueue.GetPrintCapabilities()` (`OutputColorCapability`, `DuplexingCapability`, `PageMediaSizeCapability` A3).
  - Linux: `lpstat -e` (adlar), `lpstat -p` (status), `lpoptions -p {name} -l` (`ColorModel`, `Duplex`/`sides`, `PageSize` A3).
- İşə düşəndə, hər 60 saniyədə (dəyişiklik olarsa) və admin `RefreshPrinters` əmri gələndə `PUT kiosks/me/printers` göndərilir.
- `Kiosk:UseFakePrinter=true` olduqda siyahıya `NeoPrint Fake Printer` əlavə olunur.

**Seçim (admin)**
- Admin web → Kiosk detalı → "Printerlər" tab: aşkarlanmış printerlər (ad, driver, online, rəng/duplex/A3), "Yenilə", "Default et", "Test səhifəsi çap et".
- "Default et" → `PUT admin/kiosks/{id}/printer` → `kiosks.SelectedPrinter` yenilənir → SignalR `PrinterConfigChanged` → kiosk `GET kiosks/me/config`.
- Admin capability-ləri yalnız **söndürə** bilər (`CapabilityOverrides`), printerin dəstəkləmədiyini yandıra bilməz. `EffectiveCapabilities = Detected AND Overrides`.

**Kioskda tətbiq olunması**
- Printer dəyişikliyi cari `Printing` job bitdikdən sonra tətbiq olunur.
- Son konfiqurasiya lokal keşdə (`IAppPaths.ConfigDir/config.json`), backend əlçatmaz olsa da kiosk seçilmiş printerlə açılır.

**Xüsusi hallar**
| Hal | Davranış |
|---|---|
| Yeni kiosk, printer seçilməyib | Status `NotConfigured`, NotConfigured ekranı, sifariş qəbul olunmur, mobil axtarışda görünmür. Admin dashboard-da "Konfiqurasiya tələb olunur" siyahısında |
| Aşkarlanan printer yoxdur | Incident `High` (`NoPrinterDetected`) |
| Seçilmiş printer siyahıdan itib | Incident `Critical` (`SelectedPrinterMissing`), kiosk `Paused`, Bölmə 9.6 axını |
| Test çap | `POST admin/kiosks/{id}/test-print` → `KioskCommand(TestPrint)` → kiosk daxili test PDF-i (kiosk adı, kod, printer, tarix, rəng/duplex yoxlama səhifəsi) çap edir, nəticə `CommandResult` ilə panelə qayıdır. Ödənişsiz, queue-ya düşmür, queue boş olanda icra olunur, kağız sayğacından çıxılır. `NotConfigured` kiosk uğurlu test çapından sonra `Online` olur |

### 7.8 Uzaqdan əmrlər
- Admin / Operator paneldən: `RefreshPrinters`, `TestPrint`, `Pause`, `Resume`, `RestartApp`, `Reboot`, `UpdateNow`, `RollbackVersion`, `UploadLogs`, `ReapplyProvisioning`, `RestartAgent`. UI-ya aid əmrləri UI, sistem əmrlərini (reboot, yeniləmə, log, provisioning) Agent icra edir.
- Əmr `kiosk_commands`-a yazılır, SignalR `KioskCommand` ilə göndərilir; kiosk offline-dırsa qoşulanda `GET kiosks/me/commands?status=Sent` ilə alır (24 saatdan köhnə əmrlər `Expired`).
- `Pause`: yeni sifariş qəbul edilmir, cari queue tamamlanır, ekranda "Texniki xidmət". `RestartApp`/`Reboot` yalnız cari çap bitdikdən sonra.
- Nəticə `POST kiosks/me/commands/{id}/result` → `/hubs/admin` `CommandResult`.

### 7.9 Kiosk telemetriyası (cari vəziyyət)
Kiosk hər `Kiosk.TelemetrySeconds` (default 30 san) `POST kiosks/me/telemetry` göndərir; son snapshot Redis-də (`kiosk:status:{id}`), tarixçə `kiosk_telemetry`-də saxlanılır.
| Qrup | Sahələr |
|---|---|
| Tətbiq | App versiyası, işə düşmə vaxtı / uptime, cari ekran, aktiv sessiya (mobil / istifadəçi QR / anonim), son crash vaxtı |
| Sistem | OS və versiya, hostname, CPU %, RAM (istifadə / cəmi), disk boş yer (sistem və spool), sistem uptime, saat sinxronu (NTP fərqi) |
| Şəbəkə | İnternet var/yox, bağlantı tipi (Ethernet / Wi-Fi + siqnal), lokal IP, backend-ə gecikmə (ms), SignalR bağlantı statusu və son reconnect |
| Periferiya | Flash taxılıb / yox, QR skaner qoşulub / son oxuma vaxtı, ekran ölçüsü |
| İş | Queue uzunluğu, cari çap olunan job, bugünkü job sayı / vərəq, spool-da gözləyən fayllar, uğursuz flash yükləmələri, gözləyən əmrlər |
| Sərf | Kağız sayğacları (A4/A3), printer lotok səviyyələri (bilinirsə) |
| Xidmətlər | LibreOffice / Ghostscript mövcudluğu, lokal konfiqurasiya versiyası, feature flag versiyası |
| Status | Kiosk statusu, açıq incident-lər, son heartbeat |

Hədd aşımları (`TelemetryAlertEvaluator`): disk < 2 GB → Medium, RAM > 90% 5 dəq → Medium, backend gecikməsi > 2000 ms 5 dəq → Medium, SignalR 2 dəq qopuq → High.

### 7.10 Printer detalları və telemetriyası
Kiosk hər 60 saniyədə aktiv printer üçün (digər aşkarlanan printerlər üçün hər 10 dəq) `POST kiosks/me/printers/telemetry` göndərir.
| Qrup | Sahələr |
|---|---|
| Identifikasiya | Ad, istehsalçı, model, seriya nömrəsi, firmware, driver adı və versiyası, qoşulma tipi (USB / Şəbəkə + IP / MAC), URI |
| Status | Vəziyyət (idle / printing / stopped), `state-reasons` tam siyahısı, online/offline, son xəta mətni və vaxtı |
| Sərf materialları | Hər toner / mürəkkəb / drum / fuser üçün ad, rəng, səviyyə %; kağız lotokları: ad, ölçü, doluluq (bilinirsə) |
| Sayğaclar | Ümumi çap olunmuş səhifə (lifetime), rəngli / ağ-qara sayğac (dəstəklənirsə), NeoPrint vasitəsilə çap olunan səhifələr |
| İmkanlar | Rəng, duplex, dəstəklənən kağız ölçüləri, lotoklar, rezolyusiya (DPI), native formatlar, çap kənarları |
| Spooler | Sistem spooler-dəki job-lar (ID, ölçü, status), ilişib qalmış job-lar |
| Tarixçə | Status dəyişiklikləri, xətalar, test çaplar, printer seçim dəyişiklikləri, toner dəyişmələri |

Mənbələr:
- Windows: `System.Printing` (`PrintQueue`, `PrintCapabilities`), WMI `Win32_Printer` / `Win32_PrinterDriver` (`System.Management`); şəbəkə printerlərində SNMP (Printer MIB, RFC 3805: `prtMarkerSuppliesLevel`, `prtMarkerLifeCount`, `prtInputCurrentLevel`) — `Lextm.SharpSnmpLib`.
- Linux: IPP `get-printer-attributes` (`printer-make-and-model`, `printer-firmware-string-version`, `printer-device-id`, `marker-names`, `marker-levels`, `marker-colors`, `printer-supply`, `printer-state-reasons`), `lpstat -o` (spooler), şəbəkə printerlərində eyni SNMP.
- Məlumat əldə edilə bilməyən sahələr `null` göndərilir, paneldə "Printer bu məlumatı vermir" kimi göstərilir.
- Toner səviyyələri xəbərdarlıq qaydaları ilə qiymətləndirilir (Bölmə 2.15); ilişib qalmış spooler job-u (> 5 dəq) → High incident.

### 7.11 Universal build və cihaz aktivləşdirmə
**Prinsip:** kiosk tətbiqinin **bir build-i** (hər OS üçün bir paket: `win-x64`, `linux-x64`) bütün kiosklara quraşdırılır. Kiosk üçün ayrıca build, konfiqurasiya faylı, env dəyişəni və ya əl ilə daxil edilən açar yoxdur. Kioskun "kim olduğu" və bütün parametrləri admin paneldən təyin edilir.

**Build-də olanlar (bütün kiosklar üçün eyni):** tətbiq, `appsettings.json` (`Api:BaseUrl` — mühitin, yəni Production / Staging backend-inin ünvanı; kiosk-a aid heç nə yoxdur), install skripti.
- `Api:BaseUrl` mühitə görə fərqlənə bilər (Production / Staging paketi), kiosk-a görə yox. Quraşdırma zamanı `install --api-url` parametri ilə (məs. test stendi üçün) override edilə bilər — bu secret deyil.

**İlk işə düşmə (aktivləşdirilməmiş cihaz)**
1. Tətbiq lokal cihaz kimliyi tapmır → cihaz açar cütü yaradır (ECDSA P-256, BCL):
   - Windows: CNG açarı — TPM varsa `Microsoft Platform Crypto Provider` (açar TPM-də, ixrac olunmur), yoxdursa `Microsoft Software Key Storage Provider` maşın səviyyəsində, ixrac olunmayan.
   - Linux: `/var/lib/neoprint/device/key.pem` (`chmod 600`, sahibi `neoprint`).
2. Cihaz barmaq izini toplayır (hostname, OS, versiya, machine-id, MAC-lar, CPU, RAM, ekran) və `POST devices/register` göndərir → `deviceId` + **pairing kodu** (8 simvol, qarışdırıla bilən simvollar yoxdur, `XXXX-XXXX`, 30 dəq keçərli, gözləmədə avtomatik yenilənir).
3. Cihaz challenge imzalayıb `Provisioning` token alır, `/hubs/provisioning`-ə qoşulur, aşkarladığı printerləri göndərir (`PUT devices/me/printers`), **Activation** ekranını göstərir (kod + QR).
4. Bu mərhələdə kiosk heç bir sifariş qəbul etmir, heç bir konfiqurasiyaya çıxışı yoxdur.

**Aktivləşdirmə (admin / operator)**
- Admin paneldə **"Cihazlar → Aktivləşdirmə gözləyənlər"**: kod, barmaq izi, IP, platforma, printerlər, qeydiyyat vaxtı.
- Admin kodu daxil edir (və ya Operator paneli ilə ekrandakı QR-ı telefonla scan edir) → cihaz məlumatları göstərilir → iki seçim:
  - **Yeni kiosk kimi aktivləşdir:** ad, kod (`KSK-XXXX`), ünvan + xəritədə nöqtə, qiymət profili, fayl limitləri, queue limiti, kağız kapasitetləri, fiskal terminal ID, default dil, **admin PIN**, istəyə görə aktiv printer (cihazın bildirdiyi siyahıdan).
  - **Mövcud kioska bağla:** `NotActivated` kiosk və ya **cihaz dəyişikliyi** (köhnə cihaz sıradan çıxıb) — mövcud kioskun bütün parametrləri, tarixçəsi, sayğacları saxlanılır; kioskun əvvəlki cihazı varsa təsdiqdən sonra avtomatik `Revoked` olur.
- "Aktivləşdir" → cihaz `Active`, `kiosks.ActiveDeviceId` təyin olunur → SignalR `DeviceActivated` → kiosk yeni challenge ilə **Kiosk token** alır → `GET kiosks/me/config` → printer seçilibsə və test çap uğurludursa `Online`, deyilsə `NotConfigured` (Bölmə 7.7).
- Bütün addımlar audit log-a yazılır. İcazələr: `kiosks.activate`, `devices.revoke`.

**Cihazın ayrılması və dəyişdirilməsi**
- **"Cihazı ayır"** (admin): cihaz `Revoked`, kiosk `NotActivated`. Cihaz növbəti müraciətdə 401 alır (və ya SignalR `DeviceRevoked`) → lokal konfiqurasiya keşini, spool-u, açarı silir → yeni açar yaradır → yenidən Activation ekranına qayıdır. Eyni cihaz başqa kioska bağlana bilər.
- **Cihaz dəyişikliyi** (məs. kompüter xarab oldu): yeni cihaz eyni paketlə quraşdırılır → "Mövcud kioska bağla" → köhnə cihaz avtomatik ləğv olunur. Kioskun QR kodu, kodu, tarixçəsi dəyişmir.
- Ləğv olunmuş cihazın açarı ilə token alınmır (backend `kiosk_devices.Status` yoxlayır; kiosk JWT-si 1 saatlıqdır, ləğv anında həmin cihazın SignalR bağlantısı kəsilir və token-i Redis-də qara siyahıya düşür).

**Təhlükəsizlik**
- Pairing kodu birdəfəlikdir, hash-lənmiş saxlanılır, 30 dəqiqə keçərlidir; admin tərəfində kod axtarışına rate limit (dəqiqədə 20) və uğursuz cəhdlərin audit-i.
- `devices/register` anonimdir, amma IP üzrə rate limit (saatda 10) və `Pending` cihaz sayına ümumi limit (1000) var; 24 saat aktivləşdirilməyən cihazlar `Expired` olur.
- Aktivləşdirmə yalnız `kiosks.activate` icazəsi olan admin/operator tərəfindən və onun kiosk əhatəsi daxilində (mövcud kioska bağlamada).
- Barmaq izində əsaslı dəyişiklik (məs. machine-id dəyişdi) → incident (Medium) və admin panelində xəbərdarlıq.

**Enrollment token ilə "sıfır toxunuş" aktivləşdirmə (tövsiyə olunan)**
- Admin paneldə "Cihazlar → Enrollment token yarat": mövcud (`NotActivated`) kiosk qeydini seç və ya yeni kiosk parametrlərini əvvəlcədən doldur → birdəfəlik token (`Enrollment.TokenTtlHours`) + hazır əmr: `provision.ps1 -ApiUrl ... -EnrollmentToken ...` / `sudo ./provision.sh --api-url ... --enrollment-token ...`.
- Provisioning skripti token-i `ProgramData` / `/var/lib/neoprint` altında qorunan fayla yazır → tətbiq ilk qeydiyyatda göndərir → cihaz dərhal həmin kioska `Active` olur, heç kimin ekrandakı kodu oxumasına ehtiyac yoxdur. Token istifadə olunan kimi silinir.
- Token yoxdursa və ya vaxtı keçibsə cihaz adi pairing kodu axınına düşür.

**Kiosk UI vəziyyətləri:** `Starting`, `Online`, `Degraded` (backend yoxdur, keş etibarlıdır), `Offline`, `PaymentUnavailable`, `PrinterUnavailable`, `Recovering`, `OutOfService`, `NotConfigured`, `SafeMode`. Heç bir halda backend/provider təsdiqləməmiş ödəniş "uğurlu" göstərilmir və eyni job iki dəfə çap edilmir.

**Kiosk tətbiqində vəziyyətlər:** `Unregistered → Pending (Activation ekranı) → Active (NotConfigured / Online / Paused / Offline)`; `Revoked → Unregistered`. Cihaz kimliyi tətbiq qovluğundan kənarda saxlanılır, ona görə **yeniləmə, restart və ya tətbiqin yenidən quraşdırılması yenidən qeydiyyat tələb etmir**.

### 7.12 Provisioning, Agent, yeniləmə və restart

**Komponentlər**
| Komponent | Nədir | İşləyir |
|---|---|---|
| `provision.ps1` / `provision.sh` | OS-u kiosk rejiminə salan, asılılıqları və NeoPrint-i quraşdıran **idempotent** skript (təkrar işə salınanda yalnız fərqləri düzəldir) | Bir dəfə quraşdırmada; sonra uzaqdan `ReapplyProvisioning` ilə |
| `NeoPrint.Kiosk.Agent` | Sistem servisi (Windows Service `LocalSystem` / systemd `neoprint-agent`, `neoprint` istifadəçisi + məhdud sudoers) | Həmişə, UI-dan əvvəl başlayır |
| `NeoPrint.Kiosk` (UI) | Avalonia tətbiqi, məhdud `kiosk` istifadəçisi ilə | Shell Launcher (Windows) / `cage` (Linux) tərəfindən |

**Provisioning skripti — Windows (`provision.ps1`, Administrator kimi)**
- Edition yoxlaması (IoT Enterprise / Enterprise; digərlərində aydın mesajla dayanır).
- Lokal `kiosk` istifadəçisi (standart hüquqlar), avtomatik giriş (parol LSA secret kimi saxlanılır, registry-də açıq mətn yoxdur).
- **Shell Launcher v2**: `kiosk` istifadəçisinin shell-i = `NeoPrint.Kiosk.exe` (Explorer yoxdur, masaüstü, taskbar, Start yoxdur); tətbiq bağlanarsa avtomatik yenidən başladılır.
- **Keyboard Filter**: Win, Alt+Tab, Alt+F4, Ctrl+Esc, Ctrl+Alt+Del menyusu, Task Manager qısayolları bloklanır.
- **Heç bir bildiriş çıxmaması:** toast / Action Center / bildiriş mərkəzi söndürülür, Windows tips, Spotlight, "consumer features", "Get even more out of Windows", OneDrive, Widgets, Copilot, Teams təklifləri söndürülür / silinir; Defender bildirişləri gizlədilir (qoruma açıq qalır); xəta hesabatı UI-sı (`DontShowUI`), aşağı disk xəbərdarlığı, "yeni cihaz / printer quraşdırıldı" bildirişləri söndürülür; USB **AutoPlay** söndürülür (flash-ı tətbiq özü idarə edir).
- **Windows Update:** avtomatik restart və yeniləmə bildirişləri söndürülür (`NoAutoRebootWithLoggedOnUsers`, bildiriş səviyyəsi 2); yeniləmələr quraşdırılır, amma restart yalnız Agent tərəfindən `Kiosk.UpdateWindow`-da, queue boş olanda edilir. LTSC edition funksional yeniləmə təkliflərini göstərmir.
- **Toxunma və ekran:** kənar sürüşdürmə jestləri (`AllowEdgeSwipe=0`), toxunma klaviaturasının avtomatik açılması, kilid ekranı söndürülür; yuxu, hibernasiya, ekran qoruyucu, monitoru söndürmə — söndürülür; enerji planı "Yüksək performans".
- Printer: "Windows default printeri özü idarə etsin" söndürülür.
- Saat qurşağı `Azerbaijan Standard Time`, NTP.
- Asılılıqlar səssiz quraşdırılır: LibreOffice, Ghostscript (+ GhostPCL, GhostXPS), fontlar.
- NeoPrint: `C:\Program Files\NeoPrint\versions\{ver}\`, `current` junction; Agent servisi; `C:\ProgramData\NeoPrint\` (kimlik, konfiqurasiya keşi, spool, loglar) ACL ilə yalnız `SYSTEM` + `kiosk`.
- Firewall: daxil olan bütün bağlantılar bloklanır; çıxış yalnız backend-ə (443).
- Log: `C:\ProgramData\NeoPrint\provision.log`; sonda restart.

**Provisioning skripti — Linux (`provision.sh`, root kimi, Ubuntu 24.04 Server)**
- Paketlər: `cage`, `cups`, `cups-ipp-utils`, `udisks2`, `libreoffice-core/-writer/-calc/-impress` (GUI-siz), `ghostscript`, fontlar, `chrony`; masaüstü mühiti və bildiriş servisi **quraşdırılmır** (ekranda başqa pəncərə yaranası heç nə yoxdur).
- İstifadəçilər: `neoprint` (Agent), `kiosk` (UI); `tty1`-də `kiosk` üçün avtomatik giriş → `cage -s -- /opt/neoprint/current/NeoPrint.Kiosk`; UI çıxarsa yenidən başladılır.
- `logind`: `NAutoVTs=0`, `ReserveVT=0`, güc düyməsi ignore; `ctrl-alt-del.target` mask; SysRq söndürülür.
- Boot: GRUB timeout 0, `quiet splash`, konsol mesajları gizli, ekranın boşalması (blanking) söndürülür.
- `unattended-upgrades`: yalnız təhlükəsizlik yeniləmələri, **avtomatik reboot yoxdur** (Agent `Kiosk.UpdateWindow`-da edir); `apport` söndürülür.
- udisks2 + polkit qaydası: yalnız `kiosk` istifadəçisi üçün flash avtomatik mount (`/media/kiosk`), icra icazəsi olmadan (`noexec`).
- sudoers: `neoprint` yalnız `systemctl reboot`, `systemctl restart neoprint-*`, `lpadmin` / `cupsenable` icra edə bilər.
- `ufw`: daxil olan bağlantılar bloklanır; çıxış backend-ə.
- NeoPrint: `/opt/neoprint/versions/{ver}/`, `current` symlink; `/var/lib/neoprint/` (kimlik, keş, spool), `/var/log/neoprint/`.
- Saat qurşağı `Asia/Baku`.

**Skript parametrləri (hər iki OS):** `--api-url` (məcburi deyil — default Production), `--enrollment-token` (istəyə görə, Bölmə 7.11), `--channel pilot|stable`, `--offline-package <yol>` (internet olmadan quraşdırma üçün). Skript NeoPrint paketini backend-dən (`GET releases/latest`) və ya lokal paketdən götürür və **imzasını yoxlayır**.

**Golden image (çox kiosk üçün tövsiyə)**
- Bir maşın provisioning ilə hazırlanır → `prepare-image.ps1` / `prepare-image.sh` cihaz kimliyini, keşləri, logları, `machine-id`-ni təmizləyir (Windows-da `sysprep /generalize`) → image çıxarılır → bütün yeni kiosklara klonlanır.
- Klonlanmış hər maşın ilk açılışda **öz açarını yaradır** və ayrıca cihaz kimi qeydiyyatdan keçir.

**Uzaqdan idarəetmə — nə üçün kioskun yanında olmaq lazımdır, nə üçün yox**
| İş | Uzaqdan | Yerində |
|---|---|---|
| İlk OS quraşdırılması + provisioning (və ya hazır golden image-li maşının qoşulması) | — | ✔ (bir dəfə; image varsa yalnız kabelləri qoşmaq) |
| Aktivləşdirmə | ✔ enrollment token ilə tam uzaqdan; və ya pairing kodu (kodu yerindəki şəxs deyir / operator scan edir) | — |
| Kiosk parametrləri, printer seçimi, qiymət, limitlər, flag-lar | ✔ admin panel | — |
| Yeni versiya (UI, Agent, provisioning) | ✔ avtomatik və ya "İndi yenilə" | — |
| OS parametrlərinin yenidən tətbiqi / təmiri | ✔ `ReapplyProvisioning` | — |
| Restart, reboot, log toplama, test çap | ✔ | — |
| Kağız, toner, sıxışma, avadanlıq, disk dəyişmə, OS yenidən quraşdırılması | — | ✔ |

**Yeniləmə (öz updater-imiz — əlavə kitabxana yoxdur)**
1. CI hər buraxılış üçün paket (`zip` / `tar.gz`) + manifest (versiya, komponent, platforma, SHA-256, `MinAgentVersion`) yaradır və **ECDSA P-256 ilə imzalayır** (imza açarı OpenBao-da / CI secret-də; public açar Agent-in içindədir). Paket admin panelə / API-yə yüklənir, şifrəli storage-da saxlanılır.
2. Admin "Buraxılışlar" səhifəsində rollout yaradır: hədəf — hamısı / seçilmiş kiosklar / faiz / kanal (`pilot` → `stable`); pauza, geri çəkmə.
3. Agent hər `Update.CheckIntervalMinutes`-də (və SignalR `UpdateAvailable` gələndə) `GET kiosks/me/update` soruşur → paketi `versions/{ver}`-ə endirir → SHA-256 + imza yoxlanılır.
4. Tətbiq anı: `Kiosk.UpdateWindow` aralığında, **queue boş, aktiv sessiya / ödəniş yoxdursa** (və ya admin "İndi yenilə" — yenə boş anı gözləyir). UI-ya "yenilənir" ekranı göstərmək əmri → UI dayandırılır → `current` yeni versiyaya keçirilir → UI başladılır.
5. **Sağlamlıq yoxlaması:** yeni UI `Update.HealthTimeoutMinutes` ərzində backend-ə qoşulub token almalı, printer servisi və konfiqurasiya yüklənməlidir → Agent-ə "sağlam" siqnalı. Gəlməzsə → avtomatik **rollback** (`current` əvvəlki versiyaya), status `RolledBack`, incident (High); rollout həmin kiosk üçün dayanır.
6. Son `Update.KeepVersions` versiya saxlanılır; admin istənilən kiosku əvvəlki versiyaya qaytara bilər (`RollbackVersion`).
7. **Agent özünü yeniləmə:** yeni Agent paketi eyni qaydada endirilir, kiçik `NeoPrint.Kiosk.Agent.Updater` (paketin içində) servisi dayandırır, faylları dəyişir, servisi başladır; uğursuz olarsa köhnə versiyanı bərpa edir.
8. **Provisioning yeniləməsi:** yeni `provision` skripti paketlə gəlir və `ProvisioningVersion` dəyişibsə Agent onu təmir rejimində işə salır (OS parametrləri, asılılıqların yeni versiyaları).
- **Yeniləmədə yenidən qeydiyyat yoxdur:** cihaz açarı, konfiqurasiya keşi, spool, sayğac və loglar tətbiq qovluğundan kənardadır (`ProgramData` / `/var/lib/neoprint`) və toxunulmur.

**Restart və bərpa axını (elektrik kəsilməsi, reboot, çöküş, yeniləmə)**
1. Maşın açılır → OS avtomatik giriş → **Agent** başlayır → **UI** başlayır (Shell Launcher / cage); UI ilk anda "Kiosk başladılır" ekranı göstərir.
2. UI cihaz açarını yükləyir (yoxdursa → Activation, Bölmə 7.11) → **lokal konfiqurasiya keşini** oxuyur (backend əlçatmaz olsa da əsas ekran açılır; yeni ödəniş qəbul olunmur).
3. Challenge → Kiosk token → `GET kiosks/me/config` → SignalR → `Online`; telemetriyada restart səbəbi (`PowerLoss/Reboot/Crash/Update`) göndərilir.
4. **Yarımçıq job-ların bərpası** — UI hər job üçün lokal jurnal (`state.json`: jobId, spooler job id, mərhələ) saxlayır:
   - Job `Printing` idi → sistem spooler-də həmin job axtarılır: hələ çap olunur → izləməyə davam; tamamlanıb → `Completed` (+ `PrintedSheets`); tapılmır → `Interrupted` (səbəb `AppRestart`) → Bölmə 2.5 refund qaydası (anonim bank ödənişi → avtomatik karta; qeydiyyatlı → təklif). **Avtomatik təkrar çap edilmir** (ikiqat çapın qarşısını almaq üçün).
   - `Queued` job-lar sıra ilə davam edir (spool faylları qorunan açarla şifrəli olduğu üçün mövcuddur).
   - Restart zamanı ödəniş ekranında olan anonim istifadəçi: ödəniş sonradan təsdiqlənərsə job normal queue-ya düşür və çap olunur (JobCode queue panelində görünür); ödənilməzsə `Payment.TimeoutMinutes` sonra `Expired`.
5. **Watchdog:** UI çökərsə Agent (və Shell Launcher / systemd) onu 3 saniyədə yenidən başladır; 10 dəqiqədə `Kiosk.CrashLoopThreshold` çöküş → UI **təhlükəsiz rejimdə** açılır ("Texniki xidmət", sifariş qəbul etmir), Critical incident; son yeniləmədən sonradırsa avtomatik rollback.
6. Gözlənilməz reboot-lar və çöküşlər telemetriyada sayılır, admin panelində kiosk detalında göstərilir.

---

## 8. Mobil tətbiq (Flutter)

### 8.1 Naviqasiya (bottom nav)
`Ana səhifə` | `Kiosklar` | `Fayllarım` | `Planlanmışlar` | `Profil`

### 8.2 Ekranlar və axınlar
| Feature | Ekranlar / davranış |
|---|---|
| auth | Splash, Onboarding (1 dəfə), Register (+ referral kod sahəsi, deep link-dən avtomatik), Confirm email, Login, Forgot password |
| consents | Məxfilik siyasəti + İstifadə şərtləri qəbulu (qeydiyyatda və yeni versiyada), sənəd mətnləri backend-dən |
| home | Balans + bonus səhifə kartı, "QR scan" düyməsi (kiosk / ödəniş / voucher QR-ını tanıyır), aktiv job-lar, son qəbzlər |
| kiosk_search | Siyahı + xəritə (flutter_map + OpenStreetMap), axtarış (ad, kod, Id), yaxınlıq filtri, kiosk detalı (status, imkanlar, qiymətlər, queue uzunluğu, "kağız azdır" xəbərdarlığı), "Sessiya aç" |
| session | Aktiv sessiya banneri, fayl seç → print axını; sessiya TTL bitəndə bildiriş |
| files | Fayl yükləmə (file picker, kamera; HEIC → JPEG platforma API-si ilə), siyahı (mənbə badge-i: Yüklənib / Skan / Tərtibat / USB / Paylaşılıb; "Həssas" nişanı), baxış (backend səhifə şəkilləri), redaktə (PDF səhifələri, şəkil, TXT), versiyalar, avtomatik silinmə seçimi, sil, paylaş, "Kiosk üçün hazırla" |
| print | Parametrlər, promokod, bonus istifadə toggle, qiymət (seçilmiş kioskun profili + həcm endirimi), "Ödə" |
| payment | Metod seçimi: Balans / Kart (mock) / Bonus+qalan. Kioskdakı mobil ödəniş QR-ını scan edəndə birbaşa təsdiq ekranı |
| schedule | Planlama (kiosk + fayl + parametr + tarix/saat, min. 15 dəq sonra), siyahı, vaxtı dəyiş, ləğv (balansa refund). `ScheduleDue` push → "Çapa ver" / "Vaxtı dəyiş" dialoqu |
| wallet | Balans, bonus səhifələr, kartla artırma (mock; "geri qaytarılmır" xəbərdarlığı), tarixçə, voucher aktivləşdirmə |
| receipts | Qəbz siyahısı, PDF baxış (fiskal məlumatla), email göndər, paylaş (öz platform channel) |
| support | Job detalında **"Problem barədə bildir"** düyməsi (Bölmə 2.5, ssenari B): job seçilir, qısa mətn yazılır, müraciət göndərilir. Müraciətlərin siyahısı və statusu; admin cavab verəndə push. `Suspected` job-da düymə ekranın üst hissəsində görünür |
| referral | Kod, link, QR (backend PNG), paylaş, dəvət statistikası, qazanılan bonus |
| share | Fayl paylaş (PublicId daxil et və ya QR scan), göndərilənlər/alınanlar tarixçəsi, alınan faylı "Fayllarıma saxla"; həssas fayl paylaşılanda xəbərdarlıq |
| scanner | Sənəd skanı (öz platform channel), filtrlər, sənəd tipi / real ölçü preset-ləri, sonradan ölçü dəyişmə, ilk istifadədə məlumat/razılıq (Bölmə 2.11) |
| layout_editor | Səhifə tərtibatı redaktoru, şablonlar, tərtibatı yadda saxla / sonra redaktə et |
| privacy | "Məlumatlarımı yüklə", "Hesabımı sil" (şərtlər və xəbərdarlıqlarla), default avtomatik silinmə seçimi |
| notifications | Siyahı, oxundu; push-dan müvafiq ekrana deep link; ön planda gələn push üçün tətbiq daxili banner |
| profile | Ad, email, PublicId (kopyala), şəxsi QR (60 san yenilənir, parlaqlıq artırılır), dil, hüquqi sənədlər, çıxış |

- Söndürülmüş feature flag-a aid bölmələr UI-da gizlədilir (`GET features`).

### 8.3 Deep link-lər
`neoprint://kiosk/{code}`, `neoprint://pay/{id}`, `neoprint://voucher/{code}`, `https://neoprint.az/r/{code}`

### 8.4 Push tipləri
`JobCompleted`, `JobInterrupted_RefundOffer`, `KioskOutOfService_RefundOffer`, `ScheduleDue`, `ScheduleAutoCancelled`, `RefundCompleted`, `FileShared`, `ReferralRewarded`, `ExportReady`, `FileAutoDeleted`

---

## 8A. Admin web tətbiqi (Angular)

### 8A.1 Ümumi
- Ayrıca SPA, backend `/api/admin/*` və `/hubs/admin` istifadə edir. Admin konteynerinin nginx-i `/api` və `/hubs` yollarını backend-ə proxy edir — SPA və API **eyni origin**-dədir (refresh cookie `SameSite=Strict` problemsiz işləyir, CORS tələb olunmur). Development-də eyni iş `proxy.conf.json` ilə görülür.
- Layout: sol sidebar (icazələrə görə menyu), yuxarıda axtarış, dil seçimi, bildiriş zəngi (açıq incident və supply alert sayı), profil.
- Bütün siyahılar: server-side pagination, sıralama, filtr, CSV export.
- Dağıdıcı əməliyyatlar (ləğv, refund, blok, reboot, printer dəyişmə, provayder dəyişmə) təsdiq dialoqu tələb edir; wallet adjust, refund və incident resolve-da səbəb/qeyd məcburidir. Hamısı `audit_logs`-a yazılır.
- Real-time: incident və Critical supply alert gələndə səsli siqnal + brauzer bildirişi + toast; kiosk statusları və queue avtomatik yenilənir.
- Responsive, light/dark tema.

### 8A.2 Rollar və icazələr (dinamik RBAC)
- Admin paneldə **rol yaratmaq, redaktə etmək, klonlamaq, silmək** və rola icazələr vermək / almaq mümkündür.
- Bir admin istifadəçisinə **bir neçə rol** verilə bilər; effektiv icazələr rolların birləşməsidir.
- İstifadəçiyə əlavə olaraq **kiosk əhatəsi** verilir: `Bütün kiosklar` və ya seçilmiş kiosklar. Kiosk icazələri, queue, job, ödəniş, incident, servis tapşırıqları siyahıları avtomatik bu əhatəyə görə filtrlənir.
- `SuperAdmin` sistem rolu bütün icazələrə malikdir, redaktə və silinə bilməz. Sistemdə ən azı 1 aktiv `SuperAdmin` qalmalıdır.
- Admin öz rollarını və kiosk əhatəsini dəyişə bilməz. İstifadəçisi olan rol silinmir.
- İcazə dəyişikliyi `AdminPermissionsChanged` event-i ilə dərhal tətbiq olunur: backend icazə keşi (Redis, 5 dəq) təmizlənir, açıq sessiyalara SignalR `PermissionsChanged` göndərilir.
- Backend: `[HasPermission("payments.refund")]` + policy provider (əsas qoruma). Frontend: `permissionGuard` və `*appHasPermission` (yalnız UI).

**İcazə kataloqu** (kodda sabit, `GET admin/permissions` ilə qruplaşdırılmış)
| Qrup | İcazələr |
|---|---|
| Dashboard / hesabat | `dashboard.view`, `reports.view`, `analytics.view` |
| Kiosklar | `kiosks.view`, `kiosks.status.view`, `kiosks.create`, `kiosks.edit`, `kiosks.delete`, `kiosks.activate`, `devices.view`, `devices.revoke`, `devices.enroll`, `releases.view`, `releases.manage`, `kiosks.commands.update`, `kiosks.logs.download`, `kiosks.limits.manage`, `kiosks.commands.pause`, `kiosks.commands.restart`, `kiosks.commands.reboot` |
| Printerlər | `printers.view`, `printers.select`, `printers.capabilities.manage`, `printers.testprint`, `printers.refresh` |
| Sərf / servis | `supplies.view`, `paper.refill`, `supply.rules.manage`, `service.tasks.view`, `service.tasks.manage`, `service.tasks.complete`, `inventory.view`, `inventory.manage` |
| Operator paneli | `operator.panel` |
| Queue / job-lar | `jobs.view`, `jobs.cancel` |
| Ödənişlər | `payments.view`, `payments.refund`, `refunds.view`, `refunds.retry`, `receipts.view`, `vouchers.view`, `fiscal.view`, `fiscal.manage` |
| İstifadəçilər | `users.view`, `users.block`, `wallets.adjust` |
| Konfiqurasiya | `pricing.manage`, `settings.manage`, `promos.manage`, `referrals.view`, `formats.manage`, `features.manage`, `legal.manage` |
| Incident-lər | `incidents.view`, `incidents.acknowledge`, `incidents.resolve` |
| Sistem | `storage.manage`, `admins.manage`, `roles.manage`, `audit.view` |

**Seed olunan rol şablonları** (`SuperAdmin`-dən başqa hamısı dəyişdirilə və silinə bilər)
| Rol | İcazələr |
|---|---|
| `SuperAdmin` (sistem) | Hamısı |
| `Admin` | `storage.manage`, `admins.manage`, `roles.manage`, `audit.view` xaric hamısı |
| `Operator` | `operator.panel`, `dashboard.view`, `kiosks.view`, `kiosks.activate`, `devices.view`, `kiosks.status.view`, `kiosks.commands.*`, `printers.view`, `printers.testprint`, `printers.refresh`, `supplies.view`, `paper.refill`, `service.tasks.view`, `service.tasks.complete`, `inventory.view`, `jobs.view`, `incidents.*` |
| `Maliyyə` | `dashboard.view`, `reports.view`, `payments.*`, `refunds.*`, `receipts.view`, `vouchers.view`, `fiscal.*`, `users.view`, `wallets.adjust`, `jobs.view` |

### 8A.3 Səhifələr
| Səhifə | Məzmun |
|---|---|
| Login | Email, şifrə, blok mesajı, ilk girişdə şifrə dəyişmə |
| Dashboard | Kiosk status kartları (Online/Paused/Offline/NotConfigured), açıq incident-lər, aktiv supply alert-lər (Warning/Critical), gecikən servis tapşırıqları, az anbar qalığı, uğursuz refund və fiskal çek sayı, DLQ sayı, bugünkü job sayı və gəlir, son 7 günün gəlir qrafiki, "Konfiqurasiya tələb olunur" siyahısı |
| Kiosklar | Siyahı + xəritə görünüşü (Leaflet), status filtri |
| Kiosk yarat/redaktə | Ad, kod (avtomatik `KSK-XXXX`), ünvan, xəritədə nöqtə seçimi, queue göstərmə limiti (3/5), **maksimum fayl ölçüsü** və **maksimum səhifə** ("Default istifadə et"), **qiymət profili**, **fiskal terminal ID**, **kağız kapasitetləri** (A4/A3), **default dil**, **admin PIN** (dəyişdirmək mümkün). Kiosk qeydi cihazsız yaradıla bilər (`NotActivated`) və sonra cihaz bağlanır |
| Cihazlar | Tab-lar: **Aktivləşdirmə gözləyənlər** (kod ilə axtarış, cihaz məlumatı, printerlər, "Yeni kiosk kimi aktivləşdir" / "Mövcud kioska bağla" sehrbazı) · **Aktiv cihazlar** (kiosk, platforma, versiya, son əlaqə, barmaq izi, "Cihazı ayır") · **Enrollment token-lər** (yarat — kiosk seçimi və ya yeni kiosk şablonu; hazır provisioning əmrini kopyala; ləğv et) · **Ləğv edilmiş / vaxtı keçmiş** |
| Buraxılışlar | Paketlər (komponent, platforma, versiya, kanal, imza statusu), rollout yarat (hamısı / kiosklar / faiz / kanal), pauza / davam / geri çək, kiosk versiyaları cədvəli (UI, Agent, provisioning, yeniləmə statusu, son xəta) |
| Kiosk detalı | Tab-lar: **Cihaz** (aktiv cihaz, cihaz tarixçəsi, "Cihazı ayır", "Cihazı dəyiş") · **Cari vəziyyət** (Bölmə 7.9 canlı, sağlamlıq göstəriciləri, son 24 saat / 7 gün qrafikləri) · **Sərf** (kağız sayğacları, toner səviyyələri, proqnoz, doldurma tarixçəsi, kiosk üçün xəbərdarlıq qaydaları) · **Printerlər** (Bölmə 7.7; printerə klik → printer detalı) · **Queue** · **Job tarixçəsi** · **Servis tapşırıqları** · **Incident-lər** · **Əmrlər** |
| Printer detalı | Bölmə 7.10: identifikasiya, canlı status, sərf materialları (progress bar-lar), lotoklar, sayğaclar (qrafik), imkanlar, spooler job-ları, tarixçə, "Test çap", "Default et", "Yenilə" |
| Printerlər (ümumi) | Bütün kiosklardakı printerlər: kiosk, model, status, toner səviyyələri, lifetime sayğac; filtr "az toner", "xətalı", "offline" |
| Xəbərdarlıq qaydaları | Kağız / toner qaydaları (Bölmə 2.15): qlobal və kiosk üzrə, Warning / Critical həddləri, kanallar, avtomatik tapşırıq |
| Servis tapşırıqları | Siyahı (status, prioritet, kiosk, operator), yarat, təyin et, tarixçə, SLA göstəricisi |
| Anbar | Məhsullar, qalıqlar, min qalıq, hərəkətlər (alış, operatora verilmə, istifadə, silinmə), düzəliş |
| Print job-lar | Filtrli siyahı, detal (status tarixçəsi, fayl metadata, ödəniş, qəbz), ləğv |
| **Baxış gözləyən job-lar** | `Suspected` job-ların araşdırma siyahısı (Bölmə 2.5, ssenari B). Sıralama: ən köhnə yuxarıda; `Job.SuspectedAutoResolveHours` keçənlər ayrıca işarələnir. **Araşdırma kartında göstərilir:** job (kod, fayl, səhifə/vərəq sayı, parametrlər), ödəniş (məbləğ, metod, provider istinadı, fiskal çek), kiosk, vaxtlar, müştəri məlumatı (varsa); kioskun **sonuncu heartbeat vaxtı və telemetriyası**; `print_attempts` qeydləri, **printer sayğacının əvvəl/sonra dəyəri**; kioskun bərpadan sonra göndərdiyi jurnal (gəlibsə); **eyni vaxtda həmin kioskda baş vermiş digər hadisələr** (incident-lər, digər job-lar, sərf hadisələri). **Əməliyyatlar:** "Tam qaytar", "Hissəvi qaytar", "Qaytarma yoxdur (çap olunub)", "Yenidən çapa göndər" — səbəb və qeyd məcburi, hamısı `audit_logs`-a düşür. Bağlı müştəri müraciətləri də burada görünür |
| Ödənişlər | Siyahı (filtrlər: status, metod, kanal, kiosk, "xəta olub", "refund gözləyir", "refund uğursuz"); **ödəniş detalı**: məbləğ bölgüsü (kart / wallet / bonus / promokod / həcm endirimi), bağlı **job(lar)** və status tarixçəsi, kiosk, istifadəçi, qəbz, **fiskal çek**, provider istinadı, tam **timeline**, xəta səbəbi, **refund-lar**; əməliyyatlar: "Job-u ləğv et və qaytar", "Qaytar" (tam / hissəvi, səbəb məcburi, istiqamət: account varsa **Wallet** və ya **bank kartı**, anonimdə bank kartı / fallback voucher), uğursuz refund-u "Təkrarla" / "İstiqaməti dəyiş" |
| Refund-lar | Bütün refund-lar, status və səbəb filtri, uğursuzlar ayrıca tab-da |
| Fiskal çeklər | Siyahı, status filtri, uğursuzları təkrarla, fiskal parametrlər (onlayn terminal ID, top-up rejimi) |
| Qəbzlər, Voucher-lər | Siyahı, PDF baxış, voucher statusu |
| İstifadəçilər | Axtarış (email, PublicId), detal: profil, wallet + tarixçə, job-lar, fayl metadata-sı (məzmun yox), referral-lar, razılıqlar; blok/unblok, balans/bonus düzəlişi |
| Qiymət profilləri | Profil CRUD, default profil, həcm endirimi pillələri, kiosklara bağlama (çoxlu seçim), kalkulyator, dəyişiklik tarixçəsi |
| Tənzimləmələr | Bölmə 2.4 açarları (tipə görə input, default-a qaytar) |
| Fayl formatları | Format siyahısı, yandır/söndür, default limitlər (ölçü, səhifə, kvota) |
| Promokodlar | CRUD, kiosk / profil məhdudiyyəti, statistika, aktiv/deaktiv |
| Referral | Statistika, top dəvət edənlər, bonus tarixçəsi |
| Feature flag-lar | Flag siyahısı, default, kiosk üzrə override (kiosk seçimi), mobil rollout faizi, allowlist, tarixçə |
| Pilot hesabatı / Analitika | Bölmə 2.17 hesabatları, tarix və kiosk filtri, CSV |
| Hüquqi sənədlər | Məxfilik siyasəti və istifadə şərtləri versiyaları (az/en/ru), yeni versiya dərc et |
| Incident-lər | Canlı siyahı (Critical yuxarıda), acknowledge, resolve, təsirlənən ödənişlər |
| Fayl saxlama | Provayderlər (MinIO / Scality): əlavə et, redaktə, bağlantı testi, Primary / Secondary, sağlamlıq; Sinxronizasiya: yeni iş sehrbazı, canlı progress, Pause/Resume/Cancel, uğursuzlar, mənbəni təmizlə |
| Audit log | Filtrli siyahı, before/after diff |
| Admin istifadəçiləri | CRUD, rollar (çoxlu seçim), kiosk əhatəsi, Telegram chat ID, aktiv/deaktiv, şifrə sıfırlama, effektiv icazələr |
| Rollar | Rol siyahısı (istifadəçi sayı ilə), yarat / redaktə / klonla / sil; icazə checkbox matrisi, tarixçə |

---

## 8B. Operator paneli (mobil brauzer / PWA)
- Eyni Angular tətbiqdə `/operator` bölməsi, **mobil-yönümlü** dizayn (tək sütun, iri düymələr), Angular Service Worker ilə PWA kimi telefona quraşdırıla bilir. `operator.panel` icazəsi olan istifadəçi girişdən sonra birbaşa buraya yönləndirilir (admin icazələri də varsa tam panelə keçid düyməsi).
- Bütün məlumat istifadəçinin **kiosk əhatəsi** ilə məhdudlaşır.

| Ekran | Məzmun |
|---|---|
| Mənim kiosklarım | Hər kiosk üçün sağlamlıq kartı: status rəngi, printer statusu, kağız (A4/A3 qalıq + proqnoz), toner səviyyələri, queue, son heartbeat, açıq alert/incident sayı. Sıralama: problemlilər yuxarıda |
| Kiosk (qısa detal) | Əsas telemetriya, printer statusu və səbəbləri, sərf səviyyələri, aktiv alert-lər; düymələr: **Kağız doldur**, **Toner dəyişdirildi**, Test çap, Pause/Resume, Restart, Printerləri yenilə |
| Kağız doldur | Ölçü (A4/A3), "Əlavə edilən vərəq" / "Tam doldur" / "Dəqiq say", anbardan çıxılacaq məhsul (default kağız bağlaması) |
| Tapşırıqlarım | Təyin olunmuş və hovuzdakı tapşırıqlar; Qəbul et → Başla → Tamamla (sərf məlumatı, qeyd) |
| Incident-lər | Kiosklarıma aid incident-lər, acknowledge / resolve |
| Anbar | Qalıqlara baxış, özünə götürülən materialın qeydi |
| Kiosk aktivləşdir | Kiosk ekranındakı aktivləşdirmə QR-ını telefon kamerası ilə scan et (brauzer kamera API-si + backend QR decode) və ya kodu daxil et → "Mövcud kioska bağla" və ya (icazə varsa) "Yeni kiosk" → Aktivləşdir → printer seç → test çap |

- Real-time bildirişlər: yeni tapşırıq, Critical alert, incident — səs + brauzer bildirişi (PWA açıq olduqda) + Telegram/email.
- Zəif internet üçün: son yüklənmiş vəziyyət keşdən göstərilir, "son yenilənmə vaxtı" yazılır; əməliyyatlar yalnız onlayn icra olunur.

---

## 9. Kritik axınlar (ardıcıllıq)

### 9.0 Yeni kioskun qoşulması (universal build)
1. Maşına OS quraşdırılır və standart provisioning skripti işə salınır (`provision.ps1` / `provision.sh`) — və ya hazır golden image-li maşın qoşulur. Heç bir kiosk-a aid build yoxdur
   - **Sıfır toxunuş variantı:** admin əvvəlcədən kiosk qeydi + enrollment token yaradır, skript `--enrollment-token` ilə işə salınır → cihaz qeydiyyatda dərhal aktivləşir, 3-cü addım buraxılır
2. Tətbiq işə düşür → cihaz açarı yaradır → `devices/register` → ekranda pairing kodu + QR (Activation)
3. Admin panel → Cihazlar → kod (və ya Operator paneli ilə QR scan) → "Yeni kiosk kimi aktivləşdir" (ad, məkan, qiymət profili, limitlər, kağız kapasiteti, fiskal terminal, admin PIN) və ya "Mövcud kioska bağla"
4. `DeviceActivated` → kiosk token alır → konfiqurasiyanı yükləyir → printerləri göndərir → `NotConfigured`
5. Admin/Operator: "Default et" → "Test səhifəsi"; operator ilk kağız doldurmanı qeyd edir (sayğac başlanğıcı)
6. Test uğurludur → kiosk `Online`, mobil axtarışda görünür

### 9.1 Flash kartdan çap (anonim, bank QR)
1. Kiosk: fayl seç → redaktə → parametrlər (qiymət kioskun profilindən) → kağız yoxlaması → `POST print-jobs` (Source=Flash) → `Draft`
2. `POST payments/intents` (BankQr) → job `PendingPayment`, QR göstərilir
3. Provider callback → `PaymentSucceeded` → fiskal satış çeki (asinxron) → job `Paid` → `Queued` → `KioskHub.PaymentConfirmed` + `QueueUpdated`
4. Receipt ekranı, istəyə görə email → `ReceiptRequested`
5. Çap → `Completed` (`PrintedSheets` → kağız sayğacı azalır) → lokal fayl silinir
6. Xəta olarsa → job `Cancelled` + avtomatik karta refund + qaytarma fiskal çeki → RefundInfo ekranı

### 9.2 Kioskda mobil tətbiqlə ödəniş
1–2 eyni, metod `MobileApp` → QR
3. Mobil scan → `GET payments/{id}` → təsdiq ekranı → `POST payments/{id}/pay-with-app` (Wallet/Card/Bonus)
4. Job istifadəçiyə bağlanır (`UserId`), fiskal çek və qəbz hesabda saxlanılır
5. Job flash faylındandırsa: kiosk faylı `flash-upload` ilə yükləyir → istifadəçinin "Fayllarım" siyahısında `USB` mənbəli fayl kimi görünür
6. 9.1 addım 3-dən davam (xəta halında refund Bölmə 2.5 qaydası ilə)

### 9.3 Mobil sessiya ilə çap
1. Mobil: kiosk QR scan və ya axtarış → `POST sessions` → `KioskHub.SessionOpened` (kioskda kiçik bildiriş)
2. Fayl seç/yüklə → baxış → parametrlər → `POST print-jobs` (Source=MobileSession) → ödəniş → `Queued`
3. Kioskda queue-ya düşür, istifadəçi `UserHub` ilə status alır

### 9.4 Şəxsi QR ilə kioskda çap
1. Mobil: "Kiosk üçün hazırla" → fayl + parametrlər saxlanılır (ödənişli və ya ödənişsiz)
2. Kiosk: QR skaner `NPU:...` oxuyur → HMAC və vaxt yoxlanır → `GET print-jobs/pending-for-user`
3. UserQrFiles ekranı → seç → ödənilməyibsə 9.1/9.2 ödəniş → `Queued`

### 9.5 Planlanmış çap
1. Mobil: `POST schedules` + ödəniş → `Scheduled`
2. Vaxt çatır → `AwaitingUser` → push
3. "Çapa ver" → `POST schedules/{id}/print-now` → kiosk online-dırsa və kağız kifayətdirsə job `Queued`, deyilsə xəta + vaxt dəyişmə təklifi
4. "Vaxtı dəyiş" → `PUT schedules/{id}/time` → `Scheduled`
5. Ləğv və ya `AutoCancelHours` → balansa refund (+ qaytarma fiskal çeki)

### 9.6 Kiosk nasazlığı

İki ayrı axın var və fərq budur: **kiosk nə baş verdiyini deyə bilirmi?**
(Bölmə 2.5)

**A — printer xətası, kiosk serverlə əlaqədədir**

1. Printer xətası → incident (Critical) → Admin/Operator panel real-time + email + Telegram; lazımi hallarda servis tapşırığı
2. Kiosk `Paused/Offline` → OutOfService ekranı, yeni sifariş bloklanır, mobil axtarışda "Dayanıb" statusu
3. `Printing` job → `Interrupted`
4. Kioskda bank QR / kartla ödənilmiş anonim job-lar (`Interrupted` və `Queued`) → avtomatik `Cancelled` + karta refund; ekranda refund məlumatı
5. Qeydiyyatlı istifadəçilərin job-ları → push: "Gözləyin və ya ləğv edib məbləği qaytarın" → ləğv edərsə Bölmə 2.5 qaydası ilə refund; ləğv etməzsə kiosk bərpa olunanda çap davam edir
6. Admin paneldə incident detalında təsirlənən ödənişlər və refund statusları görünür

**B — kiosk ilə əlaqə itib (elektrik, şəbəkə, avadanlıq)**

1. Heartbeat gəlmir → `KioskHeartbeatMonitor` kiosku `Offline` edir, incident (Critical)
2. `Job.SuspectedAfterMinutes` (15) keçir → `StuckJobWatchdogJob` `Printing` / `Queued` job-ları **`Suspected`** edir. **Avtomatik ləğv və refund yoxdur**; ödəniş `Succeeded` qalır
3. Job admin panelində "Baxış gözləyən job-lar" siyahısına düşür, incident (High)
4. Qeydiyyatlı istifadəçiyə bildiriş: "Çapınızın nəticəsi dəqiqləşdirilir" + "Problem barədə bildir" düyməsi. Anonim müştəri qəbzdəki refund status QR-ı ilə müraciət edə bilər
5. **Kiosk qalxıb nəticə göndərsə** → `SuspectedJobResolutionConsumer` avtomatik həll edir: çap tamamlanıb → `Completed`; olmayıb → A ssenarisi, avtomatik refund
6. Kiosk qayıtmazsa → admin araşdırma kartından qərar verir (tam / hissəvi qaytarma, qaytarma yoxdur, yenidən çap). `Job.SuspectedAutoResolveHours` (72) keçəndə yalnız xatırlatma gedir

### 9.7 Skan → tərtibat → çap
1. Mobil: Skan → vəsiqə ön və arxa → preset "Şəxsiyyət vəsiqəsi (ID-1)" (həssas, avtomatik silinmə "Çapdan sonra")
2. "Səhifədə yerləşdir" → şablon "Vəsiqə ön + arxa" → A4-də real ölçüdə yerləşir → lazım olsa düzəliş
3. Yadda saxla → `POST files/layout` → backend PDF yaradır → "Fayllarım"
4. Kiosk seç / sessiya / planlama → PrintOptions (kağız və real ölçü kilidli) → ödəniş → çap
5. Çap tamamlanır → həssas skanlar və tərtibat avtomatik silinir → push `FileAutoDeleted`

### 9.8 Storage migrasiyası (MinIO → Scality)
1. Fayl saxlama → Scality provayderini əlavə et → bağlantı testi uğurlu
2. Sinxronizasiya → Migrasiya, mənbə MinIO, hədəf Scality, "Bitəndə Primary et" ✔ → Başlat
3. Dual-write aktivləşir, `StorageMigrationWorker` checkpoint-lə kopyalayır (şifrəli məzmun olduğu kimi), panel canlı progress göstərir
4. Yoxlama (ölçü + SHA-256) → uğursuzlar təkrarlanır → Scality Primary olur
5. `SourceRetentionDays` sonra admin "Mənbəni təmizlə" ilə MinIO obyektlərini silir (və ya MinIO-nu Secondary edib Mirror rejiminə keçir)

### 9.9 Kağız bitməyə yaxınlaşır
1. Job tamamlanır → `PrintedSheets` → `SupplyLevelChanged`
2. `SupplyEvaluator`: A4 qalıq 200-dən aşağı → Warning alert (Admin + Operator paneli), qayda `CreateServiceTask=true` → `RefillPaper` tapşırığı → kiosk əhatəsindəki operatora təyin → bildiriş
3. Qalıq 50-dən aşağı → Critical alert (səs + Telegram), tapşırıq prioriteti Critical-a qalxır
4. Operator: Operator paneli → tapşırığı qəbul et → Başla → kioskda kağız doldur → "Tam doldur" → Tamamla
5. Sayğac Capacity-yə qalxır, anbardan kağız bağlaması çıxılır, alert-lər avtomatik bağlanır
6. Əgər doldurulmazsa və job üçün kağız çatmırsa: yeni sifarişlər qəbul olunmur (Bölmə 2.15)

### 9.10 Fiskallaşdırma
1. `PaymentSucceeded` → outbox → `FiscalizationRequested`
2. `FiscalizationConsumer` → `IFiscalProvider.IssueSaleAsync` (kioskun `FiscalTerminalId` və ya onlayn terminal)
3. Uğurlu → `fiscal_receipts.Issued` → qəbz PDF-i fiskal məlumatla yenilənir → (email verilibsə) yenidən göndərilir
4. Uğursuz → retry; `Fiscal.MaxPendingMinutes` keçərsə incident (High) → admin "Fiskal çeklər" səhifəsindən təkrarlayır
5. Refund-da eyni axın `IssueRefundAsync` ilə

---

## 10. Təhlükəsizlik və etibarlılıq
- Backend secret-ləri yalnız OpenBao-dan (AppRole, read-only policy, audit); Production-da OpenBao əlçatmaz və ya məcburi secret yoxdursa → tətbiq başlamır (Bölmə 2.13).
- Bütün storage obyektləri envelope encryption ilə şifrəli; fayllar yalnız backend vasitəsilə, icazə yoxlaması ilə verilir.
- Wallet dəyişiklikləri: Mongo transaction + `Version` optimistic concurrency + Redis lock `wallet:{userId}`.
- Ödəniş callback: provider imza yoxlaması (Mock-da shared secret), idempotent emal (`ProviderRef` unikal indeks).
- Event-lər MassTransit transactional outbox ilə (itmir), consumer-lər idempotent (inbox).
- Kiosk kimliyi cihazda yaradılan, ixrac olunmayan açarla (TPM varsa TPM-də) təsdiqlənir; aktivləşdirmə admin tərəfindən, ləğv dərhal (Bölmə 7.11).
- Kiosk yalnız öz `KioskId`-sinə aid job-lara çıxış əldə edir (policy-based authorization).
- İstifadəçi yalnız öz fayl/job/qəbzlərinə çıxış əldə edir; paylaşılan fayl yalnız alana görünür; admin fayl məzmununa çıxış əldə edə bilmir.
- Bütün admin əməliyyatları `audit_logs`-a yazılır.
- Konvertasiya ayrıca prosesdə, vaxt limiti ilə (tam izolyasiya və virus yoxlaması — Addım 8).
- HTTPS məcburi (Production), HSTS, təhlükəsizlik header-ləri (CSP admin üçün).
- **Idempotency ilk gündən:** pul, job, fiskal, voucher, referral, aktivləşdirmə və bütün consumer-lər üçün (`docs/IDEMPOTENCY.md`) — Addım 7-yə saxlanılmır.
- **CorrelationId** hər sorğuda yaradılır / qəbul edilir və API → Application → RabbitMQ → Worker → Kiosk → Printer → Payment/Refund/Fiscal zənciri boyunca ötürülür; structured JSON log-larda `docs/OBSERVABILITY.md`-dəki sahələr olur (token, secret, fayl məzmunu və artıq PII log-a yazılmır). Tam OpenTelemetry / Grafana / GlitchTip yığımı Addım 8-dədir.
- Konvertasiya prosesi şəbəkəsiz işləyir (Linux: ayrıca network namespace / `--no-network` wrapper; Windows: firewall qaydası ilə icazəsiz proses), temp qovluq hər çağırışdan sonra təmizlənir.

---

## 11. Test tələbləri
- Test framework: xUnit + FluentAssertions 7.x + Testcontainers (Mongo — replica set rejimində, Redis, RabbitMQ, MinIO, Zenko CloudServer). Mock kitabxanası yoxdur — əl ilə yazılmış fake-lər; MassTransit test harness.
- Domain: status maşını, qiymət formulu (profil, həcm endirimi, bonus, promokod ardıcıllığı), refund hesablaması və istiqamət matrisi, wallet qaydaları — unit test (coverage ≥ 80% Domain üçün).
- OpenBao: Testcontainers ilə OpenBao (dev rejimi) — AppRole login, KV v2 oxuma, token renew, çatışmayan açarda başlamama, rotasiya olunan secret-in restart-sız yenilənməsi.
- Crypto: envelope encryption round-trip, chunk sırasının dəyişdirilməsinin aşkarlanması, KEK rotasiyası, səhv açarla oxuma xətası.
- Application: əsas handler-lər (create job, pay, cancel, refund, fiskal, schedule, referral reward, account deletion).
- **Recovery/idempotency ssenariləri (məcburi):** ödənişdən sonra proses çöküşü, `Printing` zamanı reboot, Redis itkisi, RabbitMQ müvəqqəti itkisi, printer offline, dublikat ödəniş callback-i, dublikat refund event-i, uğursuz yeniləmə + rollback, eyni `Idempotency-Key` ilə təkrar sorğu, eyni `ProviderEventId` ilə ikinci webhook.
- Integration: 9.0–9.10 axınları API səviyyəsində; permission policy-ləri (icazəsiz rol 403, rol dəyişikliyi dərhal tətbiq, kiosk əhatəsi filtri, son SuperAdmin qorunması); MassTransit outbox → consumer çatdırılması, Hangfire job-larının icrası.
- Fayllar: `tests/fixtures/formats/` hər format qrupu üçün konvertasiya + səhifə sayı, magic bytes, effektiv limit (default + kiosk override).
- Storage: migrasiya (restart-dan checkpoint davamı daxil), mirror, reconciliation, bütün provayderlərdən silinmə.
- Tərtibat: mm → PDF point və real ölçü (ID-1 kart PDF-də 85.6 × 53.98 mm).
- Sərf: sayğac azalması (simplex/duplex/n-up), qayda qiymətləndirmə (Warning/Critical, təkrar bildiriş olmaması, bağlanma), proqnoz, tapşırıq avtomatik təyinatı.
- Telemetriya: Windows/Linux printer atribut parser-ləri (IPP, SNMP, WMI nümunə cavabları), `/proc` parser-ləri, hədd qiymətləndirməsi.
- Yeniləmə: imza / SHA-256 uyğunsuzluğunda paketin rədd edilməsi, boş an gözləmə, sağlamlıq yoxlaması uğursuzluğunda rollback, Agent özünü yeniləmə, yeniləmədən sonra kimliyin və spool-un qorunması. Bərpa: `state.json` jurnalı ilə `Printing` / `Queued` job ssenariləri (Fake printer + spooler simulyasiyası).
- Provisioning: skriptlərin idempotentliyi (Linux — konteyner / VM-də CI testi; Windows — Pester ilə yoxlama skripti), `prepare-image` sonrası cihaz kimliyinin olmaması.
- Cihaz aktivləşdirmə: register → pairing → activate → token axını, enrollment token ilə avtomatik aktivləşmə, kod müddəti və təkrar istifadəsi, rate limit, revoke-dan sonra token alınmaması və cihazın Activation ekranına qayıtması, cihaz dəyişikliyində köhnə cihazın ləğvi, eyni build-in iki fərqli kioska aktivləşdirilməsi.
- Desktop: ViewModel unit testləri, `FakePrinterService` ilə çap icrası, CUPS çıxış parser-ləri.
- Mobile: widget testləri (print options, payment, layout editor koordinatları), repository testləri, öz SignalR klientinin protokol testləri.
- Admin web: servis və guard testləri, əsas komponentlər (printer tab, refund dialoqu, icazə matrisi, operator kağız doldurma).
- CI (`.github/workflows/ci.yml`): backend build + test, desktop build + test (matrix: `windows-latest`, `ubuntu-latest`), flutter analyze + test, admin lint + test + build, **lisenziya yoxlaması** (`tools/license-check`).

---

## 12. İcra planı

### FAZA 1 — MVP (Addım 1–7)

#### Addım 1 — Təməl, təhlükəsizlik əsası, autentifikasiya, RBAC
**İşlər**
- Repo strukturu, `.gitignore` (.NET, Flutter, Node/Angular, IDE, `.env*`, `.openbao/`), `.env.openbao-seed.example`, `README.md`, `docs/DECISIONS.md`, `docs/THIRD_PARTY.md`
- OpenBao: compose servisləri, `openbao/` (server konfiqi, policy-lər, `openbao-init` skripti), öz OpenBao klienti + `OpenBaoConfigurationProvider`, `docs/OPENBAO.md`
- `docker-compose.yml` (Bölmə 4) + backend `Dockerfile` (LibreOffice, Ghostscript, fontlar, PDFium)
- `tools/license-check` + CI workflow (build, test, lisenziya)
- Backend solution, layer-lər, MediatR 12 (pipeline behavior-lar: validasiya, logging, transaction), validator-lar, ProblemDetails, health checks, Swagger (yalnız Development), OpenBao-dan secret yükləmə və validasiya
- Shared: `NeoPrint.Crypto` (envelope encryption, KEK rotasiya modeli) + testlər; `Money` value object + mərkəzi yuvarlaqlaşdırma
- **Kross-kəsən infrastruktur (ilk gündən):** `Idempotency-Key` middleware + `idempotency_keys`, `provider_events` unikal indeksi, CorrelationId middleware + structured JSON logging, explicit state transition bazası (`state_transitions`, domain transition metodları, icazəsiz keçiddə `DomainException`), rate limiting
- Mongo konfiqurasiya, indekslər, Domain entity-ləri; MassTransit 8 (RabbitMQ, Mongo outbox/inbox); Hangfire (Mongo storage, dashboard)
- Seed: SuperAdmin + rol şablonları, default qiymət profili, `admin_settings`, supply alert qaydaları, feature flag-lar, hüquqi sənəd qaralamaları; Development-də 2 test kiosk qeydi + hər biri üçün enrollment token (log-a yazılır; lokal kiosk tətbiqi `--enrollment-token` ilə dərhal aktivləşir)
- Auth: istifadəçi (register, confirm email, login, refresh), razılıqlar (`legal_documents`, `consents`), cihaz qeydiyyatı və aktivləşdirmə (`kiosk_devices`, device challenge, Provisioning/Kiosk token-ləri, `/hubs/provisioning`, Bölmə 7.11); admin auth (cookie refresh, ilk girişdə şifrə dəyişmə); dinamik RBAC (`admin_roles`, icazə kataloqu, `HasPermission`, kiosk əhatəsi); audit log middleware
- Feature flag mühərriki (backend qiymətləndirmə, `GET features`, `[RequireFeature]`)
- Desktop: Avalonia solution + platforma layihələri, `MainWindow` layout (header, footer kiosk QR + kod, queue placeholder), fullscreen/Topmost, naviqasiya, lokalizasiya, ekran klaviaturası, **universal build**: cihaz açarı (Windows CNG/TPM, Linux fayl), `devices/register`, Activation ekranı, aktivləşdirmədən sonra token + `kiosks/me/config`, heartbeat; `NeoPrint.Kiosk.Agent` skeleti (servis, UI-ı başlatma/izləmə); provisioning skriptlərinin ilk versiyası (asılılıqlar, istifadəçilər, servislər, autologin)
- Mobile: Flutter layihə, Riverpod, go_router, Dio (token interceptor, refresh), öz secure storage channel-ı, öz SignalR klienti (skelet), tema, l10n, auth + razılıq ekranları, bottom nav skeleti
- Admin web: Angular layihə, Material, i18n, layout (icazəyə görə menyu), login, interceptor/guard, Admin istifadəçiləri, Rollar (icazə matrisi), Cihazlar (aktivləşdirmə gözləyənlər, aktivləşdirmə sehrbazı, cihazı ayır), Tənzimləmələr, Feature flag-lar, Hüquqi sənədlər; `Dockerfile` + compose

**Definition of Done**
- `docker compose up` ilə OpenBao init/unseal olunur, secret-lər seed edilir, bütün servislər qalxır, `/health` OK (OpenBao bağlantısı daxil); API heç bir secret-i env-dən almır; OpenBao-da məcburi açar silinəndə Production rejimində API başlamır
- Mobil tətbiqdə qeydiyyat → razılıq → email kodu (Mailpit) → login işləyir
- **Eyni kiosk build-i** iki fərqli maşında (Windows və Linux) heç bir əl ilə konfiqurasiya olmadan işə düşür, Activation ekranında kod göstərir; admin paneldən biri yeni kiosk kimi, digəri mövcud kioska aktivləşdirilir → hər biri öz konfiqurasiyasını alır, footer-də öz QR-ı + kodu, heartbeat gedir; "Cihazı ayır"-dan sonra cihaz Activation ekranına qayıdır
- SuperAdmin ilə daxil olunur, yeni rol yaradılıb icazələr verilir, həmin rolun istifadəçisi yalnız icazəli səhifələri görür, digər API-lər 403, icazə dəyişikliyi yenidən login olmadan tətbiq olunur
- Lisenziya yoxlaması və CI yaşıldır

#### Addım 2 — Fayl saxlama (şifrəli), provayderlər, konvertasiya, formatlar, qiymət
**İşlər**
- `IFileStorage` (S3 provayder, şifrələmə ilə stream yazma/oxuma), `storage_providers` (env seed), provayder idarəsi, bağlantı testi, Primary/Secondary
- Storage sinxronizasiyası: migrasiya (dual-write, checkpoint, yoxlama, avtomatik Primary), mirror, reconciliation, health check + fallback, bütün provayderlərdən silinmə (Bölmə 2.10)
- Files API (upload, list, content stream, pages png, edit, delete, versiyalar), magic bytes, `DocumentConversionConsumer` (LibreOffice və Ghostscript ayrıca proses + vaxt limiti, SkiaSharp, NeoPrint.Pdfium), `Files.*` limitləri, format idarəsi
- `NeoPrint.Pricing` (qiymət profilləri, həcm endirimi, snapshot) + `pricing/quote`
- Admin web: Fayl saxlama (provayderlər + sinxronizasiya sehrbazı + canlı progress), Fayl formatları, Qiymət profilləri (CRUD, kiosklara bağlama, kalkulyator), Kiosklar (siyahı, yarat/redaktə, xəritə, limitlər, qiymət profili, kağız kapasiteti, cihaz tab-ı)
- Mobile: Fayllarım (upload, HEIC → JPEG, siyahı, backend səhifə şəkilləri ilə baxış, sil)

**Definition of Done**
- MVP format siyahısının hər qrupundan fayl yüklənir, səhifə sayı və preview düzgündür; söndürülmüş format qəbul edilmir; limitlər (default + kiosk override) tətbiq olunur
- Storage-da obyektlər şifrəlidir (birbaşa MinIO-dan endirilən fayl oxunmur), backend vasitəsilə düzgün açılır
- 9.8 axını: MinIO → Scality migrasiyası worker restart olunsa da tamamlanır, Primary avtomatik dəyişir; mirror rejimində Primary söndürüləndə oxumalar Secondary-dən davam edir
- Kioskun qiymət profili dəyişəndə quote nəticəsi dəyişir, həcm endirimi pillələri düzgün tətbiq olunur

#### Addım 3 — Kiosk: printer, çap axını, queue, lockdown, kağız sayğacı
**İşlər**
- Printer aşkarlama (Windows + Linux + Fake), `PUT kiosks/me/printers`, `GET kiosks/me/config`, `NotConfigured`, `PrinterConfigChanged`, capability override, lokal konfiqurasiya keşi (Bölmə 7.7); `kiosk_commands` + `RefreshPrinters`, `TestPrint`; `/hubs/admin`
- PrintJob domeni + status maşını, PrintJobs API, queue (Redis sorted set `queue:{kioskId}`), `KioskHub` + Redis backplane
- Kiosk: USB aşkarlama, FlashBrowser, FilePreview, FileEditor (PDF səhifələri, şəkil, TXT), PrintOptions (profil qiyməti, kağız yoxlaması), `IPrinterFormatSupport`, native və ya PDF çap qaydası, "USB" badge + info, şifrəli spool, çap icrası, queue paneli, idle reset, CancelJob, NotConfigured / Offline ekranları
- Kiosk lockdown: provisioning skriptlərinin tam versiyası (Bölmə 7.12 — Windows: Shell Launcher, Keyboard Filter, bildiriş/yeniləmə siyasətləri; Linux: cage, logind, polkit, unattended-upgrades), `IKioskLockdown`, gizli admin çıxışı, `docs/KIOSK_SETUP.md`, `docs/KIOSK_PROVISIONING.md`
- Agent: servis, watchdog (crash loop → təhlükəsiz rejim), job jurnalı və restart-dan bərpa axını, spool açarının qorunması, `print_attempts` (printer sayğacı ilə "çap olubmu?" yoxlaması), `Degraded` / `Offline` rejimləri və konfiqurasiya keşinin TTL-i
- Kağız sayğacı: `PrintedSheets` hesabatı, sayğac azalması, `Paper.BlockWhenInsufficient` yoxlaması, admin paneldən doldurma (Operator paneli Addım 7-də)
- Kiosk analitika hadisələri (Bölmə 2.17) toplama və göndərmə
- Admin web: Kiosk detalı — Printerlər (siyahı, yenilə, default et, override, test çap), Queue (canlı, ləğv), Job tarixçəsi, Sərf (sayğac, əl ilə doldurma); Print job-lar səhifəsi; Dashboard (kiosk statusları, NotConfigured)
- Ödəniş hələ yoxdur: Development-də `dev` bayrağı ilə job birbaşa `Queued` edilə bilir

**Definition of Done**
- 9.0 axını (ödənişsiz hissə) işləyir: yeni kioskun printerləri paneldə görünür, default seçilir, test səhifəsi çap olunur, kiosk `Online` olur
- Flash-dan hər format qrupundan fayl açılır, redaktə olunur, qiymət kioskun profilinə görə hesablanır, real printerə (Windows: "Microsoft Print to PDF", Linux: `cups-pdf`) və ya Fake printerə çap olunur; hər iki OS-də
- Kağız sayğacı düzgün azalır; kağız çatmayanda sifariş bloklanır
- Provisioning-dən sonra ekranda NeoPrint-dən başqa heç nə görünmür (OS bildirişləri, yeniləmə pəncərələri, jestlər, autoplay yoxdur); kiosk tətbiqindən OS-ə çıxmaq həm Windows, həm Linux-da mümkün deyil (admin PIN istisna); skript ikinci dəfə işə salınanda heç nəyi pozmur
- Çap zamanı elektrik kəsilməsi / reboot simulyasiyası: kiosk özü qalxır, yenidən qeydiyyat tələb olunmur, `Printing` job düzgün bərpa olunur (ikiqat çap yoxdur), `Queued` job-lar davam edir
- Queue paneli real-time, ləğv işləyir; "USB" badge bütün lazımi ekranlarda görünür

#### Addım 4 — Ödəniş, refund, wallet, fiskal çek, qəbz, promokod
**İşlər**
- Payment domeni (ledger, `Money`, snapshot), intent-lər, `IBankQrProvider` + `ICardPaymentProvider` mock-ları (`RefundAsync` daxil, uğursuzluq simulyasiyası), callback, dev endpoint-ləri, `PaymentExpirationJob` (`Payment.TimeoutMinutes`)
- `refunds` modeli (`docs/STATE_MACHINES.md` üzrə), istiqamət matrisi, webhook imza + `ProviderEventId` dublikat qorunması, `PaymentReconciliationJob`, `RefundConsumer`, `AutoRefundOnKioskErrorConsumer`, `RefundStatusPollingJob`, voucher fallback, ödəniş timeline-ı
- Wallet (balans, bonus, top-up, geri qaytarılmama qaydası), admin adjust
- `IFiscalProvider` + `MockFiscalProvider`, `fiscal_receipts`, `FiscalizationConsumer`, satış / qaytarma / top-up çekləri, kiosk və onlayn terminal ID-ləri
- Qəbz: QuestPDF şablonu (fiskal məlumat + QR), nömrələmə, şifrəli storage, email (MailKit), fiskal çek gecikəndə qəbz yenilənməsi
- Promokod mühərriki (kiosk/profil məhdudiyyəti)
- `UserHub`
- Kiosk: PaymentMethod, BankQrPayment, MobileQrPayment, Receipt (fiskal QR, email), CancelJob, RefundInfo, mobil ödənişdən sonra `flash-upload` + retry
- Mobile: QR scanner (camera + ML Kit barcode), payment ekranları, Wallet ("geri qaytarılmır" xəbərdarlığı), Receipts, voucher aktivləşdirmə, promokod sahəsi
- Admin web: Ödənişlər (filtrlər, detal, timeline, fiskal çek, "Job-u ləğv et və qaytar", istiqamət seçimi), Refund-lar, Fiskal çeklər, Qəbzlər, Voucher-lər, Promokodlar, İstifadəçilər (detal, wallet adjust, blok), Dashboard gəlir kartı və qrafik, gəlir hesabatı

**Definition of Done**
- 9.1, 9.2, 9.10 axınları simulate endpoint-ləri ilə tam işləyir; flash faylı mobil tətbiqlə ödənəndə fayl, job və qəbz hesabda görünür, bank QR ilə ödənəndə heç nə saxlanılmır
- Hər satış və refund üçün fiskal çek yaranır; fiskal uğursuzluq retry → incident → paneldən təkrarlama işləyir
- Kioskda bank QR ilə ödənilmiş job xəta ilə dayananda məbləğ avtomatik karta qaytarılır, ekranda refund məlumatı; mock refund uğursuz olanda retry → "refund uğursuz" → admin Wallet-ə və ya voucher-ə yönəldir
- Admin account-lu kart ödənişini Wallet-ə və ya karta, anonimi karta qaytarır; Wallet balansını karta çıxarmaq heç bir yoldan mümkün deyil
- `Payment.TimeoutMinutes` paneldən dəyişəndə kiosk geri sayımı və expire vaxtı yeni dəyərə uyğundur

#### Addım 5 — Mobil funksiyalar və məxfilik
**İşlər**
- Kiosk axtarışı (mətn + geo `2dsphere`), flutter_map siyahı + xəritə + detal (qiymətlər, kağız xəbərdarlığı)
- Sessions (Redis TTL), `SessionOpened/Closed`, mobil sessiya ilə çap (9.3)
- Şəxsi QR (HMAC, rotasiya, backend PNG), `pending-for-user`, kioskda QR skaner input servisi + UserQrFiles ekranı (9.4)
- Schedules API + `ScheduledPrintDueJob` + `ScheduledPrintAutoCancelJob`, mobil planlama ekranları (9.5)
- FCM HTTP v1 öz klient (`LoggingPushSender` fallback), FCM token qeydiyyatı, notification siyahısı, tətbiq daxili banner, deep link-lər
- Məxfilik: fayl avtomatik silinmə seçimləri + `FileAutoDeleteJob`, "Məlumatlarımı yüklə" (`DataExportJob`), "Hesabımı sil" (`AccountDeletionJob`, bütün provayderlərdən silinmə, anonimləşdirmə)
- Mobil feature flag inteqrasiyası (bölmələrin gizlədilməsi)

**Definition of Done**
- 9.3, 9.4, 9.5 axınları mobil ↔ backend ↔ kiosk arasında işləyir
- Planlanmış çap vaxtında push göndərir, "Çapa ver" / "Vaxtı dəyiş" / ləğv (balansa refund) işləyir
- "Çapdan sonra sil" seçilmiş fayl çapdan sonra bütün provayderlərdən silinir; hesab silindikdən sonra istifadəçinin heç bir faylı storage-da qalmır, maliyyə qeydləri anonim qalır
- Söndürülən feature flag mobil UI-da və API-də bölməni bağlayır

#### Addım 6 — Skan, tərtibat, paylaşım, referral
**İşlər**
- Mobil öz sənəd skanı platform channel-ı (Android ML Kit Document Scanner, iOS VisionKit), filtrlər, preset-lər, real ölçü, həssaslıq, ilk istifadə razılığı
- `NeoPrint.Layout` (PDFsharp) renderer + `files/scan`, `files/layout`, `files/{id}/layout`; mobil `layout_editor` (CustomPainter, jestlər, snap, şablonlar); kioskda LayoutEditor (şəkillər üçün); real ölçü çap rejimi (Windows + Linux)
- Fayl paylaşımı (PublicId, QR), tarixçə, həssas fayl xəbərdarlığı
- Referral (kod, link, QR, `ReferralRewardConsumer`, fırıldaq yoxlaması), mobil referral ekranı, register-də referral
- Admin web: Referral statistikası, istifadəçi detalında referral-lar və fayl metadata-sı

**Definition of Done**
- 9.7 axını: vəsiqə ön + arxa bir A4-də yerləşdirilib çap olunur, çapda kart ölçüsü real (85.6 × 53.98 mm ± 1 mm); çapdan sonra həssas fayllar avtomatik silinir
- Tərtibat sonradan açılıb dəyişdirilir və yenidən çap olunur; mobil və kiosk eyni PDF nəticəsini verir
- Paylaşılan fayl alan istifadəçidə görünür, tarixçə düzgündür
- Dəvət olunanın ilk ödənişli çapından sonra dəvət edənə bonus yazılır, bir job-da maksimum limit tətbiq olunur

#### Addım 7 — Əməliyyat: telemetriya, sərf, servis, operator paneli; pilot hazırlığı və buraxılış
**İşlər**
- Kiosk telemetriyası (Bölmə 7.9, Windows + Linux `ISystemMetrics`), printer detalları və telemetriyası (Bölmə 7.10: IPP, WMI, SNMP), time-series kolleksiyalar, `TelemetryAlertEvaluator`
- Uzaqdan əmrlər: `Pause`, `Resume`, `RestartApp`, `Reboot` (Bölmə 7.8); incident API, `KioskHeartbeatMonitor`, `IncidentNotificationConsumer` (panel + email + Telegram), `AffectedUsersNotificationConsumer`; OutOfService axını
- Sərf modulu: `supply_alert_rules` (admin CRUD, qlobal + kiosk), `SupplyEvaluator`, `SupplyForecastJob`, `service_tasks` (avtomatik yaratma, təyinat, eskalasiya), anbar (`inventory_*`, low stock)
- **Operator paneli** (`/operator`, PWA): Mənim kiosklarım, kiosk qısa detalı, Kağız doldur, Toner dəyişdirildi, Tapşırıqlarım, Incident-lər, Anbar
- Admin web: Kiosk detalı — Cari vəziyyət (canlı + qrafiklər), Sərf (proqnoz, qaydalar), Servis tapşırıqları, Incident-lər, Əmrlər; Printer detalı; Printerlər (ümumi); Xəbərdarlıq qaydaları; Servis tapşırıqları; Anbar; Incident-lər (səs + brauzer bildirişi); Audit log; Pilot hesabatı (`AnalyticsRollupJob`)
- **Yeniləmə sistemi:** paket imzalama (CI), `releases`, rollout-lar, Agent updater (endir, yoxla, boş anı gözlə, keçid, sağlamlıq yoxlaması, rollback), Agent özünü yeniləmə, provisioning yeniləməsi; uzaqdan `UpdateNow`, `RollbackVersion`, `UploadLogs`, `ReapplyProvisioning`, `RestartAgent`; enrollment token-lər; golden image skriptləri
- Admin web: Buraxılışlar, Cihazlar → Enrollment token-lər, kiosk detalında versiya / yeniləmə statusu, log yükləmə
- `FileCleanupJob`, DLQ monitorinqi, wallet ledger uzlaşdırması, ödəniş reconciliation-ı
- Integration testlər (Bölmə 11), CI-də hamısı
- Buraxılış: backend Docker image, desktop (UI + Agent) `dotnet publish -r win-x64 --self-contained` və `-r linux-x64 --self-contained` → imzalı buraxılış paketləri + provisioning skriptləri, Flutter `apk` / `appbundle` və iOS build, admin web Docker image (nginx), `docs/API.md`, `docs/ADMIN_GUIDE.md`, `docs/OPERATOR_GUIDE.md`, `docs/PILOT.md`, `docs/legal/*` qaralamaları, README

**Definition of Done**
- Printer söndürüləndə: job `Interrupted`, kiosk dayandı ekranı, panelə səsli bildiriş, anonim bank ödənişləri avtomatik qaytarılır, qeydiyyatlılara refund təklifi; printer qoşulanda iş davam edir
- 9.9 axını: kağız həddləri keçiləndə Warning/Critical alert-lər admin və operator panelində görünür, tapşırıq avtomatik yaranır və operatora təyin olunur, operator telefondan doldurmanı qeyd edir, sayğac və anbar yenilənir, alert bağlanır; proqnoz göstərilir
- Yeni versiya admin paneldən pilot kanalına buraxılır → kiosk boş anda yenilənir, **yenidən qeydiyyat olmadan** işə davam edir; qəsdən xətalı versiya buraxılanda avtomatik rollback olur və incident yaranır; enrollment token ilə yeni kiosk heç kim ekrana baxmadan aktivləşir
- Kiosk detalında cari vəziyyət canlı yenilənir; printer detalında model, seriya, toner səviyyələri, sayğaclar və spooler görünür
- Pilot hesabatı format paylanmasını, funnel itkilərini və ödəniş xətalarını göstərir
- Bütün testlər, lisenziya yoxlaması və CI yaşıl, buraxılış artefaktları yaradılır → **v1.0.0**

### PİLOT (Addım 7-dən sonra, kod addımı deyil)
- 1 kiosk, 1 məkan, 2–3 həftə (`docs/PILOT.md`).
- Gündəlik: pilot hesabatı, incident-lər, fiskal və refund statusları, kağız/toner sərfi.
- Pilotun sonunda: format siyahısı, feature flag-lar, qiymət profili, limitlər üzrə qərarlar; Faza 2 prioritetləri təsdiqlənir.

### FAZA 2 — Pilotdan sonra (istifadəçi göstərişi ilə başlanır)

#### Addım 8 — Etibarlılıq və təhlükəsizlik
- **Uzaqdan texniki dəstək kanalı (fövqəladə hallar üçün):** WireGuard VPN (açıq mənbə, pulsuz) + Linux-da SSH, Windows-da OpenSSH Server; yalnız VPN daxilindən, yalnız açarla, admin paneldən kiosk üzrə açılıb-bağlanır, hər sessiya audit olunur. Gündəlik idarəetmə bunsuz, Agent vasitəsilə aparılır.
- **Mərhələli rollout-un avtomatlaşdırılması:** pilot kanalında N saat problemsiz işlədikdən sonra `stable`-a avtomatik keçid; xəta faizi həddi keçəndə rollout-un avtomatik dayanması.
- **Virus yoxlaması:** backend — ClamAV (`clamd` ayrıca konteyner, INSTREAM protokolu öz klientimizlə); Linux kiosk — lokal `clamd`; Windows kiosk — daxili Microsoft Defender (`MpCmdRun.exe -Scan -ScanType 3 -File`). Yoluxmuş fayl rədd edilir, incident + analitika hadisəsi.
- **İzolyasiya olunmuş konvertasiya:** backend-də ayrıca `converter` servisi (şəbəkəsiz konteyner, read-only root FS, CPU/RAM limiti, vaxt limiti); kioskda ayrıca məhdud istifadəçi ilə proses (Linux: `systemd-run` + cgroups; Windows: Job Object limitləri).
- **Monitorinq:** OpenTelemetry (.NET SDK — Apache-2.0, CNCF) → OpenTelemetry Collector → Prometheus + Grafana (hazır dashboard-lar: API gecikmə/xəta, RabbitMQ növbələri, DLQ, fiskal/refund uğursuzluqları, storage sync); crash / xəta izləmə: Sentry SDK-ları (MIT) → self-hosted **GlitchTip** (MIT, Sentry ilə uyğun) — backend, kiosk, mobil, admin.
- **MongoDB backup (admin paneldən idarə):**
  - "Backup" səhifəsi: cədvəl (gündəlik vaxt / həftəlik), saxlama müddəti (son N backup), **hədəf yol** — lokal / şəbəkə qovluğu yolu və ya storage bucket (MinIO / Scality), bağlantı testi.
  - `mongodump --archive --gzip` → OpenBao-dan gələn `NEOPRINT_BACKUP_KEY` ilə AES-GCM şifrələnir → hədəfə yazılır; checksum.
  - "İndi backup et", backup siyahısı (ölçü, müddət, status), uğursuzluqda incident; aylıq avtomatik **bərpa testi** ayrıca test DB-yə; bərpa təlimatı `docs/BACKUP_RESTORE.md`.
  - Yeni icazələr: `backup.manage`, `monitoring.view`, `remote.support`.

#### Addım 9 — Office redaktəsi v2 və genişləndirilmiş formatlar
- **Office mətn redaktəsi — Collabora Online CODE:** compose-a `collabora/code` konteyneri; backend WOPI host (`/wopi/files/{id}` — CheckFileInfo, GetFile, PutFile, Lock/Unlock/RefreshLock; lock-lar Redis-də), `POST files/{id}/edit-session` → Collabora URL + 30 dəq-lik WOPI access token. Kiosk: `CefGlue.Avalonia` (MIT) WebView + ekran klaviaturası; mobil: `webview_flutter` (rəsmi). Flash faylları üçün müvəqqəti `POST kiosks/me/temp-files` (şifrəli, TTL 1 saat). Saxlama orijinal formatda yeni versiya kimi, sonra avtomatik PDF-ə çevrilmə. Collabora əlçatmaz olduqda "Redaktə et" deaktiv olur. Feature flag: `office_editor`.
- **Formatlar:** TIFF, SVG, kioskda HEIC.
- Hər yeni asılılıq Bölmə 2.0 siyasəti ilə yoxlanılır. Collabora CODE-un pulsuz edition limitləri (eyni anda istifadəçi / sənəd sayı) pilot yükünə görə yoxlanılır və `docs/DECISIONS.md`-də qeyd olunur.

### FAZA 3 — Biznes genişlənmələri (MVP və Faza 2-dən sonra)

#### Addım 10 — Biznes modulları
1. **Abunə paketləri** — məs. "Tələbə: aylıq 100 səhifə". Paket kataloqu (admin), aylıq səhifə limiti, qalıq, müddət, avtomatik yenilənmə (kartla), ödəniş zamanı paket səhifələrinin prioriteti, fiskal çek.
2. **Korporativ hesablar** — universitet / şirkət hesabı, təşkilat balansı, işçi/tələbə dəvəti, işçi başına aylıq limit, kiosk məhdudiyyəti, aylıq hesab-faktura (PDF), təşkilat admin rolu.
3. **Kiosk ekranında reklam** — boş vaxtda (idle) göstərilən kampaniyalar (şəkil / video), kiosk və vaxt hədəfləmə, göstərim statistikası, admin panelindən idarə.
4. **Şikayət və keyfiyyət refund-u** — mobil tətbiqdən çap job-una şikayət (səbəb + şəkil), admin baxışı, təsdiqlə → refund (Bölmə 2.5 qaydaları).
5. **Gözləmə vaxtı proqnozu** — queue-dakı vərəq sayı və printerin orta sürəti əsasında hər kiosk üçün "növbə ~N dəq" (mobil xəritə və kiosk detalında).
6. **Printer sayğacı ilə yoxlama** — printer lifetime sayğacının artımı ilə sistemdəki `PrintedSides` müqayisəsi; uyğunsuzluq həddi keçəndə incident (sui-istifadə və ya xəta).
7. **Hazır şablonlar** — ərizə formaları, CV, müqavilə nümunələri; sahələri kioskda və mobil tətbiqdə doldurulub PDF yaradılır (`NeoPrint.Layout` / PDFsharp), admin şablon kataloqunu idarə edir.

---

## 13. Git qaydaları

**Başlanğıc (Addım 1-dən əvvəl):**
```bash
git init -b main
# remote yoxdursa və gh CLI autentifikasiya olunubsa:
gh repo create NeoPrint --private --source=. --remote=origin
```
`origin` artıq varsa, mövcud remote istifadə olunur.

**Commit qaydası:** Conventional Commits — `feat(backend): ...`, `feat(kiosk): ...`, `feat(mobile): ...`, `feat(admin): ...`, `feat(operator): ...`, `fix: ...`, `test: ...`, `docs: ...`, `chore: ...`. Addım daxilində məntiqi hissələrə bölünmüş kiçik commit-lər. `.env*`, `.openbao/` və real secret-lər heç vaxt commit olunmur.

**Hər addımın sonunda:**
```bash
git add .
git commit -m "chore: complete step N - <addım adı>"
git tag <tag>
git push origin main --tags
```

| Addım | Tag |
|---|---|
| 1 | `v0.1.0` |
| 2 | `v0.2.0` |
| 3 | `v0.3.0` |
| 4 | `v0.4.0` |
| 5 | `v0.5.0` |
| 6 | `v0.6.0` |
| 7 | `v1.0.0` (MVP) |
| 8 | `v1.1.0` |
| 9 | `v1.2.0` |
| 10 | `v2.0.0` |

---

## 14. Hüquqi məsələlər (istifadəçi hüquqşünasla həll edir — Claude gözləmir)
| Mövzu | Sistemdə hazırlanan | Dəqiqləşdiriləcək |
|---|---|---|
| Fiskal çek / onlayn kassa | `IFiscalProvider`, mock, terminal ID-ləri, satış/qaytarma/top-up çekləri | Tələbin tətbiq dairəsi, provayder seçimi, kiosk başına kassa qeydiyyatı, çek məzmunu |
| Wallet artırılmasının fiskal/vergi tərəfi | `Fiscal.TopUpReceiptMode` (`OnTopUp` / `OnService`) | Hansı rejimin düzgün olduğu, ƏDV |
| Wallet-in geri qaytarılmaması | Şərtlərdə bənd, UI xəbərdarlıqları, texniki qadağa | Bəndin hüquqi formulası |
| Bonus səhifələrin fiskal əksi | 0 AZN, çekə düşmür | Təsdiq |
| Fərdi məlumatlar (şəxsi sənəd skanları) | Şifrələmə, razılıq, avtomatik silinmə, hesab silmə, export, admin çıxışının olmaması | Operator qeydiyyatı, məxfilik siyasətinin son mətni, saxlama müddətləri |
| Maliyyə qeydlərinin saxlanma müddəti | Anonimləşdirilmiş saxlama | Müddət |
| **Qaytarma (refund) siyasəti** | `docs/legal/REFUND_POLICY.az.md` qaralaması: printer xətasında avtomatik qaytarma və müddəti; texniki fasilə / elektrik kəsilməsində müraciət əsasında araşdırma və cavab müddəti; müraciət kanalları (QR, mobil tətbiq, qaynar xətt); balansın geri qaytarılmaması; anonim ödənişlərdə voucher halı. Texniki tərəf: Bölmə 2.5 (A/B ssenariləri), `Job.SuspectedAfterMinutes`, `Job.SuspectedAutoResolveHours` | Mətnin son formulası; **istehlakçı hüquqları qanunvericiliyinin tələb etdiyi maksimum cavab və qaytarma müddətləri** (sənəddəki 3 iş günü / 10 iş günü yalnız təklifdir); nəticəsi naməlum çap üçün sübut yükünün kimdə olması; voucher-in qanuni pul əvəzedicisi sayılıb-sayılmaması |

---

## 15. Scope xaricində
- NFC ödəniş (gələcək: `INfcPaymentProvider` interface yeri ayrılır, implementasiya yoxdur)
- Real bank və fiskal provayder inteqrasiyası (yalnız interface + Mock; real adapter provayder seçiləndən sonra)
- iOS App Store / Google Play publikasiyası (yalnız build)
- Faza 2 və Faza 3 addımları istifadəçi ayrıca göstəriş verənə qədər
