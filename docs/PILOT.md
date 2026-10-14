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
| 9 | Vizual yenidən dizayn, açılış ekranı və ikon | Layihə sahibi (nümunələr) + Claude | Nümunələr | T-022, T-201 | — | Açıq — Addım 7B |
| 10 | Production SMTP: göndərən domen, SPF / DKIM (və DMARC) — istifadəçi emaili (qeydiyyat kodu, qəbz) və incident emaili (Critical / High, kioskun əhatəsindəki hesablara) spam-a düşmədən çatmalıdır | Layihə sahibi (domen, SMTP provayderi) + Claude | Domen, SMTP hesabı; OpenBao-da SMTP açarları | T-219, T-241 | MT-06 №8 (email hissəsi) | Açıq — Addım 7B |
| 11 | iOS: test build-i iPhone-da (PIN, Face ID, Keychain, giriş, sifariş, ödəniş — MT-09-un iOS hissəsi), pilot üçün TestFlight və APNs push | Layihə sahibi (Apple Developer hesabı, iPhone) + Claude | Apple Developer Program ($99/il), iPhone | T-273 | MT-09 (iOS sətirləri) | Təxirə salındı — pilotdan əvvəl |

**Pilot tələbi deyil (qərar 2026-10-12):** incident-lərin Telegram botu Faza 2-yə keçirilib (T-243) — kanal
tənzimləmə ilə söndürülüdür (`Notification.TelegramEnabled`), pilot üçün bot token-i və qrup lazım deyil.
Incident-lər pilotda panel (canlı) və email ilə bildirilir.

## Hüquqşünasa suallar

Layihə sahibi hüquqşünasla dəqiqləşdirir; cavab gələndə müvafiq tapşırıq və hüquqi sənəd yenilənir.

| # | Sual | Niyə soruşulur | Sistemdə indi | Tapşırıq | Vəziyyət |
|---|---|---|---|---|---|
| 1 | **İstehlakçının ödəniş üsuluna qaytarma hüququ.** İstifadəçi çap olunmamış sifarişini özü ləğv edəndə məbləğin **ödənilən üsula (karta)** qaytarılmasını tələb edə bilərmi, yoxsa tətbiqdəki balansa qaytarma (balansın nağdlaşdırılmaması şərti ilə) qanuni sayılır? Kartla artırılmış balansdan ödənilən sifariş üçün cavab fərqlidirmi? | Plan 2.5 ləğvdə "Karta qaytar" seçimini nəzərdə tutur; MVP-də tətbiqlə ödənilən sifarişin məbləği balansa qayıdır (qonağın bank QR ödənişi isə həmişə karta) | `POST print-jobs/my/{id}/cancel` → balansa qaytarma; `docs/legal/REFUND_POLICY.az.md` qaralaması | T-242 (7B, real bank adapterindən sonra), T-162 | Açıq |
