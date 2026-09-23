# Qərarlar jurnalı

`docs/NeoPrint-PLAN.md` layihənin yeganə spesifikasiyasıdır. Planda açıq göstərilməyən kiçik
detallar üçün seçilmiş ən sadə və təhlükəsiz variant bu faylda qeyd olunur.

Format: `tarix | addım | qərar | səbəb`

| Tarix | Addım | Qərar | Səbəb |
|---|---|---|---|
| 2026-09-23 | 1 | .NET SDK `global.json` ilə 8.0-a pinləndi (`rollForward: latestFeature`) | Maşında .NET 9 SDK da var; plan .NET 8 tələb edir, build həmişə 8.0 ilə getməlidir |
| 2026-09-23 | 1 | Central Package Management (`Directory.Packages.props`) repo kökündə, `CentralPackageTransitivePinningEnabled=true` | Plan versiyaların pinlənməsini tələb edir; `backend/` və `desktop/` eyni paketləri paylaşdığı üçün versiyalar tək yerdə saxlanılır |
| 2026-09-23 | 1 | `TreatWarningsAsErrors=true` bütün .NET layihələri üçün | Xəbərdarlıqların yığılmasının qarşısını alır; CI-nin "yaşıl" olması real mənaya malik olur |
| 2026-09-23 | 1 | SkiaSharp 3.x/4.x əvəzinə **2.88.9** pinləndi | Avalonia 11.3.22 öz asılılığında SkiaSharp 2.88.9 tələb edir; transitive pinning ilə münaqişənin qarşısı alınır. Backend və kiosk eyni versiyanı paylaşır |
| 2026-09-23 | 1 | StackExchange.Redis 3.x əvəzinə **2.13.17** | `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 8.0.x 2.x xəttini gözləyir; SignalR backplane-in uyğunluğu prioritetdir |
| 2026-09-23 | 1 | Swashbuckle.AspNetCore 10.x əvəzinə **7.3.2** | 7.x xətti .NET 8-i dəstəkləyən sonuncu stabil xəttdir |
| 2026-09-23 | 1 | Health check-lər üçün `AspNetCore.HealthChecks.*` (Xabaril) paketləri **istifadə edilmir**; Mongo/Redis/RabbitMQ/OpenBao üçün öz `IHealthCheck` implementasiyalarımız yazılır | Bölmə 2.0 qaydası 1: əvvəlcə platformanın daxili imkanları (`Microsoft.Extensions.Diagnostics.HealthChecks`) və öz kodumuz |
| 2026-09-23 | 1 | `tools/license-check` Node.js ilə yazılır (əlavə asılılıq olmadan, yalnız Node standart kitabxanası) | Layihədə artıq Node var (`admin/`); üç ekosistemi (NuGet, npm, pub) bir alətlə yoxlamaq üçün ən sadə variant |
| 2026-09-23 | 1 | `license-check`-ə `--strict` bayrağı əlavə edildi; CI onunla çağırır | Restore uğursuz olduqda skan "boş" keçib yaşıl görünə bilərdi — bu, yoxlamanı mənasız edirdi |
| 2026-09-23 | 1 | `Hangfire.AspNetCore` və `Hangfire.NetCore` də LGPL-3.0 istisnasına əlavə edildi | Paketlərin real `LICENSE.md`-i yoxlanıldı: bütün Hangfire ailəsi eyni çoxlisenziyalıdır (LGPL-3.0 / kommersiya). Plan cədvəlində `Hangfire.AspNetCore` səhvən MIT yazılmışdı; `THIRD_PARTY.md` düzəldildi |
| 2026-09-23 | 1 | `Hangfire.Mongo` → MIT, `System.Memory` → MIT, `xunit.abstractions` → Apache-2.0 `packageLicenses`-ə yazıldı | Bu paketlərin metadata-sında maşınla oxunan lisenziya yoxdur; hər biri öz `LICENSE` faylı / layihə deposu ilə əl ilə təsdiqləndi |
| 2026-09-23 | 1 | Docker Hub-dakı `minio/minio` əvəzinə `quay.io/minio/minio` istifadə olunur | Docker Hub-da bu image üçün anonim pull bağlanıb (`pull access denied`); quay.io MinIO-nun rəsmi registry-sidir və autentifikasiya tələb etmir |
| 2026-09-23 | 1 | OpenBao audit device-i `openbao/config/openbao.hcl`-də deklarativ elan olunur, API ilə yox | OpenBao 2.6-dan etibarən audit device-i API ilə aktivləşdirmək qadağandır (`cannot enable audit device via API`). `init.sh` indi yalnız mövcudluğunu yoxlayır və yoxdursa dayanır |
| 2026-09-23 | 1 | Development MongoDB tək node-lu replica set kimi işləyir; replica key faylı konteynerin içində yaradılır (`docker/mongo/entrypoint.sh`) | Replica set + auth birlikdə keyFile tələb edir; keyFile-ın sahibi `mongodb`, icazəsi `400` olmalıdır — Windows host-dan bind mount bunu təmin edə bilmir |
| 2026-09-23 | 1 | Compose faylında dəyişən əvəzləmə (`${...}`) istifadə edilmir; konteynerlər öz development credential-larını `env_file: .env.openbao-seed` ilə alır | `docker compose up` əlavə `--env-file` bayrağı olmadan işləməlidir. `api` konteyneri bu faylı **oxumur** — o, yalnız OpenBao-ya qoşulma məlumatını alır (Bölmə 2.13) |
| 2026-09-23 | 1 | GhostPCL (`gpcl6`) və GhostXPS (`gxps`) backend image-ində mənbədən ayrıca build stage-də qurulur | Artifex bu iki alət üçün Linux binary dərc etmir (yalnız Windows zip və mənbə arxivi). Ayrıca proses kimi, dəyişdirilmədən istifadə olunur |
| 2026-09-23 | 1 | Backend Dockerfile-ın build konteksti repo köküdür (`-f backend/Dockerfile .`) | Build `global.json`, `Directory.Build.props` və `Directory.Packages.props` fayllarına ehtiyac duyur; bunlar `backend/`-dən yuxarıdadır |
| 2026-09-23 | 1 | Kriptoqrafik açarlar (`NEOPRINT_JWT_SIGNING_KEY`, `NEOPRINT_KEK__*`, `NEOPRINT_STORAGE_SECRETS_KEY`, `NEOPRINT_DATA_PROTECTION_KEY`, `NEOPRINT_QR_HMAC_KEY`) seed faylında boş qalır və `openbao-init` tərəfindən ilk işə düşmədə təsadüfi generasiya olunur | Repo-da (hətta nümunə faylında) heç bir açar materialı olmur; hər developer maşınının öz açarları olur |
| 2026-09-23 | 1 | Testcontainers üçün `DOCKER_API_VERSION` test assembly-si yüklənəndə avtomatik təyin olunur (`DockerApiVersionInitializer`) | Testcontainers 4.15 Docker API 1.44 istəyir; bu maşındakı Docker Engine 24.0.7 maksimum 1.43 dəstəkləyir və "client version is too new" xətası "Docker işləmir" kimi görünürdü. `docker version` ilə serverin dəstəklədiyi versiya soruşulub ötürülür — həm köhnə, həm yeni engine ilə işləyir |
| 2026-09-23 | 1 | Konteyner tələb edən testlər `[DockerFact]` ilə işarələnir və Docker yoxdursa **skip** olunur (fail yox) | CI-də Docker həmişə var, ona görə orada real işləyirlər; lokalda Docker qaldırmayan developer bloklanmır |
| 2026-09-23 | 1 | `OpenBaoOptions`-a `InitialRetryDelay` / `MaxRetryDelay` əlavə edildi | Startup retry-ın testi 1 saniyəlik sabit başlanğıc gecikmə ilə ya çox yavaş, ya da saxta saat tələb edirdi; Bölmə 2.0 qaydası 1-ə görə saxta saat kitabxanası əlavə etmək əvəzinə parametr açıldı |
