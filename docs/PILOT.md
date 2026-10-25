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
| 3 | Windows kiosk provisioning-i: Shell Launcher, Keyboard Filter, autologon, Agent servisi, Firewall | Layihə sahibi + Claude | Windows IoT Enterprise VM və ya cihaz | T-093, T-220 | MT-03 | Açıq — cihaz lazımdır |
| 4 | Linux kiosk cihazı və real flaş: avtomount, fayl siyahısı, reboot-dan sonra cage | Layihə sahibi + Claude | Ubuntu 24.04 kiosk cihazı, USB flaş | T-094 | MT-04 | Açıq — cihaz lazımdır |
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
