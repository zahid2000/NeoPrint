# NeoPrint

Çoxfunksiyalı terminal (kiosk) printer platforması. Sistem dörd hissədən ibarətdir:

| Qovluq | Nədir | Texnologiya |
|---|---|---|
| `backend/` | Mərkəzi API, real-time, ödəniş, fiskal, bildiriş | .NET 8 |
| `desktop/` | Kiosk tətbiqi (sensor ekranlı terminal, Windows və Linux) | Avalonia UI 11 |
| `mobile/` | İstifadəçi mobil tətbiqi | Flutter 3 |
| `admin/` | Admin web paneli + Operator paneli (PWA) | Angular |

Yeganə spesifikasiya: [`docs/NeoPrint-PLAN.md`](docs/NeoPrint-PLAN.md). İş qaydaları: [`CLAUDE.md`](CLAUDE.md).

---

## Tələb olunan alətlər

| Alət | Minimum versiya | Nə üçün |
|---|---|---|
| .NET SDK | 8.0 (`global.json` ilə pinlənib) | backend, kiosk |
| Docker + Docker Compose | 24 / v2 | development infrastrukturu, integration testlər |
| Node.js | 20+ | `admin/`, `tools/license-check` |
| Flutter / Dart | Flutter 3 stable | `mobile/` |
| git | 2.39+ | |

> .NET 9 SDK quraşdırılıbsa da layihə `global.json` sayəsində həmişə .NET 8 ilə build olunur.

---

## Development mühitinin qaldırılması

```bash
# 1. OpenBao seed dəyərlərini hazırla (yalnız bir dəfə)
cp .env.openbao-seed.example .env.openbao-seed
#    Faylı aç və development dəyərlərini yaz. Bu fayl .gitignore-dadır.

# 2. Bütün servisləri qaldır
docker compose up -d

# 3. Sağlamlığı yoxla
curl http://localhost:5080/health
```

`docker compose up` ardıcıllığı: `openbao` qalxır → `openbao-init` (birdəfəlik) init/unseal edir, KV v2 mount-u, AppRole-ları, policy-ləri və audit device-i qurur, seed dəyərlərini yazır → `api` başlayır.

### Servislər və portlar

| Servis | Ünvan |
|---|---|
| API | http://localhost:5080 (Swagger: `/swagger`, yalnız Development) |
| Admin panel | http://localhost:8080 (development-də `ng serve` → http://localhost:4200) |
| OpenBao | http://localhost:8200 |
| MongoDB | `mongodb://localhost:27017` (tək node-lu replica set `rs0`) |
| Redis | `localhost:6379` |
| RabbitMQ | http://localhost:15672 (management) |
| RustFS (primary storage) | http://localhost:9000 (S3 API) |
| RustFS console | http://localhost:9001/rustfs/console/ |
| RustFS (secondary) | http://localhost:9010 (migrasiya / mirror hədəfi) |
| Mailpit | http://localhost:8025 |

---

## Build və test

```bash
# Backend
dotnet build backend/NeoPrint.sln
dotnet test  backend/NeoPrint.sln

# Kiosk (desktop)
dotnet build desktop/NeoPrint.Kiosk.sln
dotnet test  desktop/NeoPrint.Kiosk.sln

# Mobile
cd mobile && flutter analyze && flutter test

# Admin
cd admin && npm ci && npm run lint && npm test && npm run build

# Lisenziya yoxlaması (bütün ekosistemlər)
node tools/license-check
```

CI (`.github/workflows/ci.yml`) eyni addımları `windows-latest` və `ubuntu-latest` üzərində icra edir.

---

## Təhlükəsizlik qaydaları (qısa)

- **Repo-ya heç bir real secret yazılmır.** Backend secret-lərini yalnız OpenBao-dan oxuyur; environment variable-da yalnız OpenBao-ya qoşulma məlumatı olur (`BAO_ADDR`, `BAO_ROLE_ID`, `BAO_SECRET_ID_FILE`). Ətraflı: [`docs/OPENBAO.md`](docs/OPENBAO.md).
- **Kiosk tətbiqi universal build-dir:** kiosk-a aid kimlik, secret və ya konfiqurasiya build-ə, `appsettings.json`-a və ya env-ə qoyulmur. Kiosk öz cihaz açarı ilə təsdiqlənir və bütün parametrlərini backend-dən alır.
- **Bütün storage obyektləri** tətbiq səviyyəsində şifrələnir (AES-256-GCM envelope encryption).
- `.env*` və `.openbao/` `.gitignore`-dadır.

---

## Sənədlər

| Sənəd | Məzmun |
|---|---|
| [`docs/NeoPrint-PLAN.md`](docs/NeoPrint-PLAN.md) | Tam spesifikasiya (yeganə mənbə) |
| [`docs/DEV_ENVIRONMENT.md`](docs/DEV_ENVIRONMENT.md) | Lokal mühit: bütün əmrlər, ünvanlar, bağlantı sətirləri, test hesabları |
| [`docs/DESIGN_SYSTEM.md`](docs/DESIGN_SYSTEM.md) | Vizual və UX qaydaları (admin, mobil, kiosk üçün məcburi) |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Planda olmayan detallar üzrə qərarlar |
| [`docs/THIRD_PARTY.md`](docs/THIRD_PARTY.md) | Bütün üçüncü tərəf asılılıqlar və lisenziyaları |
| [`docs/OPENBAO.md`](docs/OPENBAO.md) | Secret idarəsi (development və production) |
| [`docs/API.md`](docs/API.md) | REST API və SignalR hub-ları |
