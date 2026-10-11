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
| 1 | Mobil tətbiqin real telefonda əl ilə yoxlanması: kamera ilə QR, push bildirişi, sənəd skanı | Layihə sahibi | Samsung S24 (USB) | T-186, T-225 | MT-07, MT-08 — yalnız "yalnız real telefonda" qeydli sətirlər | Açıq — Addım 7-nin sonunda |
| 2 | Real printer: aşkarlama, imkanlar, test çap, hər format qrupu, printer telemetriyası (SNMP / WMI) | Layihə sahibi + Claude | Pilot printeri (model və qoşulma növü layihə sahibindən) | T-092 | MT-02 | Açıq — printer lazımdır |
| 3 | Windows kiosk provisioning-i: Shell Launcher, Keyboard Filter, autologon, Agent servisi, Firewall | Layihə sahibi + Claude | Windows IoT Enterprise VM və ya cihaz | T-093, T-220 | MT-03 | Açıq — cihaz lazımdır |
| 4 | Linux kiosk cihazı və real flaş: avtomount, fayl siyahısı, reboot-dan sonra cage | Layihə sahibi + Claude | Ubuntu 24.04 kiosk cihazı, USB flaş | T-094 | MT-04 | Açıq — cihaz lazımdır |
| 5 | Çapın ortasında elektrik kəsilməsi | Layihə sahibi | Real kiosk + printer | T-089 | MT-01 | Açıq — cihaz lazımdır |
| 6 | Real ödəniş və e-kassa provayderləri (sandbox), telefonla ödəniş | Layihə sahibi + Claude | Provayder seçimi və sandbox sənədi (layihə sahibində) | T-099, T-219 | MT-06 | Açıq — Addım 7B |
| 7 | Admin test dəstinin başqa kompüterdə işlədilməsi (bu maşında `vitest` forks işə düşmür; CI yaşıldır) | Layihə sahibi | Başqa Windows / Linux kompüteri | T-171 | `docs/DEV_ENVIRONMENT.md` — admin testləri | Açıq |
| 8 | "Production-da məcburi açar silinəndə API başlamır" — ayrıca (staging) OpenBao-da | Claude | Staging mühiti | T-169 | — | Açıq — Addım 7B |
| 9 | Vizual yenidən dizayn, açılış ekranı və ikon | Layihə sahibi (nümunələr) + Claude | Nümunələr | T-022, T-201 | — | Açıq — Addım 7B |
