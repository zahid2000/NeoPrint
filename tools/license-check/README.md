# license-check

NeoPrint-in lisenziya siyasətini (Bölmə 2.0) üç ekosistem üzrə yoxlayan skript:
**NuGet**, **npm** və **pub**. Uyğunsuzluq tapılarsa `exit 1` qaytarır və CI dayanır.

Alətin **heç bir npm asılılığı yoxdur** — yalnız Node standart kitabxanası
(Bölmə 2.0, qayda 1).

## İstifadə

```bash
node tools/license-check             # oxunaqlı hesabat
node tools/license-check --verbose   # uyğun gələn asılılıqları da göstər
node tools/license-check --json      # CI üçün JSON
node tools/license-check --strict    # skan edilməyən ekosistem = xəta
```

CI `--strict` ilə çağırır, həm də bütün layihələr restore olunandan **sonra**:
əks halda restore uğursuz olduqda yoxlama "boş" keçib yaşıl görünə bilər.

## Nə yoxlanılır

| Yoxlama | Nə edir |
|---|---|
| **Lisenziya** | Hər asılılığın lisenziyası `allowlist.json`-dakı `allowedLicenses` siyahısı ilə tutuşdurulur. SPDX ifadələri (`MIT OR Apache-2.0`, `(A AND B)`, `... WITH ...`) qiymətləndirilir |
| **Adla istisnalar** | `packageExceptions` — yalnız verilmiş lisenziya üçün. Paket lisenziyasını dəyişsə yoxlama **yenidən** xəta verir (`exception-mismatch`) |
| **Pinlənmiş major-lar** | `pinnedMajors` — MediatR 12, MassTransit 8, FluentAssertions 7. Həm `Directory.Packages.props`-dakı **elan**, həm də `dotnet list` ilə alınan **həll olunmuş** (transitive daxil) versiya yoxlanılır |
| **Qadağan olunmuş paketlər** | `bannedPackages` — Bölmə 2.0-da açıq rədd edilənlər (Serilog, FluentValidation, AutoMapper, Moq və s.). Lisenziyası icazəli olsa belə xəta verir |

## Lisenziya necə tapılır

| Ekosistem | Mənbə |
|---|---|
| NuGet | `dotnet list <sln> package --include-transitive --format json` → qlobal paket qovluğundakı `.nuspec` (`<license type="expression">`, `type="file"` → LICENSE mətni, köhnə `<licenseUrl>`) |
| npm | `<project>/node_modules/**/package.json` (`license`, köhnə `licenses` massivi), sonra paketin öz LICENSE faylı |
| pub | `<project>/pubspec.lock` → pub keşindəki paketin LICENSE faylı; Flutter SDK paketləri BSD-3-Clause sayılır |

Metadata-dan tapılmayan lisenziya **xəta**dır (`unknown-license`). Belə paket üçün
`allowlist.json` → `packageLicenses`-ə **paketin öz LICENSE faylı yoxlanıldıqdan sonra**
sətir əlavə edilir.

## Siyasətin dəyişdirilməsi

`allowlist.json`-a hər əlavə şüurlu siyasət qərarıdır və `docs/DECISIONS.md` ilə
`docs/THIRD_PARTY.md`-də qeyd olunmalıdır. Pinlənmiş major-un yüksəldilməsi
lisenziyanı kommersiyaya çevirir — bu, siyasətin genişləndirilməsi ilə deyil,
versiyanın aşağı salınması ilə həll olunur.

## Testlər

```bash
cd tools/license-check && npm test
```
