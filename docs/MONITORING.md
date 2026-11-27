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

Ancaq API-ni Visual Studio / `dotnet run` ilə işlədirsinizsə, metrikaları
kollektora göndərmək üçün `Monitoring__OtlpEndpoint`-ə `http://localhost:4318/`
yazın və kollektorun portunu açın. Default olaraq kollektor portu kənara açılmır.
Compose-dakı API metrikaları özü göndərir.

## Test serveri

- Profil `deploy.sh` tərəfindən yandırılır: `.env`-də `COMPOSE_PROFILES=office,monitoring`.
- Grafana: `https://grafana.<HOST>/`. Bu ünvanı yalnız Caddy açır, giriş Grafana-nın
  öz loginidir. İstifadəçi və parol serverdə `.test-server/credentials` faylındadır
  (`GRAFANA_ADMIN_USER`, `GRAFANA_ADMIN_PASSWORD`). Parolu `deploy.sh` təsadüfi
  yaradır və heç yerdə çap etmir.
- Bu parol yalnız Grafana-nın bazası ilk dəfə yarananda tətbiq olunur. Sonra
  Grafana-da dəyişdirsəniz, yeni parol qalır.
- Prometheus kənardan açılmır.
- Yaddaş azdırsa: `.env`-də `COMPOSE_PROFILES`-dən `monitoring`-i çıxarın və
  `docker compose up -d --remove-orphans` edin.

## Alert-lər

Alert qaydaları hələ yazılmayıb. Dashboard-lar `OBSERVABILITY.md`-dəki "Alerts"
siyahısını rəqəm kimi göstərir. Bildiriş kanalı (e-poçt / Telegram, T-243) seçiləndə
Grafana alert-ləri bu panellərin sorğuları ilə qurulacaq.
