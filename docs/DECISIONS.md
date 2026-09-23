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
