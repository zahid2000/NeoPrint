# T-164 — Plan ↔ kod auditi: Addım 1–4 (2026-10-04)

Mənbə: `docs/NeoPrint-PLAN.md`-in "Addım 1–4" bölmələri (İşlər + Definition of Done).

**Statuslar:**
- **Edilib** — kod, test və ya real stack sübutu var.
- **Qismən** — bir hissənin sübutu yoxdur.
- **Edilməyib** — sübut tapılmayıb.
- **Təxirə salınıb** — `TASKS.md`-də əsaslandırılmış qərar var.

**Real stack:** compose + Linux kiosk konteyneri `neoprint-kshot` (KSK-9002, fake printer) + Chrome (Playwright). Yeni ekran görüntüləri `docs/screenshots/audit-t164/`-dədir.

**Test dəstləri (2026-10-04, lokal):**
- backend: 80 + 480 + 45 + 465 (2 skip, T-088 / T-134);
- kiosk: 250/250;
- mobil: 162/162.

Admin (Karma) lokalda əvvəl asılı qalırdı, ona görə CI-yə istinad olunur: `c1f6ff0`, 7/7 job yaşıl.

## Addım 1 — İşlər

| Bənd | Status | Sübut |
|---|---|---|
| Repo strukturu, `.gitignore` (`.env*`, `.openbao/`, IDE…), `.env.openbao-seed.example`, README, DECISIONS, THIRD_PARTY | Edilib | `.gitignore`; `git ls-files`-da yalnız `.env.openbao-seed.example` var, real seed və `.openbao/` izlənmir |
| OpenBao: compose, `openbao/` (config, policy-lər, `init.sh`), öz klient + `OpenBaoConfigurationProvider`, `OPENBAO.md` | Edilib | `openbao/`, `Infrastructure/SecretStore/*`; real: `/health` → `openbao: Healthy` |
| `docker-compose.yml` + backend `Dockerfile` (LibreOffice, Ghostscript, fontlar, PDFium) | Edilib | `backend/Dockerfile` (LibreOffice daxil); eyni image şəbəkəsiz `converter` konteyneri kimi də işləyir (ADR-0005); real: `converter: Healthy`; PDFium SHA-256 yoxlaması (T-100) |
| `tools/license-check` + CI | Edilib | `.github/workflows/ci.yml` (7 job), CI `c1f6ff0` yaşıl |
| Solution, layer-lər, MediatR 12 behavior-ları (validasiya, logging, transaction), ProblemDetails, health, Swagger (yalnız Dev), OpenBao secret validasiyası | Edilib | `Application/Behaviors/PipelineBehaviors.cs`, `ProblemDetailsExceptionHandler.cs`, `RequiredSecrets.cs`; real: `/health`, `/health/ready`, `/swagger` → 200 (Development) |
| `NeoPrint.Crypto` (envelope, KEK rotasiyası) + testlər; `Money` + mərkəzi yuvarlaqlaşdırma | Edilib | `shared/NeoPrint.Crypto`; `An_object_written_with_the_old_key_is_still_readable_after_a_rotation`, `Rewrapping_moves_an_object_onto_the_new_key…`; `Domain/Common/Money.cs` |
| Idempotency, `provider_events`, CorrelationId + JSON log, `state_transitions`, `DomainException`, rate limiting | Edilib | `IdempotencyFilter`, `CorrelationIdMiddleware`, `MongoContext` (indekslər), `AddRateLimiter`; ADR-0001…0003 |
| Mongo, MassTransit 8 (RabbitMQ, Mongo outbox / inbox), **Hangfire (Mongo storage, dashboard)** | **Qismən** | Hangfire işləyir (`AddHangfire` + `UseMongoStorage`), amma **dashboard yoxdur**: kodda `UseHangfireDashboard` / `MapHangfireDashboard` yoxdur və heç vaxt olmayıb (`git log -S`). Real: `/hangfire` və `/api/admin/hangfire` → 404. Plan 878-ə görə `/api/admin/hangfire`, yalnız SuperAdmin. `DEV_ENVIRONMENT.md` səhv URL göstərir → **T-167** |
| Seed: SuperAdmin + rollar, qiymət profili, `admin_settings`, supply alert, flag-lar, hüquqi qaralamalar; dev-də 2 kiosk + enrollment token | Edilib | `DatabaseSeeder.cs`, `DevelopmentSeeder.cs`, `LegalDocumentDrafts.cs` (`LegalDocumentSeedTests`) |
| Auth: istifadəçi, razılıqlar, cihaz qeydiyyatı / aktivləşdirmə, admin auth (cookie refresh, ilk girişdə şifrə), dinamik RBAC (kiosk əhatəsi), audit log | Edilib | `DeviceActivationTests` (27), `RbacTests` (18). Audit middleware ilə yox, handler-lərdə `IAuditWriter` ilə yazılır (`DECISIONS.md` 2026-09-24). Bir boşluq Addım 3-də tapıldı → T-165 |
| Feature flag mühərriki (`GET features`, `[RequireFeature]`) | Edilib | `FeaturesController`, `RequireFeatureFilter`; `FeatureFlagTests` |
| Desktop: Avalonia, platformalar, MainWindow, fullscreen / Topmost, l10n, ekran klaviaturası, universal build (CNG/TPM, Linux açar faylı), Activation, heartbeat, Agent skeleti, provisioning v1 | Edilib | `WindowsPlatform.cs` (`Microsoft Platform Crypto Provider`), `DeviceEnrollmentService.cs`, `ActivationView.axaml`, `NeoPrint.Kiosk.Agent`; ekran klaviaturası T-072 |
| Mobil: Riverpod, go_router, Dio (refresh), öz secure storage, öz SignalR klienti, tema, l10n, auth / razılıq, bottom nav | Edilib | `pubspec.yaml`, `core/storage/secure_storage.dart`, `core/realtime/user_hub.dart`, `features/consents/` |
| Admin: Angular, Material, i18n, icazəyə görə menyu, login, guard, Admin istifadəçiləri, Rollar, Cihazlar (sehrbaz, ayır), Tənzimləmələr, Flag-lar, Hüquqi sənədlər; Dockerfile + compose | Edilib | `admin/src/app/app.routes.ts` (hamısı var); `docs/screenshots/step-2/admin-03…11` |

**Addım 1 TASKS-da:** reyestr Addım 2-də yaradılıb (T-020), ona görə Addım 1 işlərinin heç biri üçün ayrıca tapşırıq yoxdur (yalnız T-001 — teq). Sübut yuxarıdakı kod və testlərdir.

## Addım 1 — DoD

| Bənd | Status | Real stack / screenshot |
|---|---|---|
| `docker compose up`: OpenBao init / unseal, secret-lər, servislər, `/health` OK (OpenBao daxil); API secret-i env-dən almır; Production-da məcburi açar yoxdursa API başlamır | **Qismən** | Real: `/health` → bütün 6 yoxlama Healthy (openbao daxil); API konteynerinin env-ində yalnız `BAO_*` fayl yolları var. "Production-da başlamır" hissəsini **yalnız test** göstərir (`OpenBaoConfigurationProviderTests`). Real yoxlama üçün lokal OpenBao-da bir açarı müvəqqəti boşaltmaq lazımdır; bu əməliyyat təhlükəsizlik qaydası ilə bloklandı, layihə sahibinin qərarı gözlənilir → **T-169** |
| Mobil: qeydiyyat → razılıq → email kodu (Mailpit) → login | Edilib | T-027 (Chrome); `step-2/mobile-01…04`; bu gün push / növbə yoxlamalarında 3 yeni hesab eyni yolla açıldı |
| Eyni build Windows və Linux-da; biri yeni kiosk, digəri mövcud kioska; öz konfiqurasiyası, footer QR + kod, heartbeat; "Cihazı ayır" → Activation | **Qismən** | Linux, real (bu gün): "Cihazı ayır" → Activation ekranı + kod (`kiosk-device-01`) → **mövcud** KSK-9002-yə aktivləşdirmə → footer QR + kod, `Online` (`kiosk-device-02`). Windows: layihə sahibi kioskunu enrollment token ilə işə salıb (T-095 / T-096). Windows-da pairing kodu ilə aktivləşdirmə və "Cihazı ayır" sübutsuzdur → Windows VM yoxlaması (T-093, Addım 7) |
| SuperAdmin yeni rol yaradır; həmin rolun istifadəçisi yalnız icazəli səhifələri görür, qalan API-lər 403; icazə dəyişikliyi yenidən login olmadan | Edilib | Real (bu gün), yeni rol + yeni admin: menyuda yalnız "İdarə paneli" və "Kiosklar" (`admin-rbac-01`); `payments` / `roles` / `admins` → 403; birbaşa `/payments` URL-i ana səhifəyə qaytarır. `payments.view` verildi → menyu **canlı** yeniləndi (`admin-rbac-02`), səhifə 200 ilə açıldı (`admin-rbac-03`). Qeyd: əlavə olunan icazə token yenilənəndə işə düşür (panel bunu `PermissionsChanged`-də özü edir); geri alınan icazə isə dərhal bağlanır (`RbacTests`) |
| Lisenziya yoxlaması və CI yaşıl | Edilib | CI `c1f6ff0` 7/7; lisenziya 891/891 (Addım 5 sonu) |

## Addım 2 — T-021 (a–j) yenilənmiş cədvəli

| Bənd | 2026-09-28 | İndi (2026-10-04) | Sübut |
|---|---|---|---|
| a. Mobil qeydiyyatda şəbəkə xətası | Edilib | Edilib | `0e8fab3`; bu gün 3 yeni hesab real API-yə qarşı qeydiyyatdan keçdi |
| b. `DEV_ENVIRONMENT.md` (URL, hesab, əmr) | Edilib | **Qismən** | Sənəd var və genişlənib (7a–7j). **Hangfire dashboard sətri səhvdir:** `http://localhost:5080/hangfire` → 404 (T-167) |
| c. Refund sənədləri (A / B ssenarisi) | Edilib | Edilib | `7307ad6`; Addım 4-də kodda da var (T-103, T-104; `step-4/admin-01…04`) |
| d. `/hubs/admin` və canlı proqres | Edilib | Edilib | Bu gün admin panelində "Canlı" göstəricisi, növbə səhifəsi yenilənmədən boşaldı (`admin-queue-03`) |
| e. KEK rotasiyası, `KeyRewrapJob` | Edilib | Edilib | `An_object_written_with_the_old_key_is_still_readable_after_a_rotation` və b.; OpenBao-da `k1` + `k2`, cari `k2` |
| f. Konvertasiya: şəbəkəsiz, CPU / RAM limiti | Edilib | Edilib | ADR-0005; `converter` konteyneri Healthy; `ConverterIsolationTests` |
| g. Qiymət snapshot-u | Edilib | Edilib | `PriceSnapshotTests`; job səviyyəsində T-047 |
| h. `dart format` + CI; admin refresh 401 | Edilib | Edilib | `ci.yml` (`dart format --set-exit-if-changed`); `No_cookie_at_all_is_a_401`, `A_token_that_was_already_used_is_a_401` |
| i. RustFS | Edilib | Edilib | `rustfs` və `rustfs-secondary` konteynerləri Healthy |
| j. Docker təmiz qaldırma, `dev-doctor` | Edilib | Edilib, bir yeni tapıntı ilə | `tools/dev-doctor`. **Yeni:** `api` konteyneri tək yenidən yaradılanda admin nginx köhnə IP-yə gedir → 502 → panel "Serverdə xəta baş verdi" göstərir (T-166) |

## Addım 3 — İşlər

| Bənd | Status | Sübut |
|---|---|---|
| Printer aşkarlama (Windows, Linux, Fake), `kiosks/me/printers`, `NotConfigured`, `PrinterConfigChanged`, override, konfiq keşi, `RefreshPrinters` / `TestPrint`, `/hubs/admin` | Edilib (kod + fake), real printer **Təxirə salınıb** | T-064; `step-3/admin-02`, `kiosk-01`; real printer T-092 (Addım 7) |
| PrintJob domeni, API, Redis queue, `KioskHub` + backplane | Edilib | T-063; bu gün iki iş növbəyə düşdü və canlı göründü |
| Kiosk: USB, FlashBrowser, Preview, Editor (PDF, şəkil, TXT), PrintOptions, `IPrinterFormatSupport`, USB nişanı, şifrəli spool, idle reset, CancelJob, NotConfigured / Offline | Edilib; **Office faylları sübutsuz** | T-065, `step-3/kiosk-03…11`, `step-4/kiosk-03`; bu gün JPG (`kiosk-flash-01`). DOCX dev kiosk konteynerində açılmır, çünki orada LibreOffice yoxdur; kiosk aydın xəta göstərir (`kiosk-flash-02`) → **T-168** |
| Lockdown: provisioning (Windows + Linux), `IKioskLockdown`, gizli admin çıxışı, `KIOSK_SETUP.md`, `KIOSK_PROVISIONING.md` | Edilib (kod + sənəd), real cihaz **Təxirə salınıb** | `provision.ps1` (Shell Launcher, Keyboard Filter, bildiriş / yeniləmə / autoplay siyasətləri), `provision.sh` (cage, `unattended-upgrades`); T-093 / T-094 (Addım 7) |
| Agent: watchdog, təhlükəsiz rejim, jurnal və bərpa, spool açarı, `print_attempts`, Degraded / Offline | Edilib | T-066; `step-3/kiosk-14…15`; bu gün restart-dan sonra bərpa (aşağıda) |
| Kağız sayğacı, `Paper.BlockWhenInsufficient`, admin doldurma | Edilib | T-067; `step-3/kiosk-17`, `admin-07` |
| Kiosk analitika hadisələri (2.17) | Edilib | `KioskAnalytics`; real bazada `analytics_events` time-series kolleksiyasında 441 hadisə |
| Admin: kiosk detalı (Printerlər, Növbə, Sifarişlər, Sərf), Sifarişlər səhifəsi, Dashboard | Edilib | T-069; `step-3/admin-01…10`. Növbədən ləğv sifariş detalından olur: sətir → "Sifarişi ləğv et" |
| Dev `dev` bayrağı ilə ödənişsiz `Queued` | Edilib | `PrintJob.PaymentSkipped`; T-115-dən sonra kioskda söndürülüb |

## Addım 3 — DoD

| Bənd | Status | Real stack / screenshot |
|---|---|---|
| 9.0 (ödənişsiz): printerlər paneldə, default, test səhifəsi, `Online` | Edilib | `step-3/admin-02`, `kiosk-01`, `kiosk-02` |
| Flash-dan hər format qrupu açılır, redaktə, qiymət, real və ya fake printerə çap; hər iki OS | **Qismən** | Linux-da real: PDF, TXT, PNG, JPG. Office (DOCX / XLSX / PPTX) kioskda sübutsuzdur → T-168. Real printer → T-092. Windows: T-096 (layihə sahibi) |
| Kağız sayğacı azalır; çatmayanda sifariş bloklanır | Edilib | `step-3/kiosk-17` |
| Provisioning-dən sonra yalnız NeoPrint görünür; OS-ə çıxış yoxdur; skript ikinci dəfə pozmur | Təxirə salınıb | T-093 (Windows VM), T-094 (Linux cihaz), Addım 7 — pilotdan əvvəl |
| Elektrik kəsilməsi / reboot: kiosk özü qalxır, yenidən qeydiyyat yoxdur, `Printing` bərpası (ikiqat çap yox), `Queued` davam edir | **Qismən** | Real (bu gün): növbədə iki ödənilmiş iş (printerdə kağız yoxdur) → `docker restart` → kiosk qeydiyyatsız qalxdı ("This device is activated as kiosk KSK-9002"), iki iş də yerində (`kiosk-queue-02`). Kağız qayıdanda 708538 **bir dəfə** çap olundu (printer qovluğunda bir fayl). Çapın ortasında kəsilmə yalnız testlərlə və T-089 (real printer, Addım 7) ilə örtülür |
| Növbə paneli real-time, ləğv işləyir; USB nişanı lazımi ekranlarda | Edilib | Real (bu gün): admin Növbə tabında iki iş (`admin-queue-01`). 532320 admindən ləğv olundu, 0,20 ₼ balansa qayıtdı (`admin-queue-02`). Tab yenilənmədən boşaldı (`admin-queue-03`). USB nişanı `FlowView`-da bütün mərhələlərin üstündədir. **Tapıntı:** admin ləğvinin səbəbi saxlanmır və `audit_logs`-a yazılmır → T-165 |

## Addım 4 — İşlər

| Bənd | Status | Sübut |
|---|---|---|
| Payment domeni, intent-lər, `IBankQrProvider` / `ICardPaymentProvider` mock-ları, callback, dev endpoint-ləri, `PaymentExpirationJob` | Edilib | T-101; `PaymentFlowTests`; `step-4/kiosk-04` |
| `refunds`, istiqamət matrisi, webhook imzası, `ProviderEventId`, reconciliation, refund consumer-ləri, polling, voucher fallback, timeline | Edilib | T-103; `RefundTests`; `step-4/admin-07`, `admin-18…20` |
| Wallet (top-up, geri qaytarılmama), admin adjust | Edilib | T-102; `A_wallet_payment_goes_back_only_to_the_wallet_never_to_a_card`; `step-4/mobile-04` |
| Fiskal: provayder + mock, `fiscal_receipts`, consumer, satış / qaytarma / top-up, terminal ID-ləri | Edilib | T-105, T-129; `FiscalTests`; `step-4/admin-21…23` |
| Qəbz: QuestPDF, nömrələmə, şifrəli storage, email, fiskal gecikəndə yenilənmə | Edilib | T-106; `ReceiptConsumer`, `ReceiptFiscalConsumer`; `step-4/mobile-03`, `admin-10` |
| Promokod (kiosk / profil məhdudiyyəti) | Edilib | T-107, T-130; `PromoTests`; `step-4/kiosk-10…12`, `mobile-05…08` |
| `UserHub` | Edilib | `UserHub.cs`, `user_hub.dart` |
| Kiosk: ödəniş üsulları, bank QR, tətbiq QR-ı, qəbz (fiskal QR, email), RefundInfo, `flash-upload` + retry | Edilib | T-108; `step-4/kiosk-04…09` |
| Mobil: QR skaner (kamera + ML Kit), ödəniş, Wallet, Qəbzlər, voucher, promokod | Edilib; kamera əl ilə | T-109; `step-4/mobile-01…08`; real kamera — `MANUAL_TESTS.md` (Addım 4 və MT-07) |
| Admin: Ödənişlər, Refund-lar, Fiskal çeklər, Qəbzlər, Voucher-lər, Promokodlar, Müştərilər (adjust, blok), Dashboard pul kartı, gəlir hesabatı | Edilib | T-110, T-131, T-137, T-138; `step-4/admin-05…27` |

## Addım 4 — DoD

| Bənd | Status | Real stack / screenshot |
|---|---|---|
| 9.1, 9.2, 9.10; mobil ilə ödənilən flash faylı hesabda; bank QR ilə heç nə saxlanılmır | Edilib | `step-4/kiosk-04…09`, `mobile-01…03`; `The_flash_file_of_an_app_paid_order_joins_the_account_once`. Bank QR hissəsi bu gün real Mongo-da yoxlandı: 8 bank QR flash sifarişinin heç birində `UserId`, `FileId` və istifadəçiyə bağlı qəbz yoxdur |
| Hər satış / refund üçün fiskal çek; uğursuzluq → retry → incident → paneldən təkrar | Edilib | `step-4/admin-21…23` |
| Bank QR job xətası → avtomatik karta refund, ekranda məlumat; mock refund uğursuz → admin Wallet / voucher | Edilib | `step-4/kiosk-07`, `kiosk-09`, `admin-18…20` |
| Admin: hesablı kart ödənişini Wallet-ə və ya karta, anonimi karta; Wallet → kart heç bir yolla | Edilib | `step-4/admin-15…17`; `A_wallet_payment_goes_back_only_to_the_wallet_never_to_a_card` |
| `Payment.TimeoutMinutes` dəyişəndə kiosk geri sayımı | Edilib | `step-4/kiosk-04` (3 dəq → 2:53) |

## Ekranlar (plana görə)

Addım 1–4-ün plandakı bütün admin səhifələri (`app.routes.ts`), mobil ekranlar və kiosk ekranları (`Views/*`, `FlowView` mərhələləri) mövcuddur. Admin kiosk redaktorunda xəritə (leaflet) və qiymət kalkulyatoru var.

Plandan fərq yalnız birdir: kiosk Növbə tabında ləğv düyməsi sətirdə yox, sifariş detalındadır (iki toxunuş).

## Kiçik müşahidələr (kiosk)

- `docker restart`-dan sonra başlıqdakı "N telefon qoşulub" nişanı itir, halbuki sessiyalar serverdə hələ açıqdır (`kiosk-queue-02`).
- "Cihazı ayır"-dan sonra Activation ekranının başlığında köhnə kioskun adı ("Test kiosk — Linux") qalır (`kiosk-device-01`).

İkisi də T-170-dir.

## Açılan tapşırıqlar (layihə sahibinin təsdiqini gözləyir)

| ID | Tapşırıq | Təklif |
|---|---|---|
| T-165 | Admin sifarişi ləğv edəndə dialoqdakı səbəb backend-də atılır (`AdminCancelPrintJobCommand.Reason` istifadə olunmur), `audit_logs`-a heç nə yazılmır | Addım 6-dan əvvəl |
| T-166 | Admin nginx `proxy_pass http://api:8080` DNS-i bir dəfə həll edir. `api` konteyneri yenidən yaradılanda 502 qaytarır, panel "Serverdə xəta baş verdi" göstərir | Addım 6-dan əvvəl |
| T-167 | Hangfire dashboard yoxdur (plan Addım 1 + bölmə 8: `/api/admin/hangfire`, yalnız SuperAdmin). `DEV_ENVIRONMENT.md` səhv URL göstərir | Sənəd düzəlişi indi; dashboard Addım 6-dan əvvəl və ya Addım 7-də (layihə sahibinin seçimi) |
| T-168 | Office faylları (DOCX / XLSX / PPTX) kioskda real stack-də yoxlanmayıb: dev Linux kiosk konteynerində LibreOffice / Ghostscript yoxdur | Addım 6-dan əvvəl |
| T-169 | "Production-da məcburi açar yoxdursa API başlamır" yalnız testlə təsdiqlənib | Addım 7 (staging) və ya layihə sahibinin icazəsi ilə lokal OpenBao-da |
| T-170 | Kiosk: restart-dan sonra "telefon qoşulub" nişanı itir; Activation ekranında köhnə kiosk adı qalır | Addım 7 |
