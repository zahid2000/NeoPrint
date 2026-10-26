# NeoPrint — pilot

Pilot: 1 kiosk, 1 məkan, 2–3 həftə (plan Bölmə 12, Addım 7).

Bu sənədin quraşdırma yoxlama siyahısı, gündəlik baxış siyahısı, uğur meyarları
və pilotdan sonra qərar şablonu Addım 7B-də yazılır (T-219). İndi burada yalnız
**pilot-bloklayıcılar** saxlanılır ki, heç biri unudulmasın.

## Pilot-bloklayıcılar

Pilot aşağıdakıların hamısı bağlanmadan başlamır. Hər sətrin mənbəyi
`docs/TASKS.md`-dəki tapşırıqdır; addımlar `docs/MANUAL_TESTS.md`-dədir.

| # | Nə | Kim | Nə lazımdır | Tapşırıq | Addımlar | Vəziyyət |
|---|---|---|---|---|---|---|
| 1 | Mobil tətbiqin real telefonda əl ilə yoxlanması: kamera ilə QR, push bildirişi, sənəd skanı | Layihə sahibi | Samsung S24 (USB) | T-186, T-225 | MT-07, MT-08 — "Harada yoxlanır" sütununda **"Yalnız real telefonda"** yazılmış sətirlər (MT-07: №6, 11–13, 19–25 və bir neçəsinin push / kamera hissəsi; MT-08: №5–7, 16–19, 22, 25 və bir neçəsinin kamera hissəsi) | Açıq — Addım 7-nin sonunda |
| 2 | Real printer: aşkarlama, imkanlar, test çap, hər format qrupu, printer telemetriyası (SNMP / WMI) | Layihə sahibi + Claude | Pilot printeri (model və qoşulma növü layihə sahibindən) | T-092 | MT-02 | Açıq — printer lazımdır |
| 3 | **Windows kiosk provisioning-i (pilot cihazı — T-329):** nüsxə yoxlaması (`check-windows.ps1`), Shell Launcher, Keyboard Filter, autologon, Agent servisi, Firewall, golden image (`sysprep`), klona `enroll.ps1` ilə açar; 1280×1024 ekranda toxunma ilə sifariş axını | Layihə sahibi + Claude | Touch-pie 19" monoblok, Windows 10 IoT Enterprise LTSC 2021 lisenziyası | T-093, T-329, T-331 (M19b) | MT-03 | Açıq — cihaz lazımdır |
| 4 | Linux kiosk cihazı və real flaş: avtomount, fayl siyahısı, reboot-dan sonra cage | Layihə sahibi + Claude | Ubuntu 24.04 kiosk cihazı, USB flaş | T-094 | MT-04 | **Pilot üçün tələb deyil** — pilot cihazı Windows-dur (T-329); Linux provisioning konteynerdə sınanır (`tools/provision-test`), real Linux cihazı Faza 2-də |
| 5 | Çapın ortasında elektrik kəsilməsi | Layihə sahibi | Real kiosk + printer | T-089 | MT-01 | Açıq — cihaz lazımdır |
| 6 | Real ödəniş və e-kassa provayderləri (sandbox), telefonla ödəniş | Layihə sahibi + Claude | Provayder seçimi və sandbox sənədi (layihə sahibində) | T-099, T-219 | MT-06 | Açıq — Addım 7B |
| 7 | Admin test dəstinin başqa kompüterdə işlədilməsi (bu maşında `vitest` forks işə düşmür; CI yaşıldır) | Layihə sahibi | Başqa Windows / Linux kompüteri | T-171 | `docs/DEV_ENVIRONMENT.md` — admin testləri | Açıq |
| 8 | "Production-da məcburi açar silinəndə API başlamır" — ayrıca (staging) OpenBao-da | Claude | Staging mühiti | T-169 | — | Açıq — Addım 7B |
| 9 | Vizual yenidən dizayn, açılış ekranı və ikon (dizayn mənbəyi: Figma "ZSPrint Kiosk", T-303) | Layihə sahibi (nümunələr) + Claude | Nümunələr | T-022, T-201 | — | Açıq — Addım 7B |
| 10 | Production SMTP: göndərən domen, SPF / DKIM (və DMARC) — istifadəçi emaili (qeydiyyat kodu, qəbz) və incident emaili (Critical / High, kioskun əhatəsindəki hesablara) spam-a düşmədən çatmalıdır | Layihə sahibi (domen, SMTP provayderi) + Claude | Domen, SMTP hesabı; OpenBao-da SMTP açarları | T-219, T-241 | MT-06 №8 (email hissəsi) | Açıq — Addım 7B |
| 11 | iOS: test build-i iPhone-da (PIN, Face ID, Keychain, giriş, sifariş, ödəniş — MT-09-un iOS hissəsi), pilot üçün TestFlight və APNs push | Layihə sahibi (Apple Developer hesabı, iPhone) + Claude | Apple Developer Program ($99/il), iPhone | T-273 | MT-09 (iOS sətirləri) | Təxirə salındı — pilotdan əvvəl |
| 12 | **Production buraxılış açarları** (oflayn açar mərasimi): `current` + `next` cütləri oflayn kompüterdə, iki şifrəli USB iki ayrı yerdə, public açarlar `ReleaseKeys.production.cs`-də. Onsuz pilot kiosku heç bir yeniləməni qəbul etmir. **M19-dan (golden image) əvvəl bitməlidir** | Layihə sahibi | Oflayn noutbuk, 2 USB (`docs/RELEASE_SIGNING.md` §3) | T-315 | — | Açıq — M19-dan əvvəl |
| 13 | **Windows kioskunda yeniləmə (M17)**: UI-ın `app\current` junction-ı ilə keçidi, Shell Launcher-in köhnə / yeni versiyanı yenidən başlatması, sağlamlıq yoxlaması və rollback, `RestartAgent`, logların yüklənməsi — kodu var, real Windows cihazı olmadan **yoxlanmayıb**. Agent-in özünü yeniləməsi və provisioning yeniləməsi Windows-da hələ yoxdur: Agent bu sorğuları `updates.notSupportedHere` ilə rədd edir (`docs/KIOSK_UPDATES.md` §4) | Layihə sahibi (cihaz) + Claude | Windows IoT Enterprise cihazı / VM | T-093, T-321 | — | Açıq — cihaz lazımdır |

**Xəritə və məxfilik (T-286, qeyd):** kiosklar xəritəsi OpenFreeMap-dən yüklənir — xəritəni açan
istifadəçinin IP ünvanı OpenFreeMap-ə və Cloudflare-ə çatır. Məxfilik siyasəti qaralamasında yazılıb
(`docs/legal/PRIVACY_POLICY.az.md` §9, T-162); hüquqşünasın yoxlamasından keçməlidir. OpenFreeMap
zəmanət vermir (SLA yoxdur, xəbərdarlıqsız dayana bilər): xəritə yüklənməyəndə tətbiq və panel
siyahını göstərir; uzunmüddətli həll — öz tile serveri (`Map.StyleUrl` ayarı + panelin
`NEOPRINT_MAP_ORIGIN`-i).

**API bir nüsxədir (qərar 2026-10-25):** qlobal, giriş, refresh və anonim müraciət limitləri API prosesinin yaddaşındadır — pilotda tək API nüsxəsi üçün məqbuldur. API bir neçə nüsxəyə (scale) keçəndə bu limitlər Redis-ə köçürülür (`docs/DEPLOY.md` §2), əks halda limit nüsxə sayına vurulur.

**Pilot tələbi deyil (qərar 2026-10-12):** incident-lərin Telegram botu Faza 2-yə keçirilib (T-243) — kanal
tənzimləmə ilə söndürülüdür (`Notification.TelegramEnabled`), pilot üçün bot token-i və qrup lazım deyil.
Incident-lər pilotda panel (canlı) və email ilə bildirilir.


**Son tarixlər (layihə sahibi, 2026-10-23, T-305):**
- **Proqramın adı** (T-298): M21-dən (domen, SMTP göndərən ünvan) və M23-dən (loqo, ikon) **əvvəl**; Google Play-ə ilk
  yükləmədən (T-275) əvvəl **mütləq** — sonra `applicationId` dəyişmir. M21-ə çatanda Claude xatırladır.
- **Production bazası təmiz başlayır** (T-300): development məlumatı köçürülmür, yalnız seed; pilot hesabatı ilk gündən
  yalnız real müştərilərin rəqəmlərini göstərir. Pilot zamanı yoxlama üçün işlədilən hesablar admin paneldə
  "Test hesabı" kimi işarələnir (səbəb məcburidir, audit-ə yazılır).

## Pilot kiosk cihazı (qərar 2026-10-26, T-329)

**Cihaz Windows-dur:** Touch-pie 19" monoblok (daxili kiosk dizaynı) — Intel Core i5 6-cı nəsil, 8 GB RAM, 128 GB SSD,
PCAP toxunma, ekran 1280×1024 (4:3, 250 nit), 2× RS232, RJ45, Wi-Fi / Bluetooth, 2× USB 2.0 + 3× USB 3.0.

**Əməliyyat sistemi — Windows 10 IoT Enterprise LTSC 2021** (versiya 21H2, build 19044; dəstək **2032-ci ilin yanvarına
qədər**). Səbəblər:

- NeoPrint kiosku Shell Launcher (ekranda yalnız NeoPrint) və Keyboard Filter (Ctrl+Alt+Del, Windows düyməsi bağlı)
  tələb edir — bunlar yalnız **Enterprise / Education / IoT Enterprise** nüsxələrindədir. **Windows Pro-da yoxdur**:
  provisioning skripti Pro-nu aydın səbəblə rədd edir (`edition.ps1`, test: `tests/edition.tests.ps1`).
- **6-cı nəsil prosessorla Windows 11 rəsmi dəstəklənmir** (Windows 11 8-ci nəsil və sonrakını tələb edir) — belə
  qurğuda Windows 11 yeniləmə almaya bilər. Ona görə Windows 11 yox, Windows 10 IoT Enterprise **LTSC 2021**.
- **"IoT" sözü vacibdir:** adi *Windows 10 Enterprise LTSC 2021*-in dəstəyi 2027-ci ilin yanvarında bitir, *IoT
  Enterprise LTSC 2021*-in isə 2032-ci ilin yanvarında. Windows 10 Pro 22H2-nin dəstəyi artıq bitib (oktyabr 2025).
- **Pro üçün ehtiyat variant (qərar layihə sahibinindir, tövsiyə olunmur):** Windows Pro-da "Assigned Access" (bir və ya
  bir neçə tətbiqli kiosk rejimi) var, amma Keyboard Filter yoxdur — Ctrl+Alt+Del ekranı açılır, müştəri sessiyadan
  çıxa bilər; üstəlik Windows 10 Pro artıq təhlükəsizlik yeniləməsi almır. Bu variant seçilsə, provisioning skriptinə
  ayrıca rejim yazılır və real cihazda sınanır.

**Cihaz gələndə ilk iş:** `check-windows.ps1` (`docs/KIOSK_SETUP.md` §1) — nüsxə, build, prosessor, yaddaş, disk, ekran,
toxunma, şəbəkə, TPM, Secure Boot, aktivləşdirmə; sonda `RESULT: SUITABLE` / `NOT SUITABLE` və səbəb.

**Lisenziya üçün satıcıya (və ya Microsoft IoT distributoruna) suallar:**

1. Cihazda hansı Windows nüsxəsi və build quraşdırılıb? Lisenziyalıdırmı (rəqəmsal lisenziya və ya COA etiketi, faktura)?
2. Cihazı **Windows 10 IoT Enterprise LTSC 2021** ilə (OEM IoT lisenziyası ilə) göndərə bilərsinizmi? Qiyməti nədir?
   Lisenziya cihaza bağlıdırmı, hansı sənədlə təsdiqlənir?
3. Bu cihazın sürücüləri (toxunma paneli, Wi-Fi, çipset) Windows 10 LTSC 2021 üçün varmı? Toxunma HID (sürücüsüz) işləyirmi?
4. BIOS UEFI-dirmi, TPM 2.0 (Intel PTT) və Secure Boot yandırıla bilirmi?
5. Windows-u biz yenidən quraşdırsaq zəmanət qüvvədə qalırmı? Bərpa (recovery) image-i verirsinizmi?
6. Windows 11 IoT Enterprise LTSC 2024 bu prosessorla dəstəklənirmi (rəsmi prosessor siyahısına görə)? — yox cavabı
   gözlənilir; "hə" desələr, yazılı təsdiq istəyin.
7. İnterfeys dili: ingilis / rus dil paketi daxildirmi? (Kiosk öz dilini özü göstərir, Windows dili müştəriyə görünmür.)

**Yoxlanmayıb (cihazsız):** Windows-da yeniləmə (junction, xidmət, Agent-in özünü yeniləməsi), provisioning, Shell
Launcher, Keyboard Filter, autologon, Firewall, golden image (`sysprep`) — M19b-də real cihazda (T-331, bloklayıcı №3
və №13). Cihazsız heç biri "yoxlanılıb" yazılmır. **Wi-Fi telemetriyası** (T-220) cihaz Ethernet ilə qoşulacaqsa aşağı
prioritetdir.

## Pilot üçün avadanlıq tələbləri (alış siyahısı, T-327)

Qısa və konkret: aşağıdakılar bir kiosk nöqtəsi üçündür.

| Nə | Tələb | Niyə |
|---|---|---|
| **Kiosk kompüteri** (seçildi: Touch-pie 19") | x86-64, Intel Core i5 (6-cı nəsil+; yeni alışda 8-ci nəsil+), **8 GB RAM**, **SSD ≥ 128 GB** (≥ 40 GB boş), **Gigabit Ethernet (RJ45)**, ≥ 2 USB (biri öndə, müştəri üçün), UEFI + TPM 2.0 + Secure Boot, Windows 10 IoT Enterprise LTSC 2021 lisenziyası | Sənəd çevirmə (LibreOffice) və baxış yaddaş istəyir; yeniləmə əvvəlki versiyanı saxlayır (disk); kabel Wi-Fi-dan etibarlıdır |
| **Toxunmatik ekran** | PCAP (çox toxunuşlu), HID (sürücüsüz), **≥ 1280×1024** və ya 1920×1080, parlaqlıq **≥ 250 nit** (qapalı məkan; pəncərə / günəş qarşısında ≥ 400 nit), parıltı əleyhinə şüşə | Kiosk UI bu ölçülər üçün yoxlanır (`DESIGN_SYSTEM.md` 5.1); 250 nit-də kontrast yüksək saxlanılır |
| **Printer** | **Şəbəkə lazer printeri (Ethernet)**, A4 ağ-qara (rəngli — yalnız rəngli çap satılacaqsa), **avtomatik ikitərəfli**, ≥ 30 səh/dəq, lotok ≥ 250 vərəq (500-lük əlavə lotok tövsiyə), **IPP / IPP Everywhere** (sürücüsüz çap) və **SNMP Printer MIB (RFC 3805)** — toner, sayğac, xəta; toner ≥ 3 000 səhifə. Kağız: A4 80 q/m² ofis kağızı (A3 yalnız printer A3 dəstəkləyirsə) | Kiosk printeri IPP ilə idarə edir, toneri / sayğacı / ilişməni SNMP ilə oxuyur: panel "toner azalıb" deyir, servis tapşırığı açılır, refund qərarı sayğaca əsaslanır |
| ↳ **USB printer olmaz** | USB ilə qoşulan printer Windows-da toner səviyyəsi və səhifə sayğacı **vermir** (WMI yalnız "hazırdır / xəta" bilir) — telemetriya, xəbərdarlıqlar və avtomatik tapşırıqlar işləməz (T-092) | |
| **Flaş oxuyucu** | Kioskun ön panelində müştəri üçün USB-A yuvası (USB 2.0 / 3.0), panelə bərkidilən uzatma kabeli ilə; SD kart oxuyucu MVP-də lazım deyil | Müştəri flaşdan fayl seçir; kompüterin arxa yuvaları əlçatmazdır |
| **UPS** | Line-interactive, **650–1000 VA**, ≥ 10 dəq ehtiyat: **kiosk kompüteri + router / switch**; USB ilə kompüterə (istəyə görə). **Lazer printer UPS-in batareya çıxışına qoşulmur** — yalnız ayrıca gərginlik qoruyucusuna (lazer printer qızanda böyük cərəyan çəkir) | Elektrik kəsiləndə kiosk düzgün dayanır, çap jurnalı qorunur (T-089) |
| **Şəbəkə** | Kabel (Ethernet) əsas; kiosk və printer **eyni yerli şəbəkədə**, printerə sabit IP (DHCP rezervasiyası); internet ≥ 5 Mbit/s endirmə / 2 Mbit/s yükləmə, gecikmə < 100 ms; xaricə çıxış: 443 (HTTPS), 80, 53 (DNS), 123 (NTP) — içəri heç nə açılmır; ehtiyat: 4G router (avtomatik keçid) | Ödəniş, sifariş, yeniləmə backend-ə HTTPS ilə gedir; printer yalnız yerli şəbəkədə |
| ↳ **Wi-Fi** (yalnız kabel mümkün deyilsə) | WPA2 / WPA3, siqnal ≥ −65 dBm kioskun yerində, 5 GHz; Wi-Fi parolu golden image-ə **yazılmır** (`prepare-image` dayanır) | |

## Uğur meyarları (T-214, T-282 → T-219)

> **Təklif — pilotdan əvvəl təsdiqlənir.** Hədləri layihə sahibi 2026-10-17-də təklif edib (T-282); pilot başlamazdan
> əvvəl son dəfə təsdiqlənir və bu qeyd götürülür.

Pilotun sonunda **Pilot hesabatı**na (`/pilot-report`, bütün pilot günləri, pilot kioskunu seçib) və göstərilən
səhifələrə baxıb hər sətrin nəticəsini yazırıq.

| # | Meyar | Hədd (təklif) | Haradan oxunur |
|---|---|---|---|
| 1 | Fayl seçənlərdən uğurla çap olunanlar | **≥ 50%** | Pilot hesabatı → "Kioskda müştəri yolu": "Uğurla çap olundu" / "Fayl seçildi" |
| 2 | Ödəniş uğuru (başlanan ödənişlərdən uğurlu) | **≥ 85%** | "Ödəniş xətaları" → "Üsullara görə" → Uğur (hamısı birlikdə) |
| 3 | Ödənilmiş sifarişlərdən uğurla çap olunanlar | **≥ 97%** | Funnel: "Uğurla çap olundu" / "Ödənildi"; "Uğursuz çap" və "Çap dayandı" səbəbləri |
| 4 | Açılmayan fayllar | **≤ 5%** | "Format paylanması": Açılmadı / Seçilib (cəm) |
| 5 | Orta müddət | **≤ 3 dəq** | "Orta çap müddəti"; ziyarətin uzunluğu — "Harada imtina edirlər" (`seconds`) |
| 6 | İkiqat çap, itmiş pul, fiskal çeksiz satış | **0** | Arxa plan işləri → "Ödəniş uzlaşdırması" və "Balans uzlaşdırması" (tapıntı 0), Fiskal çeklər (uğursuz 0), Baxış gözləyən sifarişlər (`Suspected` həll olunub) |
| 7 | Çap xətasında avtomatik refund | **100%** | Pilot hesabatı: "Çap dayandı" sayı = "Refund səbəbləri" → Sistem xətası + Qismən çap; Qaytarmalar → "Uğursuzlar" boş |
| 8 | Kiosk əlçatanlığı | **≥ 95%** | Kiosk → Cari vəziyyət (son 7 gün) və "Əlaqə yoxdur" incident-lərinin müddəti |
| 9 | Critical incident-in bağlanma müddəti | **≤ 4 saat** | Incident-lər → "Bağlanan", ciddilik "Kritik": açılıb → bağlanıb |

## Gündəlik baxış (qaralama, T-284 → T-219)

> Tam siyahı Addım 7B-də (T-219) yazılır; layihə sahibinin indidən istədiyi bəndlər burada saxlanılır.

| # | Nəyə baxılır | Harada | Nə edilir |
|---|---|---|---|
| 1 | **Fayl təmizləmənin sınaq siyahısı** — `Files.CleanupDryRun = true` ilə pilot başlayır; iş saatda bir "silinəcək" obyektləri yazır, heç nə silmir | Arxa plan işləri → "Fayl təmizləmə" (siyahı və ölçü) | Siyahıda açıq sifarişin, refund-un, planlı çapın və ya "Kiosk üçün hazırla"-nın faylı **yoxdursa** və bir neçə gün eyni mənzərədirsə — **Tənzimləmələr → `Files.CleanupDryRun` = false**, əl ilə (T-284). Şübhəli obyekt görünərsə söndürülmür, tapıntı kimi yazılır |
| 2 | Açıq pul incident-ləri (balans və ödəniş uzlaşdırması) | Incident-lər, ciddilik "Kritik" | Sistem bağlamır; baxıb səbəb qeydi ilə bağlanır |
| 3 | Dead-letter növbələri | Arxa plan işləri → "Uğursuz mesajlar" | Mesaj varsa səbəbi araşdırılır |

## Hüquqşünasa suallar

Layihə sahibi hüquqşünasla dəqiqləşdirir; cavab gələndə müvafiq tapşırıq və hüquqi sənəd yenilənir.

| # | Sual | Niyə soruşulur | Sistemdə indi | Tapşırıq | Vəziyyət |
|---|---|---|---|---|---|
| 1 | **İstehlakçının ödəniş üsuluna qaytarma hüququ.** İstifadəçi çap olunmamış sifarişini özü ləğv edəndə məbləğin **ödənilən üsula (karta)** qaytarılmasını tələb edə bilərmi, yoxsa tətbiqdəki balansa qaytarma (balansın nağdlaşdırılmaması şərti ilə) qanuni sayılır? Kartla artırılmış balansdan ödənilən sifariş üçün cavab fərqlidirmi? | Plan 2.5 ləğvdə "Karta qaytar" seçimini nəzərdə tutur; MVP-də tətbiqlə ödənilən sifarişin məbləği balansa qayıdır (qonağın bank QR ödənişi isə həmişə karta) | `POST print-jobs/my/{id}/cancel` → balansa qaytarma; `docs/legal/REFUND_POLICY.az.md` qaralaması | T-242 (7B, real bank adapterindən sonra), T-162 | Açıq |
