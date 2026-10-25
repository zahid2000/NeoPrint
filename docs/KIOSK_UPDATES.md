# Kiosk yeniləməsi və uzaq əmrlər (Addım 7B, M17 — T-321)

Bu sənəd kioskun yeni versiyanı necə aldığını, necə yoxladığını, nə vaxt və necə keçdiyini, uğursuzluqda necə geri qayıtdığını və Agent-in beş uzaq əmri necə icra etdiyini təsvir edir. Plan: 7.12 "Yeniləmə", `docs/STEP_7B_PLAN.md` §2.1. Paket formatı və imza: `docs/RELEASE_SIGNING.md`.

## 1. Kim nə edir (plandan fərq — `DECISIONS.md` 2026-10-25)

Plan 7.12 "Agent `kiosks/me/update`-i soruşur, paketi endirir" deyir. Kodda isə cihaz kimliyi (açar, token) yalnız UI-dadır (`/var/lib/neoprint/device`, `700`, `kiosk` istifadəçisi), Agent-də qəsdən heç bir kimlik məlumatı yoxdur. Layihə sahibinin qərarı: **UI endirir, Agent tətbiq edir**, bu şərtlərlə:

| | **UI** (`NeoPrint.Kiosk`, `kiosk` istifadəçisi, backend-in yeganə müştərisi) | **Agent** (`NeoPrint.Kiosk.Agent`, `neoprint` istifadəçisi, kimliksiz) |
|---|---|---|
| Backend | `GET kiosks/me/update`, endirmə, `update-status`, əmrlər, loglar | Backend-ə **heç vaxt** qoşulmur; açar / token saxlamır |
| Paket | Endirir (yarımçıq qalsa davam edir), öz yoxlamasını edir (vaxt itirməmək üçün), boş anı gözləyir | Paketi **öz qorunan qovluğuna** köçürür və yalnız orada yoxlayır: imza, hash, açar dəsti, `NotNewer`, geri çəkilmə, `MinAgentVersion`. UI-nın yaza bildiyi qovluqda yoxlayıb sonra oradan istifadə etmir (TOCTOU) |
| Etibar | Agent-ə yalnız **faylın adını, buraxılış id-sini, manifest və imzanı** (özü yoxlanan mətnlər) verir | UI-nın dediyinə inanmır: versiyanı, komponenti, dəsti manifestdən oxuyur, manifesti imza ilə yoxlayır |
| Keçid | "Yenilənir" ekranını göstərir | Versiya qovluğunu açır, `current`-i atomik keçirir, UI-ı dayandırıb başladır |
| Sağlamlıq | Yeni UI hazır olanda siqnal yazır | Siqnalı gözləyir; gəlməsə geri qaytarır |
| Hesabat | Agent-in nəticəsini backend-ə bildirir (incident backend-də açılır) | Nəticəni öz qovluğuna yazır |

Agent-in imtiyazı artmır: o əvvəlki kimi `neoprint` istifadəçisidir; yeni olan yalnız `/opt/neoprint`-in **öz** alt qovluqlarına yazmasıdır (UI-ın proqram faylları). Geri çəkilmiş paketlərin siyahısı imzasızdır (M15 qərarı) — Agent onu yalnız böyüdür: UI-dan gələn hər siyahı əvvəlkilərlə birləşdirilir, heç nə silinmir.

## 2. Qovluqlar

| Linux | Windows (yoxlanmayıb, T-093) | Sahibi / icazə | Nə |
|---|---|---|---|
| `/opt/neoprint/app/versions/<v>/`, `app/current` → symlink | `C:\Program Files\NeoPrint\app\versions\<v>\`, `current` junction | `neoprint`, `755` | UI |
| `/opt/neoprint/agent/versions/<v>/`, `agent/current` | eyni | `neoprint`, `755` | Agent |
| `/opt/neoprint/provisioning/versions/<v>/`, `provisioning/current` | eyni | **`root`**, `755` | Provisioning skriptləri — root kimi işləyir, ona görə Agent-in yaza bildiyi yerdə deyil |
| `/var/lib/neoprint/updates/` | `C:\ProgramData\NeoPrint\updates\` | `kiosk:neoprint`, `2770` | UI-nın yazdığı: `downloads/`, `requests/`, `health/` |
| `/var/lib/neoprint/agent/` | `C:\ProgramData\NeoPrint\agent\` | `neoprint:kiosk`, `750` | Agent-in yazdığı, UI-ın oxuduğu: `state.json`, `journal.json`, `results/` |
| `/var/lib/neoprint/agent/incoming/` | eyni | `neoprint`, `700` | Agent-in paket nüsxəsi — yoxlama yalnız burada |
| `/var/log/neoprint/` | `C:\ProgramData\NeoPrint\logs\` | `kiosk:neoprint`, `770` | `kiosk-YYYYMMDD.log`, `agent-YYYYMMDD.log` |

Cihaz kimliyi, spool, sayğac, konfiqurasiya keşi və loglar `/var/lib/neoprint` / `/var/log/neoprint`-dədir — yeniləmə onlara toxunmur (yenidən qeydiyyat yoxdur).

## 3. Yeniləmə axını

1. **Soruşmaq.** UI hər `Update.CheckIntervalMinutes`-də, hub-da `UpdateAvailable` gələndə və `UpdateNow` əmrində `GET kiosks/me/update` soruşur. Hər komponent üçün təklif quraşdırılmış versiyadan (Agent-in `state.json`-u) yenidirsə, işə başlayır.
2. **Endirmək** (`Downloading`). `updates/downloads/<releaseId>.part`-a; kəsilsə (elektrik, şəbəkə) növbəti cəhd `Range` ilə qaldığı yerdən davam edir. Bitəndə SHA-256 yoxlanılır və fayl `<releaseId>.pkg` olur. İstəyə görə sürət limiti (`Kiosk:UpdateDownloadKbps`).
3. **UI yoxlaması** (`Ready`). `ReleasePackageCheck` (kioskun öz dəsti) — uyğun deyilsə fayl silinir, status `Failed` (səbəb ilə).
4. **Boş an** (`WaitingForIdle`). Yeniləmə pəncərəsində (`Kiosk.UpdateWindow`, standart `02:00-05:00`, kioskun yerli saatı) və ya pəncərədən kənar yalnız admin "İndi yenilə" dedikdə və ya yayım **təcili** (təhlükəsizlik) işarələnibsə. Boş an: ekran müştərisizdir (ana ekran, "xidmətdən kənar", "bağlantı yoxdur"), çapda iş yoxdur, ödəniş gedişatda deyil, telefon sessiyası açıq deyil, flash faylı yüklənmir. `Paused` / `OutOfService` kiosk da boş sayılır (növbədəki ödənilmiş işlər spool-da qalır və yenidən başladıqdan sonra davam edir).
5. **Gözləmə bitdi** (`Draining`). `Update.MaxIdleWaitMinutes` (standart 30) keçib, hələ müştəri varsa: cari müştərinin ödənişinə və çapına **toxunulmur**, yeni müştəri isə "Kiosk bir neçə dəqiqəyə yenilənir" ekranını görür. Unudulmuş sessiya yeniləməni saxlaya bilməz: axın 90 saniyə toxunuşsuz qalanda sıfırlanır (`Kiosk.IdleResetSeconds`), ödənişin öz bitmə vaxtı var, telefon sessiyası 10 dəqiqəyə bağlanır.
6. **Agent-ə sorğu** (`Applying`). UI `updates/requests/<id>.json` yazır (atomik) və "Yenilənir" ekranını göstərir.
7. **Agent yoxlaması.** Paket `agent/incoming/`-ə köçürülür, orada yoxlanılır (bölmə 1). Disk yeri: açılmış paket üçün `3 × ölçü + 200 MB` boş yer. Rədd olunarsa — nəticə `Refused` (səbəb açarı ilə), heç nə dəyişmir.
8. **Keçid.** Paket `versions/<v>.partial/`-a açılır (yol `..`, mütləq yol və kənara baxan link rədd olunur), sonra `versions/<v>`-ə köçürülür. Jurnal (`journal.json`, atomik): `Switching`. UI dayandırılır, `current` atomik dəyişir (`current.next` symlink → `rename`), jurnal: `HealthCheck` (son vaxt), UI başladılır.
9. **Sağlamlıq yoxlaması** (`HealthCheck`). Yeni UI `Update.HealthTimeoutMinutes` (standart 5) ərzində: token alıb konfiqurasiyanı yükləməli, ən azı bir heartbeat göndərməli, printer aşkarlamasını keçməli, təhlükəsiz rejimdə olmamalıdır → `updates/health/app-<v>` yazır.
10. **Uğur** (`Succeeded`): `state.json`-da yeni versiya, əvvəlki saxlanılır; ən son `Update.KeepVersions` (standart 3) versiyadan artığı silinir — **cari və əvvəlki heç vaxt**.
11. **Uğursuzluq** (`RolledBack`): `current` əvvəlkinə qaytarılır, köhnə UI başladılır və nəticəni bildirir → backend **High** incident açır (`KioskUpdateFailed`; panel + email). Bu versiya həmin kioskda bir daha quraşdırılmır (Agent-in `failed` siyahısı və backend təklifi), növbəti yeni versiya gələnə qədər.

**Elektrik kəsilməsi.** Agent hər açılışda jurnala baxır: `Switching` — `current` hələ köhnədirsə keçid ləğv olunur (nəticə `Failed`, `updates.interrupted`, UI yenidən istəyəcək), yenidirsə `HealthCheck` kimi davam edir; `HealthCheck` — sağlamlıq yoxlaması tam müddətlə yenidən başlayır, sonda ya təsdiqlənir, ya geri qayıdır. `current` mövcud olmayan qovluğa baxırsa — əvvəlki versiyaya qaytarılır. `.partial` qovluqları silinir. Köhnə versiya sağlamlıq yoxlaması keçənə qədər silinmir.

## 4. Agent-in özünü yeniləməsi və provisioning

- **Agent** paketi eyni yoxlamadan keçir, `agent/versions/<v>`-ə açılır, `agent/current` keçirilir, jurnal `HealthCheck`, Agent çıxır; xidmət meneceri (systemd `Restart=always`) yenisini başladır. Yeni Agent jurnalda özünü görüb 30 saniyə işlədikdən sonra təsdiqləyir. Yeni Agent başlaya bilmirsə: **Agent paketindən kənar** qoruyucu skript (`neoprint-agent-guard`, provisioning quraşdırır, systemd `ExecStartPre`) hər başlanğıcı sayır; 3 uğursuz başlanğıcdan sonra `agent/current`-i əvvəlkinə qaytarır, köhnə Agent nəticəni `RolledBack` yazır. Windows-da Agent-in özünü yeniləməsi real cihaz olmadan yoxlana bilmir — orada Agent bu sorğunu `updates.notSupportedHere` ilə rədd edir (T-093).
- **Provisioning** skriptləri root kimi işləyir, ona görə Agent (`neoprint`) onları özü quraşdırmır və işlətmir: bu, Agent-in imtiyazını root-a qaldırardı. Agent paketi öz yoxlamasından keçirir və root-a məxsus köməkçiyə verir: `sudo -n /usr/local/lib/neoprint/neoprint-reprovision install <paket> <manifest> <imza>` (sudoers-də Agent üçün yeganə provisioning qaydası). Köməkçi Agent-in **root-a məxsus nüsxəsini** (`/usr/local/lib/neoprint/agent`) işlədir. Bu nüsxə paketi build-ə yazılmış açarlarla **özü yenidən yoxlayır**, root-a məxsus `provisioning/versions/<v>`-ə açır, `current`-i keçirir və `provision.sh --repair`-i işlədir. Skriptlərə yalnız `--repair` və ya `--dry-run` ötürülə bilər. Uğursuz olsa `current` geri qaytarılır, nəticə `Failed`. `ReapplyProvisioning` → `neoprint-reprovision reapply`. Root-un Agent nüsxəsini yalnız texniki işçinin işlətdiyi provisioning yeniləyir, Agent-in istədiyi `--repair` heç vaxt yeniləmir — Agent öz binary-sini root-un əlinə verə bilməz. `--repair` NeoPrint-in özünü quraşdırmır, qalanını idempotent düzəldir; `--dry-run` heç nə dəyişmir (konteyner kiosku).

## 5. Uzaq əmrlər (`KioskCommand`, M16 endpoint-ləri)

Hamısı UI-ın mövcud əmr kanalından gəlir (hub + `GET kiosks/me/commands`), `ack` / `result` ilə; eyni əmr id-si ikinci dəfə icra olunmur (UI və Agent hər ikisi id-yə görə); audit backend-də (M16).

| Əmr | Nə edir | Nəticə |
|---|---|---|
| `UpdateNow` | Dərhal soruşur, pəncərəni gözləmir, boş anı gözləyir | `updates.result.started` / `updates.result.nothing` |
| `RollbackVersion` | Agent UI-ı saxlanılmış əvvəlki versiyaya keçirir (sağlamlıq yoxlaması ilə), boş anı gözləyir | Agent-in nəticəsi |
| `UploadLogs` | Son 3 günün `kiosk-*.log` və `agent-*.log` faylları təmizlənir, sıxılır, `POST kiosks/me/logs` | Log id-si |
| `ReapplyProvisioning` | `provision.sh --repair`, boş anı gözləyir | Agent-in nəticəsi |
| `RestartAgent` | Agent çıxır, xidmət meneceri yenidən başladır | Yeni Agent başlayanda |

## 6. Loglar

- UI və Agent öz loglarını fayla yazır (öz kodumuz, yeni paket yoxdur): JSON sətirləri, günlük fayl, 7 gün, gündə ən çox 20 MB.
- **Şəxsi məlumat yazılmır və göndərilmir:** yazılarkən və yükləmədən əvvəl təmizlənir — `Bearer …`, JWT, `token` / `password` / `secret` / `key` dəyərləri, telefon nömrələri, e-poçtlar, uzun base64 blokları `[redacted]` olur; fayl məzmunu heç vaxt loga yazılmır. Backend yükləməni açıb **yenidən** təmizləyir (ikinci qat).
- Ölçü limiti: sıxılmış `Logs.MaxUploadMb` (standart 10) — kiosk köhnə sətirlərdən başlayaraq kəsir, backend artığını `413` ilə rədd edir.
- Backend: şifrəli storage (`kiosk-logs/<kiosk>/<id>.log.gz`), `kiosk_logs` kolleksiyası, saxlama `Logs.RetentionDays` (standart 30), sonra gündəlik iş silir. Siyahı / endirmə — `kiosks.logs.download`, kiosk əhatəsinə tabe, endirmə audit-ə yazılır.

## 7. Backend dəyişiklikləri (M17)

- `KioskUpdateStatus`: `WaitingForIdle`, `Draining` əlavə olunur (`STATE_MACHINES.md` "Update").
- `Failed` / `RolledBack` bildirişi → High incident `KioskUpdateFailed` (kiosk + komponent + versiya ilə dedup). Kiosk həmin versiyanı geri qaytarıbsa, o buraxılış ona bir daha təklif olunmur.
- Yayımda **`Urgent`** (təcili, təhlükəsizlik): kiosk pəncərəni gözləmir (boş anı yenə gözləyir).
- Konfiqurasiyada yeniləmə parametrləri: `Kiosk.UpdateWindow`, `Update.CheckIntervalMinutes`, `Update.HealthTimeoutMinutes`, `Update.KeepVersions`, yeni `Update.MaxIdleWaitMinutes`.
- Endirmə `Range` qəbul edir (davam etmək üçün).
- Loglar: `POST kiosks/me/logs`, `GET admin/kiosks/{id}/logs`, `GET admin/kiosks/{id}/logs/{logId}`; `Logs.MaxUploadMb`, `Logs.RetentionDays`.

## 8. Nə yoxlanılır

Testlər: Agent — UI-ın verdiyi dəyişdirilmiş / yanlış imzalı / başqa dəstin / köhnə / geri çəkilmiş paketi rədd edir; paket yoxlamadan sonra UI-ın qovluğunda dəyişdirilsə belə quraşdırılan Agent-in yoxladığı nüsxədir; keçid, sağlamlıq, rollback, jurnaldan bərpa (hər faza), versiya təmizliyi; yol hücumu olan arxiv rədd olunur. UI — boş an qaydaları, gözləmə → qəbul etməmə, pəncərə / təcili / `UpdateNow`, davam edən endirmə, log təmizləmə. Backend — yeni statuslar, incident, təklifdən çıxarma, `Range`, loglar, icazə matrisi.

Real stack (Linux konteyner kiosku, staging açarı): uğurlu yeniləmə; qəsdən xətalı versiya → avtomatik rollback + incident; çap işi gedərkən gözləmə; yarımçıq endirmədən sonra davam; yanlış imzalı / dəyişdirilmiş paket rədd. Windows hissəsi real cihazsız yoxlanmır — `PILOT.md` bloklayıcısı (T-093).
