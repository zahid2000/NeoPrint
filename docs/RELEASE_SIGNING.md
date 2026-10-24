# Buraxılış paketlərinin oflayn imzalanması

Bu sənəd kiosk buraxılış paketlərinin (UI, Agent, provisioning) necə yığıldığını, necə imzalandığını və yoxlandığını, imza açarlarının necə yaradılıb saxlanıldığını və açar itəndə və ya sızanda nə edildiyini təsvir edir (T-313, `docs/DECISIONS.md` 2026-10-11).

**Əsas qayda:** özəl açar yalnız layihə sahibinin oflayn kompüterində və onun şifrəli ehtiyat daşıyıcılarında olur. Özəl açar CI-ya, repoya, buluda, e-poçta, mesajlaşmaya və Claude-a **heç vaxt** getmir. Repoda yalnız public açarlar olur (`desktop/src/NeoPrint.Kiosk.Core/Updates/ReleaseKeys.cs`).

## 1. Necə işləyir

```
CI (tag v*)  →  paket + manifest + SHA256SUMS  (imzasız artefakt)
                     │
                     ▼  (USB ilə)
Oflayn kompüter  →  release-sign sign  →  manifest.json.sig
                     │
                     ▼
Admin panel (M16)  →  kiosk endirir  →  Agent yoxlayır:
     imza (2 açardan biri) → manifest → komponent / platforma → geri çəkilməyib
     → quraşdırılandan yenidir → Agent versiyası kifayətdir → ölçü + SHA-256
```

| Fayl | Nədir |
|---|---|
| `neoprint-kiosk-<komponent>-<versiya>-<platforma>.zip` / `.tar.gz` | Paket: `app` (UI), `agent`, `provisioning`; Windows-da `zip`, Linux-da `tar.gz` |
| `<paket>.manifest.json` | Komponent, platforma, versiya, `minAgentVersion`, fayl adı, ölçü, SHA-256, commit, yaradılma vaxtı |
| `<paket>.manifest.json.sig` | İmza: `keyId`, `ES256` (ECDSA P-256 + SHA-256), imza (base64) |
| `SHA256SUMS` | Bütün faylların SHA-256-sı (CI run-ın xülasəsində də çap olunur) |

İmzalanan manifestin **dəqiq baytlarıdır** (önündə sabit kontekst sətri ilə — imza başqa sənəd növünə köçürülə bilməz). Paketin özü imzalanmır: manifestdəki SHA-256 onu bağlayır.

**Kiosk paketi rədd edir, əgər:**

| Səbəb (`ReleaseRejection`) | Nə deməkdir |
|---|---|
| `UnknownKey` | Kioskdakı iki açardan heç biri ilə imzalanmayıb (və ya build-də açar yoxdur) |
| `BadSignature` | İmza manifestə uyğun deyil — manifest imzadan sonra dəyişdirilib və ya imza saxtadır |
| `MalformedSignature`, `MalformedManifest` | Fayl oxunmur; manifestdə naməlum və ya çatışmayan sahə var |
| `WrongTarget` | Başqa komponent və ya platforma üçün paket |
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
| `manifest --package ... --component ... --platform ... --version ... --min-agent-version ...` | Manifest yazır | CI |
| `sign --manifest <m> --package <p> --key <ad>.private.pem` | Paketin SHA-256-sını manifestlə tutuşdurur, sonra imzalayır | Oflayn kompüter |
| `verify --manifest <m> --package <p> [--public-key ...] [--installed X] [--withdrawn SHA]` | Kioskun etdiyi yoxlamanı edir; `--public-key` verilməsə kioskun öz açarları ilə | Hər yerdə |

Parol həmişə terminalda soruşulur (ekranda görünmür); əmr arqumenti və ya mühit dəyişəni kimi verilmir — onlar shell tarixçəsinə və proses siyahısına düşür. Parol ən azı 16 simvoldur. Mövcud açar faylının üstünə yazılmır. Domain-ə qoşulmuş Windows hesabında alət xəbərdarlıq verir.

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
9. İki public açarın **mətnini** göndərin (`-----BEGIN PUBLIC KEY-----` ilə başlayan 4 sətir; bunlar gizli deyil). Onlar `ReleaseKeys.cs`-ə yazılır və növbəti kiosk build-i ilə kiosklara gedir.

**Qadağandır:** özəl açarı OneDrive / Google Drive / Dropbox qovluğunda saxlamaq, e-poçt və ya mesajla göndərmək, ekran şəkli çəkmək, repoya qoymaq (`*.pem` `.gitignore`-dadır, amma buna güvənməyin), Claude-a göstərmək.

## 4. Hər buraxılışda imzalama

1. Teq qoyulur (`vX.Y.Z`) → GitHub **Actions → "Release packages"** avtomatik işləyir (və ya **Run workflow** ilə versiya verilir). Run-ın xülasəsində `SHA256SUMS` çap olunur.
2. İnternetli kompüterdə run-ın artefaktlarını (`kiosk-release-<versiya>-win-x64`, `...-linux-x64`) yükləyin, açar olmayan USB-yə köçürün.
3. Oflayn kompüterdə (internet söndürülmüş) əsas açar USB-sini açın və hər manifesti imzalayın:
   ```powershell
   cd G:\kiosk-release-0.8.0-linux-x64
   D:\release-sign\release-sign.exe sign --manifest neoprint-kiosk-agent-0.8.0-linux-x64.tar.gz.manifest.json --package neoprint-kiosk-agent-0.8.0-linux-x64.tar.gz --key E:\neoprint-keys\current.private.pem
   ```
   Alət əvvəl paketin SHA-256-sını manifestlə tutuşdurur (uyğun deyilsə imzalamır), sonra xülasəni göstərir — **SHA-256-nı CI run-ın xülasəsindəki ilə müqayisə edin**, sonra parolu yazın. `.sig` faylı manifestin yanında yaranır.
4. Yoxlayın — kioskun öz açarları ilə:
   ```powershell
   D:\release-sign\release-sign.exe verify --manifest neoprint-kiosk-agent-0.8.0-linux-x64.tar.gz.manifest.json --package neoprint-kiosk-agent-0.8.0-linux-x64.tar.gz
   ```
   `OK: Agent 0.8.0 for linux-x64, key ...` olmalıdır.
5. Açar USB-sini çıxarın. Paket + manifest + `.sig` admin panelə yüklənir (M16).

Bir buraxılışda 6 paket var (3 komponent × 2 platforma) — 6 dəfə `sign`.

## 5. Açar rotasiyası

Kiosk iki açara etibar edir: **cari** (imzalanır) və **növbəti** (oflaynda gözləyir, istifadə olunmur). Buna görə bir açarın itməsi kioskları yerində yenidən qurmağı tələb etmir.

| Hal | Nə etmək |
|---|---|
| **Cari açar sızıb** (və ya şübhə var) | 1) Cari açarla imzalamağı dərhal dayandırın. 2) Oflayn kompüterdə yeni `next2` açarı yaradın (bölmə 3). 3) `ReleaseKeys.cs`: `Current` = köhnə `next`, `Next` = `next2`; yeni Agent və UI buraxılışı **köhnə `next` açarı ilə** imzalanır — kiosklar onu artıq qəbul edir. 4) M16-dan sonra: sızmış açarla imzalanmış və hələ quraşdırılmamış paketlər paneldə geri çəkilir. 5) Bütün kiosklar yeni Agent-ə keçəndən sonra sızmış açar heç bir kioskda qalmır. Risk pəncərəsi: kiosklar yenilənənə qədər. Kiosk paketi yalnız öz backend-imizdən cihaz kimliyi ilə endirir, ona görə hücumçuya açardan əlavə backend-ə nəzarət də lazımdır |
| **Cari açar itib** (sızmayıb) | Yuxarıdakı 2–3 və 5-ci addımlar; təcili geri çəkmə lazım deyil |
| **Növbəti açar sızıb / itib** | Yeni `next2` yaradılır; növbəti buraxılış (cari açarla) `ReleaseKeys.cs`-də `Next` = `next2` ilə gedir |
| **Hər iki açar itib** | Kiosklar uzaqdan yenilənə bilmir — yeni açarlarla golden image (M19) və yerində yenidən quraşdırma. Buna görə ehtiyat nüsxə iki ayrı yerdədir |
| **Planlı rotasiya** | 2 ildə bir və ya kriptoqrafiya tövsiyəsi dəyişəndə: cari açarın sızması kimi, amma təcili addımlar olmadan |
| **Parol unudulub** | Açar itmiş sayılır (yuxarıdakı kimi) |

Hər rotasiya `docs/DECISIONS.md`-yə yazılır (tarix, köhnə və yeni `keyId`, səbəb).

## 6. Hələ edilməyənlər

- M16: paketin admin panelə yüklənməsi, `releases` kolleksiyası, geri çəkmə (`IsWithdrawn`) — siyahını kiosk serverdən alır.
- M17: Agent-in updater-i `ReleasePackageCheck`-i işlədir (endir → yoxla → quraşdır).
- `ReleaseKeys.cs` hələ boşdur — layihə sahibi açarları yaradıb public açarları göndərənə qədər **heç bir paket qəbul olunmur**.
