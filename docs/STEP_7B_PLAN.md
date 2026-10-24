# Addım 7B — plan (T-297)

Tarix: 2026-10-23. Əsas: plan Addım 7 (yeniləmə sistemi, buraxılış), Bölmə 7.11–7.12, `docs/TASKS.md` T-219, T-204
qərarları, `docs/PILOT.md`. **Layihə sahibi 2026-10-23-də təsdiqlədi (T-305): M-lər və ardıcıllıq olduğu kimi; M22 (ad) açılmır
(T-298).** Kod `v0.7.1`-dən (T-299…T-301, T-304) sonra başlayır. Hər M əvvəlki kimi: dizayn qaydası koddan əvvəl, az / en / ru, testlər, real stack, CI, deploy (T-277),
hesabat, təsdiq.

Həcm: **S** — 1–2 iş sessiyası, **M** — 3–4, **L** — 5 və daha çox.

## 1. Artıq nə var (7B sıfırdan başlamır)

- Provisioning skriptləri: `desktop/provisioning/linux/provision.sh`, `desktop/provisioning/windows/provision.ps1`
  (Addım 3; real cihazda yoxlanmayıb — T-093, T-094).
- Enrollment token-lər: backend (`POST/GET/DELETE admin/enrollment-tokens`) və aktivləşmə axını. Admin səhifəsi
  M18-də yoxlanılacaq.
- Agent: yalnız UI watchdog-u (`KioskUiWatchdog`). Updater, rollback, özünü yeniləmə **yoxdur**.
- Uzaqdan əmrlər: `Pause`, `Resume`, `RestartApp`, `Reboot` (7A). Yeniləmə əmrləri yoxdur.
- Test serveri, deploy workflow-u (T-272), test APK-sı (T-287).

## 2. M-lər

| M | Nə | Asılılıq | Layihə sahibindən gözlənilən | Həcm |
|---|---|---|---|---|
| **M15** | **Buraxılış paketi və oflayn imzalama:** CI hər buraxılışa paket (`zip` / `tar.gz`) + manifest (versiya, komponent, platforma, SHA-256, `MinAgentVersion`) yaradır, **imzalamır**; lokal `tools/release-sign` (ECDSA P-256); `docs/RELEASE_SIGNING.md`; Agent-də 2 public açar (rotasiya); imza yoxlaması (`NeoPrint.Kiosk.Core`) — imza / SHA-256 uyğunsuzluğunda paket rədd olunur (test) | — | Bir dəfə: oflayn kompüterdə açar cütünün yaradılması (əmr hazır olacaq), public açarların verilməsi; imzalamanın hansı kompüterdə ediləcəyi | M |
| **M16** | **Backend: buraxılışlar və rollout-lar:** `releases` (paket şifrəli storage-da), rollout (hamısı / seçilmiş / faiz / kanal `pilot` → `stable`, pauza, geri çəkmə), `GET kiosks/me/update`, hub `UpdateAvailable`, əmrlər `UpdateNow`, `RollbackVersion`, `UploadLogs`, `ReapplyProvisioning`, `RestartAgent` (idempotent, audit, kiosk əhatəsi), telemetriyada versiya / yeniləmə statusu; icazə matrisinə yeni endpoint-lər | M15 | — | L |
| **M17** | **Agent updater:** endir → SHA-256 + imza → `Kiosk.UpdateWindow`-da, queue və ödəniş boş olanda keçid (`current` symlink / junction) → sağlamlıq yoxlaması (`Update.HealthTimeoutMinutes`) → uğursuzsa avtomatik rollback + incident (High); `Update.KeepVersions`; Agent özünü yeniləmə (`Agent.Updater`); provisioning yeniləməsi (`ProvisioningVersion`); log yükləmə. Yeniləmədən sonra kimlik, spool, sayğac qorunur (test). Real stack: Linux konteyner kioskunda iki versiya, qəsdən xətalı versiya → rollback | M15, M16 | — | L |
| **M18** | **Admin: Buraxılışlar, rollout-lar, Enrollment token-lər; kiosk detalında versiya / yeniləmə statusu, log yükləmə, yeniləmə əmrləri.** DoD: yeni versiya pilot kanalına → kiosk boş anda **yenidən qeydiyyatsız** yenilənir; xətalı versiya → rollback + incident; enrollment token ilə yeni kiosk heç kim ekrana baxmadan aktivləşir | M16, M17 | — | M |
| **M19** | **Golden image və provisioning:** `prepare-image.sh` / `.ps1` (kimlik, keş, `machine-id` təmizlənir; Windows `sysprep /generalize`), `--offline-package`, `--channel`; Linux skriptinin idempotentliyi konteyner / VM-də CI testi; Windows — Pester yoxlama skripti | M15 | Windows IoT Enterprise VM və ya cihaz (T-093), Ubuntu 24.04 kiosk cihazı + flaş (T-094) — cihazda yoxlama layihə sahibi ilə | M |
| **M20** | **Staging compose + T-169:** ayrıca OpenBao ilə staging; "Production-da məcburi açar silinəndə API başlamır" real yoxlanır. **Staging və production bazası təmiz başlayır:** development məlumatı (test kioskları, dev hesabları, test sifarişləri) köçürülmür — yalnız seed (rollar, tənzimləmələr, hüquqi sənədlər, ilk SuperAdmin) (T-300). **Paralel:** bank və e-kassa provayder araşdırması (T-302, `docs/PROVIDERS.md`) — implementasiya M25–M26-da | — | — | S |
| **M21** | **Domen, TLS, production SMTP:** production bazası təmiz — yalnız seed (T-300); test serveri `sslip.io`-dan domenə (T-272), SPF / DKIM / DMARC, SMTP açarları OpenBao-da; qeydiyyat kodu, qəbz və incident emailinin spam-a düşmədən çatması (MT-06 №8) | — | **Domen**, SMTP provayderi və hesabı, DNS-ə giriş. **Ad qərarı bu M-dən əvvəl** (domen və göndərən ünvan ondan asılıdır) — M21-ə çatanda Claude xatırladır | S |
| **M22** | **Ad dəyişdirmə** (yalnız ad qərarı gəlsə — bölmə 4) | Ad qərarı | Yeni ad, domen, loqo. **Hələlik açılmır (T-298).** Son tarix: M21-dən və M23-dən əvvəl; Google Play-ə ilk yükləmədən (T-275) əvvəl **mütləq** | S–M |
| **M23** | **Vizual yenidən dizayn (T-022) və açılış ekranı / ikon (T-201):** token faylları (admin, mobil, kiosk) və ümumi komponentlər; Android splash, iOS `LaunchScreen`, tətbiq ikonu; kiosk ekranları | M22 (ad qərarı varsa) | **Dizayn mənbəyi: Figma-dakı "ZSPrint Kiosk" faylı** (link layihə sahibindən, T-303); bölmə 3-dəki nümunələr bu fayldan götürülür | M–L |
| **M24** | **Sənədlər:** `ADMIN_GUIDE.md`, `OPERATOR_GUIDE.md` (ekran görüntüləri ilə, redizayndan sonra), `PILOT.md` yekun (T-171 xatırlatması), README; hüquqi qaralamaların son redaktəsi (T-162) | M23 | Hüquqşünasın rəyi (T-162), `PILOT.md`-dəki hüquqi sual | M |
| **M25** | **Real bank adapteri (sandbox):** QR / kart ödənişi, callback imzası, refund; ləğvdə **"Karta qaytar"** (T-242); telefonla ödəniş | — | **Provayder seçimi, sandbox sənədi və açarları** (OpenBao-ya özünüz yazırsınız) | L |
| **M26** | **Real e-kassa adapteri (sandbox):** fiskal çek, ləğv, uzlaşdırma | M25 (ödəniş axını) | **Provayder seçimi, sandbox sənədi və açarları** | M–L |
| **M27** | **7B-nin bağlanması → `v1.0.0`:** buraxılış artefaktları — backend Docker image; desktop (UI + Agent) `win-x64` və `linux-x64` self-contained **imzalı** paketlər + provisioning; Flutter APK / AAB (T-275); iOS build (T-273, Apple hesabı olsa); admin Docker image; inteqrasiya testləri, icazə matrisi (7B endpoint-ləri), DoD real stack-də, CI, teq | hamısı | — | M |

**Ardıcıllıq:** M15 → M16 → M17 → M18 (yeniləmə zənciri, ardıcıl) → M19 → M20 → M21 → M22 → M23 → M24 →
M25 → M26 → M27. M20 və M21 girişi gələn kimi istənilən vaxt araya alına bilər. M22 (ad) **M23-dən və Play-ə ilk
yükləmədən (T-275) əvvəl** olmalıdır. Real bank / e-kassa adapterləri (M25, M26) sandbox sənədinə görə **sonda**
qalır (T-204). Sənəd gec gəlsə, M27-nin artefakt hissəsi onlarsız hazırlanır, `v1.0.0` isə adapterləri gözləyir.

**Paralel, layihə sahibinin əlində (pilot-bloklayıcılar, `PILOT.md`):** S24-də MT-07 / MT-08 (T-186, T-225),
real printer (T-092), çapın ortasında elektrik kəsilməsi (T-089), Windows kiosk (T-093, T-220), admin testləri başqa
kompüterdə (T-171), iOS (T-187, T-273).

**Qeyd (2026-10-25, layihə sahibinin qərarı):** 7B sonunda API tək nüsxədir; yaddaşdakı sorğu limitləri (qlobal, giriş, refresh, anonim müraciət) bir neçə nüsxəyə keçiddə Redis-ə köçürülür — Faza 2 (Addım 8) işi. Production buraxılış açarları (T-315) M19-dan əvvəl hazır olmalıdır; M16–M18 staging açar dəsti ilə işləyir (T-316).

## 2.1. M16–M18 — dəqiq bölgü (T-317, 2026-10-25)

Yeniləmə zənciri üç M-ə bölünür ki, hər M sonunda **real işləyən** bir şey olsun və panel heç vaxt hələ işləməyən düyməni göstərməsin.

| | **M16 — backend + "Buraxılışlar" səhifəsi** | **M17 — Agent: yeniləmə və uzaq əmrlər** | **M18 — kiosk detalı + uçdan-uca DoD** |
|---|---|---|---|
| Backend | `releases` (paket + manifest + imza; yükləmədə imza **serverdə yoxlanılır**, server yalnız öz dəstini qəbul edir — `Releases:KeySet`, test serveri `staging`); paket şifrəli storage-da; kanal `pilot` → `stable` (promote); geri çəkmə. `release_rollouts`: hədəf — hamısı / seçilmiş kiosklar / faiz / kanal; pauza, davam, geri çəkmə, tamamlama. Kioskun kanalı (`UpdateChannel`). `GET kiosks/me/update` (hədəf versiyalar + manifest + imza + geri çəkilmiş SHA-256-lar), `GET releases/{id}/download` (yalnız hədəflənmiş kiosk), hub `UpdateAvailable`. Əmr növləri `UpdateNow`, `RollbackVersion`, `UploadLogs`, `ReapplyProvisioning`, `RestartAgent` + admin endpoint-ləri (idempotent, audit, kiosk əhatəsi). `POST kiosks/me/update-status`; heartbeat-də Agent / provisioning versiyası. `GET admin/kiosks/versions` | `POST kiosks/me/logs` (sıxılmış arxiv, ölçü limiti, şifrəli, saxlama müddəti), admin log siyahısı / endirmə | — |
| Kiosk / Agent | — (UI-də dəyişiklik yoxdur) | Updater: soruş / `UpdateAvailable` → endir → `ReleasePackageCheck` → `Kiosk.UpdateWindow` + boş növbə → keçid (`current` symlink / junction) → sağlamlıq yoxlaması → uğursuzsa rollback + incident (High); `KeepVersions`; Agent özünü yeniləmə; provisioning yeniləməsi; əmrlərin icrası (5 əmr); status hesabatı. Kimlik, spool, sayğac qorunur (test) | — |
| Admin panel | **"Buraxılışlar"** səhifəsi (İdarəetmə qrupu): paket yükləmə (paket + manifest + `.sig`), siyahı (komponent, platforma, versiya, dəst, kanal, imza statusu, geri çəkilib), promote, geri çəkmə; rollout-lar (yarat, pauza, davam, geri çək, tamamla, hədəf sayı); kiosk versiyaları cədvəli (UI / Agent / provisioning, yeniləmə statusu, son xəta) | — | Kiosk detalında: versiyalar, yeniləmə statusu və tarixçəsi, **əmr düymələri** ("İndi yenilə", "Əvvəlki versiyaya qaytar", "Logları yüklə", "Provisioning-i yenidən tətbiq et", "Agent-i yenidən başlat"), log endirmə; enrollment token səhifəsinin son baxışı |
| İcazələr | `releases.view`, `releases.manage` — **qlobal** (`GlobalOnly`, kiosk əhatəli hesab görmür); əmrlər `kiosks.commands.update`, `kiosks.logs.download` — kiosk əhatəsinə tabe; icazə matrisi yeni endpoint-lərlə | Kiosk endpoint-ləri matrisdə | Panel hadisələri matrisdə |
| Real stack sübutu | Staging imzalı paket test serverinə yüklənir, saxta / başqa dəstin paketi rədd olunur; rollout yaradılır, kiosk `GET kiosks/me/update`-də onu görür, hədəfdən kənar kiosk görmür; geri çəkilmiş paket təklif olunmur | Linux konteyner kioskunda iki versiya; qəsdən xətalı versiya → rollback + incident | Yeni versiya pilot kanalına → kiosk boş anda **yenidən qeydiyyatsız** yenilənir; xətalı versiya → rollback + incident; enrollment token ilə yeni kiosk heç kim ekrana baxmadan aktivləşir |

**Qeyd:** anonim `GET releases/latest` (provisioning skripti paketi oradan götürür) M19-a keçirildi — onu işlədən skriptlə birlikdə yoxlansın, o vaxta qədər anonim paket endirmə yolu açıq qalmasın. M16-da əmr endpoint-ləri backend-də olur, amma panel düymələri M18-dədir — Agent onları M17-də öyrənir; o vaxta qədər hələ işləməyən düymə göstərilmir. Logların yüklənməsi (kioskdan backend-ə) Agent-in log yığımı ilə birlikdə M17-yə keçirildi — uçdan-uca yoxlana bilsin.

## 3. Redizayn üçün lazım olan nümunələr (T-022, T-201)

1. **Rəng palitrası:** əsas (brend) rəng və 1–2 köməkçi rəng — hex kodu ilə və ya bəyəndiyiniz bir şəkil / sayt.
2. **Loqo:** SVG (və ya ən azı 1024 px PNG), açıq və tünd fon üçün variant; yalnız nişan (ikon) variantı.
3. **Tətbiq ikonu:** 1024 × 1024 PNG; Android adaptiv ikonu üçün ayrıca ön plan (şəffaf fon) və fon rəngi.
4. **Açılış ekranı:** fon rəngi + loqo (Android 12+ splash dairəvi nişanı kəsir — nişan ortada, kənarda boşluq).
5. **Admin paneli:** bəyəndiyiniz 2–3 nümunə — sidebar, dashboard kartları, cədvəl.
6. **Mobil tətbiq:** 2–3 ekran nümunəsi (ana ekran, fayl siyahısı, ödəniş).
7. **Kiosk ekranı:** 1 nümunə (böyük toxunma düymələri, uzaqdan oxunan mətn).
8. **İllüstrasiyalar:** üslub (xətti / düz / 3D) — boş vəziyyət, xəta, uğur üçün; lisenziyası ilə.
9. **Şrift:** Inter qalır, yoxsa başqası (lisenziya — OFL olmalıdır).
10. **Qaranlıq rejim:** lazımdırmı (admin / mobil).

Dəyişiklik əsasən token fayllarında və ümumi komponentlərdə olacaq (`DESIGN_SYSTEM.md`, `tools/design-lint`).

## 4. Ad dəyişdirmə — tapşırıq təklifi

**Nə vaxt:** ad qərarı gələn kimi, **Google Play-ə ilk yükləmədən əvvəl** (Play-də `applicationId` sonradan dəyişmir,
dəyişsə — yeni tətbiq olur) və redizayndan əvvəl (loqo, ikon, splash bir dəfə edilsin).

**Toxunulur (istifadəçi görür və ya xarici identifikatordur):**
- UI mətnləri (az / en / ru): tətbiq adı, başlıqlar, admin `<title>`, PWA manifest (`name`, `short_name`), kiosk
  pəncərə başlığı və ekranları, email şablonları və göndərən adı, qəbz PDF-i, push başlıqları.
- Android: `applicationId` / `namespace` (`az.neoprint.neoprint`), launcher adı, ikon; Firebase-də yeni Android
  tətbiqi qeydiyyatı və yeni `google-services.json` (gitignore-dadır).
- iOS: `PRODUCT_BUNDLE_IDENTIFIER`, görünən ad, APNs.
- Deep link sxemi (`neoprint://`) — yeni sxem əlavə olunur, köhnəsi **alias kimi qalır** (çap olunmuş QR-lar).
- Domen, email göndərən domen (SPF / DKIM), test serverinin adı.
- Hüquqi sənədlər, `PILOT.md`, README, mağaza mətnləri.

**Toxunulmur (daxili identifikatorlar — dəyişmək risk verir, istifadəçiyə heç nə vermir):**
- .NET solution / namespace-lər (`NeoPrint.*`), Dart paket adı, Angular layihə adı.
- Mongo bazasının adı, kolleksiyalar, RabbitMQ növbələri, OpenBao yolları (`neoprint/…`) — dəyişsə secret-lər və
  data köçürülməlidir.
- Kiosk cihazındakı yollar (`/var/lib/neoprint`, `C:\ProgramData\NeoPrint`), systemd / Windows servis adları,
  `neoprint` istifadəçisi — quraşdırılmış kiosklarda miqrasiya tələb edər.
- Cookie / `localStorage` açarları (`neoprint.admin.session`), JWT issuer / audience — dəyişsə hamı yenidən daxil olur.
- Docker konteyner / image adları — istəyə görə, ayrıca və sonra.

**Risklər:**
- Yeni `applicationId` = yeni tətbiq: test telefonlarında köhnəsi silinib yenisi quraşdırılmalıdır, köhnə FCM
  token-ləri işləməz (T-283 onları özü təmizləyir).
- Çap olunmuş QR-lar və linklər köhnə sxem / domenlə qalır — alias saxlanılmasa, sınır.
- Email domeni dəyişəndə SPF / DKIM yenidən qurulur, ilk günlər spam riski.
- Yarımçıq dəyişiklik (bir yerdə köhnə ad) — yoxlama: köhnə adın UI-da qalmadığını göstərən `grep` + walk.

**Həcm:** S–M. **Sübut:** walk az / en / ru-da köhnə ad yoxdur; APK-nın yeni `applicationId` ilə quraşdırılması;
köhnə `neoprint://` linkinin işləməsi; CI yaşıl.
