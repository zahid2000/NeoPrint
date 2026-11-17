# ADR-0008 — .NET 8-dən .NET 10-a keçid (T-443)

### Status

Accepted — 2026-11-17, layihə sahibinin qərarı (T-439 sualı, T-443 tapşırığı).

### Context

Plan (Bölmə 2.0, 7.6) backend və kiosk üçün .NET 8 yazır. .NET 8-in rəsmi dəstəyi 2026-11-10-da bitdi — bundan sonra təhlükəsizlik yeniləmələri gəlmir. .NET 10 LTS-dir və 2028-ci ilin noyabrına qədər dəstəklənir. Bəzi kitabxanaların yeni major versiyaları ödənişlidir (MediatR 13+, MassTransit 9+, FluentAssertions 8+), amma bu, .NET versiyasından asılı deyil.

### Decision

- Bütün layihələr `net10.0`-a keçir (kioskun Windows layihələri `net10.0-windows`); `global.json` — SDK `10.0.100`, `latestFeature`.
- `Microsoft.AspNetCore.*` / `Microsoft.Extensions.*` / `System.Management` → `10.0.15`; `System.IdentityModel.Tokens.Jwt` → `8.19.2` (JwtBearer 10.0.15-in tələb etdiyi).
- **Pulsuz major-lar qalır:** MediatR 12.x, MassTransit 8.x, FluentAssertions 7.x — .NET 10-da işləyir, yüksəldilmir.
- Docker: `dotnet/sdk:10.0`, `dotnet/aspnet:10.0` (əsası Ubuntu 24.04; LibreOffice / Ghostscript / şriftlər eyni adlarla qurulur), dev kiosk konteyneri `dotnet/runtime:10.0`.
- .NET 10 SDK tranzitiv paketləri də audit edir: köhnə tranzitiv `System.Security.Cryptography.Pkcs` (PDFsharp, MimeKit) və `Newtonsoft.Json` (Hangfire) təhlükəsiz versiyalara pin olundu.

- **Konvertor (ADR-0005):** .NET 10-da `Microsoft.Extensions.Logging.Abstractions` və `DependencyInjection.Abstractions` ASP.NET Core paylaşılan framework-ünün hissəsidir, API-nin çıxışında (konvertor oradan başladılır) artıq yoxdur. Konvertora `FrameworkReference Microsoft.AspNetCore.App` verildi — yalnız kitabxanalar; izolyasiya (sandbox, şəbəkəsiz konteyner, limitlər) dəyişmir. Bunsuz PDF redaktəsi və tərtibat render-i `FileNotFoundException` ilə düşürdü (CI-da 7 test).

### Alternatives

- .NET 8-də qalmaq — dəstəksiz runtime, yeni zəifliklər bağlanmır. Rədd edildi.
- .NET 9 (STS) — 2026-cı ilin mayında dəstəyi bitdi. Rədd edildi.
- Ödənişli major-lara keçmək — lisenziya xərci, funksional ehtiyac yoxdur. Rədd edildi.

### Consequences

- Müsbət: dəstəklənən runtime, performans, yeni təhlükəsizlik yeniləmələri.
- Mənfi: yeni analizator qaydaları (`CA2264`, `ASPDEPR005`) koda kiçik düzəlişlər tələb etdi; inkişaf maşınlarında .NET 10 SDK lazımdır.
- Uzunmüddətli risk: MassTransit 8-in dəstəyinin bitməsi — əvəzi ayrıca qərarla (Wolverine / Rebus / RabbitMQ.Client + öz outbox).

### Security Impact

Müsbət: dəstəklənən runtime; tranzitiv paket auditi əvvəl görünməyən iki zəif paketi aşkar etdi və bağladı. Heç bir təhlükəsizlik nəzarəti zəiflədilmədi.

### Operational Impact

- Backend imici yenidən qurulur (Ubuntu əsaslı runtime). Deploy workflow-u dəyişmir.
- Kiosk relizləri **self-contained**-dir: .NET 10 runtime paketin içindədir, cihaza ayrıca runtime qurulmur. Agent yeni paketi tam qovluq kimi qoyur — runtime dəyişikliyi adi yeniləmədir; paketin həcmi bir qədər böyüyür.
- CI SDK-nı `global.json`-dan götürür.

### Migration

Data miqrasiyası yoxdur. İnkişaf maşınlarında .NET 10 SDK quraşdırılır (`docs/DEV_ENVIRONMENT.md`).
