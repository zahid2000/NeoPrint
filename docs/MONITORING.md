# NeoPrint — Monitorinq

Plan Bölmə 12, Addım 8 "Monitorinq" (T-477, T-478). Metrikaların siyahısı və log
qaydaları [`OBSERVABILITY.md`](OBSERVABILITY.md)-dədir; bu sənəd yığımın necə
qurulduğunu və necə istifadə olunduğunu izah edir.

## Necə işləyir

```
API (OpenTelemetry .NET SDK)
   │  OTLP/HTTP, 15 saniyədə bir
   ▼
OpenTelemetry Collector  ──►  Prometheus  ◄── RabbitMQ (rabbitmq_prometheus plagini)
                                  │
                                  ▼
                               Grafana  (hazır dashboard-lar)

API ──► Collector ──► Tempo ──► Grafana → Explore → Tempo   (trace-lər, T-479)
```

- **API** metrikaları özü göndərir (`Monitoring__OtlpEndpoint`). Bu dəyişən
  boşdursa monitorinq ümumiyyətlə qoşulmur. Kollektor işləmirsə, API
  göndərə bilmədiyi nöqtələri atır və işinə davam edir.
- **Kollektor** metrikaları Prometheus formatında açır (`:8889`, yalnız stack daxilində).
- **Prometheus** kollektoru və RabbitMQ-nu oxuyur. Məlumat 30 gün saxlanılır,
  diskdə 2 GB-dan çox yer tutmur.
- **Grafana** dashboard-ları repodan oxuyur (`docker/monitoring/grafana/dashboards`).
  Grafana-da edilən dəyişiklik saxlanılmır. Dəyişiklik repoda edilir (aşağıya baxın).
- Konvertor (converter) şəbəkəsizdir (ADR-0005), ona görə metrika göndərmir.
- Bu üç servis `monitoring` compose profili ilə qalxır. Profil sönülü olanda heç
  biri işləmir, stack-in qalan hissəsi də dəyişmir. Yaddaş limitləri: kollektor
  160 MB, Prometheus 256 MB, Grafana 384 MB (lokal ölçü, boş halda: təxminən 80 + 60 + 220 MB).

## Trace-lər (Tempo, T-479)

- Bir trace bir sorğunun bütün yoludur. Ona daxildir:
  - API-yə gələn sorğu;
  - onun MongoDB əmrləri;
  - xaricə gedən HTTP sorğuları (bank, fiskal, storage);
  - RabbitMQ-ya göndərdiyi mesaj və həmin mesajı işləyən consumer.
- Hər hissənin müddəti və xətası görünür.
- **Necə tapılır:** Grafana → Explore → "Tempo".
  - Log sətrindəki `TraceId` ilə birbaşa.
  - Və ya TraceQL ilə:
    - `{ span.neoprint.correlation_id = "<CorrelationId>" }`;
    - `{ status = error }`;
    - `{ duration > 2s }`;
    - `{ span.http.route = "api/files/{id}" }`.
- **Göndərilməyənlər:** müştərinin IP-si, user-agent, query string, MongoDB əmrlərinin mətni. `/health` və `/hubs` izlənmir.
- Hamısı yazılır (`Monitoring__TraceSampleRatio`, default `1`). Yük artanda, məsələn `0.2` ilə payı azaltmaq olar.
- Tempo 7 gün saxlayır. Limiti 384 MB-dır, boş halda ~45 MB.

## Xəta izləmə (GlitchTip, T-478)

```
API ─────────────(daxili şəbəkə)──────────┐
kiosk, mobil tətbiq, admin paneli ─ HTTPS ─┤──►  GlitchTip  (+ öz PostgreSQL-i)
                                            │
   DSN-i hər klient API-dən alır: GET /api/error-tracking/{admin|mobile|kiosk}
```

- **GlitchTip** (MIT) Sentry protokolunu başa düşür. Dörd yerdə Sentry SDK-ları işləyir:
  - backend: `Sentry.AspNetCore`;
  - kiosk: `Sentry`;
  - mobil: `sentry_flutter`;
  - admin: `@sentry/angular`.
- Hər klientin GlitchTip-də ayrıca layihəsi var: "NeoPrint API", "NeoPrint Kiosk", "NeoPrint Mobile", "NeoPrint Admin".
- **DSN gizli məlumat deyil.** O, klientə yalnız öz layihəsinə xəta göndərməyə imkan verir və hər brauzerdə və telefonda olur.
  - Klientlər DSN-i girişdən əvvəl API-dən alır, ona görə giriş ekranındakı çöküş də çatır.
  - API-nin öz DSN-i (`ErrorTracking__ApiDsn`) verilmir.
- DSN boşdursa (lokal default), xəta izləmə sönülüdür və heç nə göndərilmir. Tətbiqlər əvvəlki kimi işləyir.
- **Göndərilən:** xəta və onun stack-i, mühit, versiya.
- **Göndərilməyən:**
  - istifadəçi, e-poçt, IP, cookie, başlıqlar, sorğunun gövdəsi, query string;
  - breadcrumb-lar, yəni xətadan əvvəl istifadəçinin etdikləri;
  - kioskun maşın adı;
  - ekran şəkli.
- Hər SDK-da `beforeSend` bu sahələri təmizləyir, testlərlə yoxlanılır.
- Xətalar 30 gün saxlanılır (`GLITCHTIP_MAX_EVENT_LIFE_DAYS`).
- Admin paneli → "Monitorinq" → **"Sınaq xətası göndər"**: API bir sınaq xətası göndərir. O, bir dəqiqə ərzində GlitchTip-də "NeoPrint API" layihəsində görünür. Bütün yolun işlədiyini belə yoxlamaq olar.
- GlitchTip bir proses kimi işləyir (`SERVER_ROLE=all_in_one`), Valkey olmadan (növbə PostgreSQL-dədir).
  - Limitlər: GlitchTip 512 MB, PostgreSQL 256 MB.
  - Boş halda təxminən 170 + 75 MB (lokal ölçü).

### Qurulma: `tools/monitoring/glitchtip-bootstrap.sh`

Skript bunları yaradır, əgər yoxdursa:
- admini;
- "NeoPrint" təşkilatını;
- dörd layihəni.

Sonra DSN-ləri env faylına yazır. Təkrar işlədiləndə heç nəyi dəyişmir.
Test serverində `deploy.sh` onu hər deploy-da özü işlədir. Lokal:

```bash
docker compose --profile monitoring up -d
GLITCHTIP_ADMIN_EMAIL=admin@neoprint.local GLITCHTIP_ADMIN_PASSWORD='<parol>'   bash tools/monitoring/glitchtip-bootstrap.sh .env.error-tracking --profile monitoring
NEOPRINT_ERROR_TRACKING_ENV=./.env.error-tracking docker compose up -d api admin
```

`.env.error-tracking` gitignore-dadır. Bu dəyişəni hər dəfə yazmamaq üçün
`NEOPRINT_ERROR_TRACKING_ENV=./.env.error-tracking` sətrini `.env`-ə əlavə edin.

## Dashboard-lar

| Dashboard | Nə göstərir |
|---|---|
| **Ümumi baxış** (ana səhifə) | API sorğu sayı, 5xx faizi, p95 gecikmə, yaddaş; dead-letter və gözləyən mesajlar; uğursuz və gözləyən refund-lar və fiskal çeklər, storage sync xətaları; online / offline kiosklar, açıq incident-lər. Qırmızı rəng baxılmalı yer deməkdir |
| **API** | Ən yavaş və ən çox 5xx verən endpoint-lər, 429 (limit), işlənməmiş exception-lar, HTTP və SignalR bağlantıları, yaddaş / CPU / thread pool, xarici sorğular (bank, fiskal) |
| **Növbələr (RabbitMQ)** | Dead-letter növbələri (MassTransit-in `_error` / `_skipped` növbələri), gözləyən və işlənən mesajlar, consumer-i olmayan növbələr |
| **Əməliyyatlar** | Statuslara görə refund, fiskal, storage, kiosk və incident sayları; fayl təmizləmə; uzlaşdırma tapıntıları; atılan analitika hadisələri |

"Əməliyyatlar" rəqəmləri MongoDB-dən dəqiqədə bir sayılır
(`OperationalMetricsCollector`, meter `NeoPrint.Operations`). Ona görə API
restart olanda itmir, bir neçə API nüsxəsi də eyni rəqəmi göstərir.

### Dashboard-u dəyişmək

Dashboard-lar `tools/monitoring/dashboards.mjs`-də kod kimi yazılıb, JSON faylları
ondan yaradılır:

```bash
node tools/monitoring/dashboards.mjs          # JSON fayllarını yenidən yazır
node tools/monitoring/dashboards.mjs --check  # JSON köhnədirsə xəta verir (CI)
```

Grafana faylları dəqiqədə bir yenidən oxuyur, restart lazım deyil.

## Lokal

```bash
docker compose --profile monitoring up -d
```

| Nə | Ünvan |
|---|---|
| Grafana | http://localhost:3000 — `admin` / `admin`, ilk girişdə yeni parol istəyir |
| Prometheus | http://localhost:9090 — yalnız bu kompüterdən (127.0.0.1) |
| GlitchTip | http://localhost:8000 — bootstrap skriptinə verdiyiniz e-poçt / parol |

Ancaq API-ni Visual Studio / `dotnet run` ilə işlədirsinizsə, metrikaları
kollektora göndərmək üçün `Monitoring__OtlpEndpoint`-ə `http://localhost:4318/`
yazın və kollektorun portunu açın. Default olaraq kollektor portu kənara açılmır.
Compose-dakı API metrikaları özü göndərir.

## Test serveri

- Profil `deploy.sh` tərəfindən yandırılır: `.env`-də `COMPOSE_PROFILES=office,monitoring`.
- **Grafana:** `https://grafana.<HOST>/`.
  - Bu ünvanı yalnız Caddy açır, giriş Grafana-nın öz loginidir.
  - İstifadəçi və parol serverdə `.test-server/monitoring/grafana.env` faylındadır (`GF_SECURITY_ADMIN_*`).
  - Parolu `deploy.sh` təsadüfi yaradır və heç yerdə çap etmir. Monitorinqin ilk deploy-unda parol
    `.test-server/credentials`-ə yazılmışdı. Növbəti deploy həmin parolu bu fayla köçürür.
  - Parol yalnız Grafana-nın bazası ilk dəfə yarananda tətbiq olunur. Sonra Grafana-da dəyişdirsəniz, yeni parol qalır.
- **GlitchTip:** `https://errors.<HOST>/`. Giriş GlitchTip-in özündədir.
  - E-poçt və parol serverdə `.test-server/monitoring/glitchtip-admin` faylındadır.
  - Qeydiyyat bağlıdır. Yeni istifadəçini admin GlitchTip-in özündə dəvət edir.
  - Bazanın parolu və `SECRET_KEY` `.test-server/monitoring/glitchtip.env` faylındadır.
  - DSN-lər `.test-server/monitoring/error-tracking.env` faylındadır və API-yə buradan verilir.
  - Kiosklar, telefon və panel xətaları bu ünvana göndərir, ona görə o, test alətlərinin parolu ilə bağlı deyil.
- Bu fayllar bir dəfə yaranır və serverdən çıxmır. Repodakı `docker/monitoring/dev-secrets` yalnız lokal üçündür.
  Serverdə `.env`-dəki `NEOPRINT_MONITORING_SECRETS_DIR` və `NEOPRINT_ERROR_TRACKING_ENV` başqa yerə göstərir.
- Prometheus kənardan açılmır.
- Yaddaş: monitorinq servisləri boş halda təxminən 600 MB tutur (Grafana yığımı ~360 MB, GlitchTip ~250 MB).
  Yaddaş azdırsa: `.env`-də `COMPOSE_PROFILES`-dən `monitoring`-i çıxarın və
  `docker compose up -d --remove-orphans` edin.

## Alert-lər

Alert qaydaları hələ yazılmayıb. Dashboard-lar `OBSERVABILITY.md`-dəki "Alerts"
siyahısını rəqəm kimi göstərir. Bildiriş kanalı (e-poçt / Telegram, T-243) seçiləndə
Grafana alert-ləri bu panellərin sorğuları ilə qurulacaq.
