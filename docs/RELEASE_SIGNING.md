# Buraxılış paketlərinin oflayn imzalanması

Bu sənəd kiosk buraxılış paketlərinin (UI, Agent, provisioning) necə yığıldığını, necə imzalandığını və yoxlandığını, imza açarlarının necə yaradılıb saxlanıldığını və açar itəndə və ya sızanda nə edildiyini təsvir edir (T-313, T-316, `docs/DECISIONS.md` 2026-10-11).

**Əsas qayda:** production özəl açarı yalnız layihə sahibinin oflayn kompüterində və onun şifrəli ehtiyat daşıyıcılarında olur. O, CI-ya, repoya, buluda, e-poçta, mesajlaşmaya və Claude-a **heç vaxt** getmir. Repoda yalnız public açarlar olur (`desktop/src/NeoPrint.Kiosk.Core/Updates/KeySets/`).

## 0. İki açar dəsti: production və staging (T-316)

| | **production** | **staging** |
|---|---|---|
| Hansı kiosklar | Real pilot kioskları (golden image, M19) | Test serverinə qoşulan və development stack-dəki kiosklar (`tools/dev-kiosk`, Linux konteyner kiosku) |
| Public açarlar | `KeySets/ReleaseKeys.production.cs` — **hələ boşdur** (T-315) | `KeySets/ReleaseKeys.staging.cs` — 2 açar (`e097a6082f6d3545` cari, `fa8b3e094c232de1` növbəti) |
| Özəl açar harada | Oflayn kompüter + 2 şifrəli USB (bölmə 3) | Layihə sahibinin iş kompüterində, repodan kənarda: `%USERPROFILE%\.neoprint\release-keys\staging\` |
| Kim imzalayır | Layihə sahibi, oflayn, parolu əllə yazaraq (`sign`) | Layihə sahibinin kompüterində `sign-staging` (Claude da bu kompüterdə işləyəndə) |

**Dəst build parametri ilə seçilir, konfiqurasiya ilə yox:** `dotnet publish ... -p:ReleaseKeySet=staging` (və ya `production`). Parametr verilməsə **production** götürülür — unudulmuş parametr test açarlı kiosk buraxmır. Layihə yalnız seçilmiş dəstin faylını kompilyasiya edir, ona görə:

- production build-də staging açarları, staging sinfi və hətta "staging" sözü **yoxdur** — `release-sign check-build` bunu hər NeoPrint assembly-sində yoxlayır (CI-da hər push-da və hər buraxılış paketində), test: `The_kiosk_assembly_carries_its_own_key_set_and_nothing_of_the_other`;
- staging paketi production kioskunda `UnknownKey` ilə rədd olunur, production paketi staging kioskunda da (test: `A_package_of_one_key_set_is_refused_by_a_build_of_the_other`);
- manifestdə `keySet` sahəsi var və imzalanır: başqa dəst üçün işarələnmiş paket, açarı etibarlı olsa belə, `WrongTarget` ilə rədd olunur.

Paketin adında dəst görünür: `neoprint-kiosk-agent-0.8.0-linux-x64-staging.tar.gz`, artefakt: `kiosk-release-0.8.0-linux-x64-staging`.

**Staging özəl açarı niyə GitHub secret-də deyil (qərar):** layihə sahibinin kompüterində saxlanılır. CI-da secret olsaydı, repoya yazma icazəsi olan hər kəs (və ya ələ keçirilmiş workflow / action) onu çıxara bilərdi; staging paketləri isə avtomatik deyil, yeniləmə sınağı üçün lazım olanda imzalanır — avtomatlaşdırmanın faydası azdır. İmkan saxlanılır: `sign-staging` parolu `NEOPRINT_STAGING_SIGNING_PASSPHRASE` mühit dəyişənindən də oxuyur, gələcəkdə CI-da imzalamaq lazım olsa, açar yalnız ayrıca "environment" ilə qorunan secret-ə qoyulur (layihə sahibi özü). **Risk (hər iki halda):** staging açarı sızarsa, kimsə test serverinin və development kiosklarının qəbul etdiyi paketi imzalaya bilər — **pilot kioskuna təsiri yoxdur** (production build staging açarı daşımır). Həll: yeni staging cütü yaradılır, public açarlar `ReleaseKeys.staging.cs`-də dəyişdirilir, dev kiosklar yenidən yığılır.

Staging açarının parolu açarın yanındakı `passphrase.txt`-dədir: bu, production açarından fərqli olaraq **məqsədli güzəştdir** — açar faylı tək başına kopyalansa açılmır, amma maşının özü qorunmalıdır.

## Staging: sürətli başlanğıc (T-320)

Test serverinə staging paketi yükləmək üçün **bir əmr** kifayətdir. Skript açarı yoxlayır (yoxdursa yaradır), son CI paketlərini endirir, imzalayır, yoxlayır və panelə hansı faylların yüklənəcəyini yazır. Heç nə serverə göndərilmir, özəl açar kompüterdən çıxmır.

**Bir dəfə lazım olanlar:** .NET 8 SDK, GitHub CLI (`gh`) və `gh auth login` ilə giriş, repo kompüterdə.

**Əmr** (PowerShell, repo qovluğunda):

```powershell
powershell -ExecutionPolicy Bypass -File tools\release-sign\staging-quickstart.ps1
```

Seçimlər: `-Platform linux-x64` və ya `-Platform win-x64` — yalnız bir platforma (standart: hər ikisi); `-RunId 40103493022` — konkret CI işi (standart: "Release packages"in son uğurlu işi); `-OutDir C:\yol` — paketlərin qovluğu (standart: `%USERPROFILE%\.neoprint\staging-packages\<iş nömrəsi>`).

**Nə görməlisiniz:** beş addım, hər birinin sonunda yaşıl `OK`:

1. `dotnet və gh var, GitHub-a giriş edilib`, `release-sign hazırdır`;
2. `açar yerindədir və repodakı staging açarıdır`;
3. `N artefakt endirildi`;
4. hər paket üçün `signed with staging key e097a6082f6d3545` və `OK: ... key e097a6082f6d3545`, sonda `bütün paketlər imzalandı`;
5. hər paket üçün üç fayl: **Paket** (`….tar.gz` və ya `….zip`), **Manifest** (`….manifest.json`), **İmza** (`….manifest.json.sig`).

Sonra test serverinin panelində: **İdarəetmə → Buraxılışlar → "Paket yüklə"** → üç faylı seçin → kanal **Pilot** → **Yüklə**. Siyahıda yeni sətir görünür: komponent, versiya, platforma, dəst `staging`, açar `e097a6082f6d3545`, kanal Pilot.

**Qırmızı `DAYANDI` görsəniz** — altındakı sarı sətir nə etmək lazım olduğunu deyir:

| Mətn | Səbəb | Nə etməli |
|---|---|---|
| .NET SDK / GitHub CLI tapılmadı | Proqram quraşdırılmayıb | Quraşdırın, terminalı yenidən açın |
| GitHub-a giriş edilməyib | `gh` hesabı tanımır | `gh auth login`, brauzerdə təsdiq |
| "Release packages" workflow-unun uğurlu işi tapılmadı / Artefakt endirilmədi | Hələ iş yoxdur və ya artefaktın 30 günü keçib | GitHub → Actions → "Release packages" → "Run workflow" (versiya, məs. `0.8.0-pilot.2`), bitəndən sonra skripti yenidən başladın |
| … artıq var (bu iş əvvəl endirilib) | Eyni iş ikinci dəfə | Köhnə qovluğu silin və ya `-OutDir` verin |
| … imzalanmadı, yuxarıda `wrong passphrase` | Parol səhvdir | Skripti yenidən başladın, parolu diqqətlə yazın |
| … imzalanmadı, yuxarıda `not the file in the manifest` | Endirilən fayl zədələnib | Qovluğu silin, yenidən başladın |
| `DİQQƏT: bu kompüterdəki staging açarı repodakı staging açarı deyil` | Açar başqa kompüterdə yaradılıb və ya yenidir | Yalnız iki `.public.pem` faylını göndərin (`.private.pem` və `passphrase.txt` **heç vaxt**) — `ReleaseKeys.staging.cs` yenilənir, workflow yenidən işlədilir, sonra skript |

**Açar yoxdursa** (yeni kompüter) skript `current` və `next` açarlarını yaradır və hər biri üçün parolu iki dəfə soruşur (ən azı 12 simvol; yazarkən ekranda heç nə görünmür — normaldır). Yeni açarı kiosklar hələ tanımır, ona görə skript yuxarıdakı `DİQQƏT` mesajı ilə dayanır. Parol `passphrase.txt`-də yoxdursa, imzalayarkən soruşulur.

## 1. Necə işləyir

```
CI (tag v*)  →  paket + manifest + SHA256SUMS  (imzasız artefakt)
                     │
                     ▼  (USB ilə)
Oflayn kompüter  →  release-sign sign  →  manifest.json.sig
                     │
                     ▼
Admin panel (M16)  →  kiosk endirir  →  Agent yoxlayır:
     imza (öz dəstinin 2 açarından biri) → manifest → komponent / platforma / dəst → geri çəkilməyib
     → quraşdırılandan yenidir → Agent versiyası kifayətdir → ölçü + SHA-256
```

| Fayl | Nədir |
|---|---|
| `neoprint-kiosk-<komponent>-<versiya>-<platforma>-<dəst>.zip` / `.tar.gz` | Paket: `app` (UI), `agent`, `provisioning`; Windows-da `zip`, Linux-da `tar.gz`; dəst `production` və ya `staging` |
| `<paket>.manifest.json` | Komponent, platforma, açar dəsti (`keySet`), versiya, `minAgentVersion`, fayl adı, ölçü, SHA-256, commit, yaradılma vaxtı |
| `<paket>.manifest.json.sig` | İmza: `keyId`, `ES256` (ECDSA P-256 + SHA-256), imza (base64) |
| `SHA256SUMS` | Bütün faylların SHA-256-sı (CI run-ın xülasəsində də çap olunur) |

İmzalanan manifestin **dəqiq baytlarıdır** (önündə sabit kontekst sətri ilə — imza başqa sənəd növünə köçürülə bilməz). Paketin özü imzalanmır: manifestdəki SHA-256 onu bağlayır.

**Kiosk paketi rədd edir, əgər:**

| Səbəb (`ReleaseRejection`) | Nə deməkdir |
|---|---|
| `UnknownKey` | Kioskdakı iki açardan heç biri ilə imzalanmayıb (və ya build-də açar yoxdur) |
| `BadSignature` | İmza manifestə uyğun deyil — manifest imzadan sonra dəyişdirilib və ya imza saxtadır |
| `MalformedSignature`, `MalformedManifest` | Fayl oxunmur; manifestdə naməlum və ya çatışmayan sahə var |
| `WrongTarget` | Başqa komponent, platforma və ya açar dəsti üçün paket |
| `Withdrawn` | Paket buraxılışdan sonra geri çəkilib (M16: admin panel) |
| `NotNewer` | Quraşdırılmış versiyadan yeni deyil — köhnə paketin təkrar göndərilməsi |
| `AgentTooOld` | Paket daha yeni Agent tələb edir (`minAgentVersion`) |
| `PackageMismatch` | Paketin ölçüsü və ya SHA-256-sı manifestdəkindən fərqlidir — paket dəyişdirilib |

Testlər: `desktop/tests/NeoPrint.Kiosk.Tests/ReleasePackageCheckTests.cs`.

## 2. Alət: `tools/release-sign`

Yalnız .NET-in öz kriptoqrafiyası (`System.Security.Cryptography`), heç bir paket yoxdur. Əmrlər:

| Əmr | Nə edir | Harada |
|---|---|---|
| `keygen --name current --out <qovluq>` | Açar cütü: `<ad>.private.pem` (parolla şifrəli, AES-256, PBKDF2-SHA256 600 000) və `<ad>.public.pem` | Oflayn kompüter |
| `check-key --key <ad>.private.pem` | Ehtiyat nüsxə açılırmı, hansı açardır | Oflayn kompüter |
| `manifest --package ... --component ... --platform ... --key-set ... --version ... --min-agent-version ...` | Manifest yazır | CI |
| `sign --manifest <m> --package <p> --key <ad>.private.pem` | **Production:** paketin SHA-256-sını manifestlə tutuşdurur, sonra imzalayır. Staging manifestini və production dəstində olmayan açarı rədd edir | Oflayn kompüter |
| `sign-staging --dir <artefakt qovluğu>` (və ya `--manifest ... --package ...`) | **Staging:** qovluqdakı hər staging manifestini staging açarı ilə imzalayır; production manifestinə toxunmur | Layihə sahibinin iş kompüteri |
| `verify --manifest <m> --package <p> --key-set staging\|production [--installed X] [--withdrawn SHA]` | O dəstin kioskunun etdiyi yoxlamanı edir (və ya `--public-key` ilə istənilən açarla) | Hər yerdə |
| `check-build --dir <publish qovluğu> --key-set ...` | Build-də yalnız öz dəstinin olduğunu yoxlayır | CI |

Production parolu həmişə terminalda soruşulur (ekranda görünmür); əmr arqumenti və ya mühit dəyişəni kimi verilmir — onlar shell tarixçəsinə və proses siyahısına düşür. Parol ən azı 16 simvoldur. Mövcud açar faylının üstünə yazılmır. Domain-ə qoşulmuş Windows hesabında alət xəbərdarlıq verir.

**Aləti oflayn kompüterə aparmaq** (bir dəfə, internetli kompüterdə, repodan):

```powershell
dotnet publish tools/release-sign -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o D:\release-sign
# Linux üçün: -r linux-x64
```

`D:\release-sign\release-sign.exe` faylını açar olmayan USB ilə oflayn kompüterə köçürün. .NET quraşdırmaq lazım deyil.

## 3. Açar cütlərinin yaradılması (layihə sahibi, bir dəfə)

**Nə lazımdır:**

- **Oflayn kompüter** — şəxsi noutbuk. Şirkətin domeninə qoşulmuş (korporativ) kompüter **olmaz**: domen administratorları ona uzaqdan çıxa bilir. İdeal: imzalama vaxtı Wi-Fi və kabel söndürülür.
- **İki şifrəli USB daşıyıcı** — əsas və ehtiyat. Windows Pro-da: USB-yə sağ klik → **BitLocker-i aç** → parol → bərpa açarını kağıza yazın (USB-də və kompüterdə saxlamayın).
- **Parol meneceri** (məs. telefonunuzdakı) — iki açarın parolu və BitLocker parolları üçün. Parollar USB-lərdə saxlanılmır.

**Addımlar:**

1. Kompüterin internetini söndürün (Wi-Fi off, kabel çıxarılıb).
2. Şifrəli **əsas** USB-ni taxın və açın (məs. `E:`).
3. Cari açarı yaradın:
   ```powershell
   D:\release-sign\release-sign.exe keygen --name current --out E:\neoprint-keys
   ```
   Parolu iki dəfə yazın (≥ 16 simvol, parol menecerinə "NeoPrint release current" adı ilə yazın). Çıxışda `Key id` görünür — onu da qeyd edin.
4. Növbəti (ehtiyat rotasiya) açarını yaradın — **başqa parolla**:
   ```powershell
   D:\release-sign\release-sign.exe keygen --name next --out E:\neoprint-keys
   ```
5. `E:\neoprint-keys` qovluğunu şifrəli **ehtiyat** USB-yə köçürün (məs. `F:\neoprint-keys`).
6. Ehtiyat nüsxənin açıldığını yoxlayın:
   ```powershell
   D:\release-sign\release-sign.exe check-key --key F:\neoprint-keys\current.private.pem
   D:\release-sign\release-sign.exe check-key --key F:\neoprint-keys\next.private.pem
   ```
   Hər ikisi `OK: key ... opens` deməlidir və id-lər 3–4-cü addımdakı ilə eyni olmalıdır.
7. **Yalnız** `current.public.pem` və `next.public.pem` fayllarını açar olmayan USB-yə köçürün. `*.private.pem` fayllarını heç yerə köçürməyin.
8. USB-ləri çıxarın. Əsas USB — evdə (məs. seyf), ehtiyat USB — **başqa binada** (məs. etibarlı yaxının yanında və ya bank seyfi). İkisi bir yerdə olmamalıdır: yanğın və ya oğurluq ikisini birlikdə aparmasın.
9. İki public açarın **mətnini** göndərin (`-----BEGIN PUBLIC KEY-----` ilə başlayan 4 sətir; bunlar gizli deyil). Onlar `ReleaseKeys.production.cs`-ə yazılır və növbəti production build-i ilə kiosklara gedir.

**Son tarix:** M19-dan (golden image) əvvəl — pilot kiosku production build ilə qurulur və açarsız heç bir yeniləməni qəbul etmir (`PILOT.md`, bloklayıcı 12).

**Qadağandır:** özəl açarı OneDrive / Google Drive / Dropbox qovluğunda saxlamaq, e-poçt və ya mesajla göndərmək, ekran şəkli çəkmək, repoya qoymaq (`*.pem` `.gitignore`-dadır, amma buna güvənməyin), Claude-a göstərmək.

## 4. Hər buraxılışda imzalama

1. Teq qoyulur (`vX.Y.Z`) → GitHub **Actions → "Release packages"** avtomatik işləyir (və ya **Run workflow** ilə versiya verilir). Run-ın xülasəsində `SHA256SUMS` çap olunur.
2. İnternetli kompüterdə run-ın **production** artefaktlarını (`kiosk-release-<versiya>-win-x64-production`, `...-linux-x64-production`) yükləyin, açar olmayan USB-yə köçürün.
3. Oflayn kompüterdə (internet söndürülmüş) əsas açar USB-sini açın və hər manifesti imzalayın:
   ```powershell
   cd G:\kiosk-release-0.8.0-linux-x64-production
   D:\release-sign\release-sign.exe sign --manifest neoprint-kiosk-agent-0.8.0-linux-x64-production.tar.gz.manifest.json --package neoprint-kiosk-agent-0.8.0-linux-x64-production.tar.gz --key E:\neoprint-keys\current.private.pem
   ```
   Alət əvvəl paketin SHA-256-sını manifestlə tutuşdurur (uyğun deyilsə imzalamır), sonra xülasəni göstərir — **SHA-256-nı CI run-ın xülasəsindəki ilə müqayisə edin**, sonra parolu yazın. `.sig` faylı manifestin yanında yaranır.
4. Yoxlayın — kioskun öz açarları ilə:
   ```powershell
   D:\release-sign\release-sign.exe verify --manifest neoprint-kiosk-agent-0.8.0-linux-x64-production.tar.gz.manifest.json --package neoprint-kiosk-agent-0.8.0-linux-x64-production.tar.gz --key-set production
   ```
   `OK: Agent 0.8.0 for linux-x64, key ...` olmalıdır.
5. Açar USB-sini çıxarın. Paket + manifest + `.sig` admin panelə yüklənir (M16).

Bir buraxılışda 6 production paketi var (3 komponent × 2 platforma) — 6 dəfə `sign`.

### Staging paketləri (test serveri, development)

Ən sadəsi — yuxarıdakı "Staging: sürətli başlanğıc" skripti. Əl ilə, layihə sahibinin iş kompüterində (staging açarı orada), repodan:

```bash
gh run download <run-id> -n kiosk-release-0.8.0-linux-x64-staging -D "$TEMP/rel"
dotnet build tools/release-sign
dotnet tools/release-sign/bin/Debug/net8.0/release-sign.dll sign-staging --dir "$TEMP/rel"
dotnet tools/release-sign/bin/Debug/net8.0/release-sign.dll verify --key-set staging \
  --manifest "$TEMP/rel/neoprint-kiosk-agent-0.8.0-linux-x64-staging.tar.gz.manifest.json" \
  --package "$TEMP/rel/neoprint-kiosk-agent-0.8.0-linux-x64-staging.tar.gz"
```

Staging açar cütü itərsə və ya sızarsa: `keygen --name current --out %USERPROFILE%\.neoprint\release-keys\staging` (və `next`), public açarlar `ReleaseKeys.staging.cs`-də dəyişdirilir, dev kiosklar yenidən yığılır.

## 5. Açar rotasiyası

Kiosk iki açara etibar edir: **cari** (imzalanır) və **növbəti** (oflaynda gözləyir, istifadə olunmur). Buna görə bir açarın itməsi kioskları yerində yenidən qurmağı tələb etmir.

| Hal | Nə etmək |
|---|---|
| **Cari açar sızıb** (və ya şübhə var) | 1) Cari açarla imzalamağı dərhal dayandırın. 2) Oflayn kompüterdə yeni `next2` açarı yaradın (bölmə 3). 3) `ReleaseKeys.production.cs`: `Current` = köhnə `next`, `Next` = `next2`; yeni Agent və UI buraxılışı **köhnə `next` açarı ilə** imzalanır — kiosklar onu artıq qəbul edir. 4) M16-dan sonra: sızmış açarla imzalanmış və hələ quraşdırılmamış paketlər paneldə geri çəkilir. 5) Bütün kiosklar yeni Agent-ə keçəndən sonra sızmış açar heç bir kioskda qalmır. Risk pəncərəsi: kiosklar yenilənənə qədər. Kiosk paketi yalnız öz backend-imizdən cihaz kimliyi ilə endirir, ona görə hücumçuya açardan əlavə backend-ə nəzarət də lazımdır |
| **Cari açar itib** (sızmayıb) | Yuxarıdakı 2–3 və 5-ci addımlar; təcili geri çəkmə lazım deyil |
| **Növbəti açar sızıb / itib** | Yeni `next2` yaradılır; növbəti buraxılış (cari açarla) `ReleaseKeys.production.cs`-də `Next` = `next2` ilə gedir |
| **Hər iki açar itib** | Kiosklar uzaqdan yenilənə bilmir — yeni açarlarla golden image (M19) və yerində yenidən quraşdırma. Buna görə ehtiyat nüsxə iki ayrı yerdədir |
| **Planlı rotasiya** | 2 ildə bir və ya kriptoqrafiya tövsiyəsi dəyişəndə: cari açarın sızması kimi, amma təcili addımlar olmadan |
| **Parol unudulub** | Açar itmiş sayılır (yuxarıdakı kimi) |

Hər rotasiya `docs/DECISIONS.md`-yə yazılır (tarix, köhnə və yeni `keyId`, səbəb).

## 6. Hələ edilməyənlər

- M16: paketin admin panelə yüklənməsi, `releases` kolleksiyası, geri çəkmə (`IsWithdrawn`) — siyahını kiosk serverdən alır.
- M17: Agent-in updater-i `ReleasePackageCheck`-i işlədir (endir → yoxla → quraşdır).
- `ReleaseKeys.production.cs` hələ boşdur — layihə sahibi açarları yaradıb public açarları göndərənə qədər **heç bir production kiosku paket qəbul etmir** (T-315, M19-dan əvvəl). M16–M18 staging dəsti ilə işləyir.
