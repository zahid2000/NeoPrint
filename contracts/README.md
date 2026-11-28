# Klient API müqaviləsi (ADR-0009, T-491)

Sahədəki kiosklar, provisioning skriptləri və mobil tətbiq köhnə versiyada ola bilər. Burada onların işlətdiyi API qeyd olunur ki, backend onu silməsin və dəyişməsin.

| Fayl | Nə var | Kim yazır |
|---|---|---|
| `client-api.json` | `minSupported` (hər klientin dəstəklənən minimum versiyası), `endpoints` (klient + metod + yol), `events` (SignalR hadisələri) | İnsan; yeni sətirləri test özü əlavə edir |
| `client-api.shapes.json` | Aktiv endpoint-lərin sorğu və cavab formaları (sahə adları, tiplər, enum dəyərləri; `!` — sorğuda məcburi, `?` — null ola bilər) | Yalnız test |

Yoxlayan: `backend/tests/NeoPrint.Api.IntegrationTests/ClientApiContractTests.cs` (Docker lazım deyil, CI-ın backend işində işləyir).

## CI nəyə qırmızı olur

- Klientin kodunda yeni yol var, amma faylda yoxdur — yeniləmə əmrini işlədin (aşağıda).
- Aktiv sətirdəki endpoint backend-də artıq yoxdur.
- Aktiv endpoint-in cavabından sahə silinib, tipi dəyişib, `null` ola bilən olub və ya enum dəyəri silinib.
- Sorğuya yeni **məcburi** sahə əlavə olunub (köhnə klient onu göndərmir).
- Klientin dinlədiyi hadisəni backend artıq göndərmir.
- Sətir fayldan silinib, amma onun formaları hələ `client-api.shapes.json`-dadır.
- Klient artıq çağırmadığı sətir `retiredIn`-siz qalıb.

Cavaba yeni sahə, enum-a yeni dəyər, sorğuya könüllü sahə əlavə etmək **sındırmır** — yalnız formalar faylını yeniləmək lazımdır.

## Yeniləmə

```bash
NEOPRINT_CONTRACT_UPDATE=1 dotnet test backend/tests/NeoPrint.Api.IntegrationTests --filter ClientApiContractTests
```

PowerShell: `$env:NEOPRINT_CONTRACT_UPDATE='1'; dotnet test backend/tests/NeoPrint.Api.IntegrationTests --filter ClientApiContractTests; Remove-Item Env:NEOPRINT_CONTRACT_UPDATE`

Əmr klientlərin yeni çağırdığı yolları və hadisələri əlavə edir, formalar faylını yenidən yazır. Sındıran dəyişiklik bu rejimdə də qırmızıdır. Nəticə `git diff` ilə yoxlanılır və commit olunur.

## Köhnə yolu silmək (genişləndir → köçür → daralt)

1. Klient yeni yola keçir. Köhnə sətrə `"retiredIn": "<köhnəni çağırmayan ilk klient versiyası>"` yazılır. Sətir silinmir.
2. Admin → "Versiyalar" səhifəsində bu versiyadan köhnə kiosk qalmadıqda `minSupported.kiosk` həmin versiyaya qaldırılır.
3. Bundan sonra endpoint backend-dən silinə bilər. Yeniləmə əmri onu formalar faylından çıxarır, sonra sətir də silinə bilər.

Bir **sahəni** silmək üçün sətrə `"retiredFields": { "<sahə yolu>": "<versiya>" }` yazılır. Sahə yolu formalar faylındakı kimi yazılır (məs. `camera.mode`, siyahının içi `items.list.name`).

- `"manual": true` — klientin kodundan tapılmayan çağırış (məs. URL-i serverdən gələn). Belə sətir "artıq çağırılmır" xətası vermir.
- `provisioning` versiyalanmır: onun sətirləri `retiredIn` ilə yox, yalnız `DECISIONS.md`-də qərarla silinir.

## Görmədiyi şeylər

- Sahənin **mənası** (məs. qəpik → manat) — kod baxışında yoxlanılır.
- `IActionResult` qaytaran endpoint-lərin cavab forması (`"untyped"`) — `[ProducesResponseType(typeof(...), 200)]` əlavə etsəniz, yoxlanılır.
- Query parametrləri və hadisələrin daşıdığı məlumat.
