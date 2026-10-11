# Addım 6 — ekran görüntüləri (real stack, 2026-10-05 … 2026-10-11)

Hamısı `docker compose up --remove-orphans -d --build api admin` edilmiş stack-dən çəkilib.

- **Mobil tətbiq:** web build (`flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/`), Playwright + Chrome, 430×932. Paylaşım və referral üçün `dev-user` + qeydiyyatla yaradılan yeni hesablar (kod Mailpit-dən).
- **Kiosk:** Linux build-i Docker konteynerində (`neoprint-kshot`, KSK-9002, Xvfb 1920×1080, fake printer — çap olunan PDF `spool/printed/`-dən götürülüb ölçülüb). Flash — konteynerdəki `/media/kiosk/NEOSTICK` tmpfs. Yeniləmə — `tools/dev-kiosk/update-linux-kiosk.sh`.
- **Admin paneli:** docker `admin` (http://localhost:8080), `dev-admin`, 1440×900.
- **Ölçmə:** PDF-lər PDFium ilə 300 dpi-də render olunub, ağ olmayan blokların ölçüsü mm ilə hesablanıb (piksel xətası ±0,09 mm).
- **Android:** emulyator (API 34, Google Play image, 1080×2220, debug APK), real ML Kit skaneri; virtual kameranın "sənədi" otaqdakı şahmat lövhəsidir. Qurulması — `docs/DEV_ENVIRONMENT.md` bölmə 6, alət — `tools/dev-mobile/emu.sh`.
- **Real telefonda qalanlar** (real kartın kənarları və xətkeşlə ölçü, razılıq vərəqi, səhifə redaktoru, QR-dan ID oxuma, push) — `docs/MANUAL_TESTS.md` MT-08; iOS — T-187.

| Qrup | Fayllar | Nə göstərir | Tapşırıq |
|---|---|---|---|
| Skan və tərtibat (backend) | `m2-scan-id-front-back-a4`, `m2-layout-4-cards`, `m2-layout-edited-turned` | `POST files/scan` ilə vəsiqə ön + arxa bir A4-də (85,69 × 54,02 mm), 4 kartlı tərtibat, redaktədən sonra fırladılmış element | T-176 |
| Real ölçü çapı | `m3-kiosk-real-size-lock`, `m3-kiosk-printed-a4-real-size`, `m3-mobile-prepare-real-size-lock` | Kioskda və tətbiqdə kağız / vərəqdə səhifə kilidi, kioskun çap etdiyi A4 (kart real ölçüdə) | T-177, T-188 |
| Mobil tərtibat redaktoru | `m5-01`, `m5-03`, `m5-04`, `m5-06`, `m5-07` | "Səhifədə yerləşdir", seçim, "8 kart" şablonu, saxlama, yenidən açma | T-179, T-190 |
| Kiosk tərtibat redaktoru | `m6-01…07` | Ekran 4a, 8 kart, önizləmə, A4 100% kilidi, çap olunmuş A4 (cərgə 176,30 mm = 2 × 85,6 + 5), yapışma xətti, kağızdan kənar xətası | T-180 |
| Fayl paylaşımı | `m7-01`, `m7-04…14` | "Paylaş", göndərildi, Göndərilənlər, alıcıda badge, Alınanlar, önizləmə, "Fayllarıma saxla", QR kodum, həssas faylın xəbərdarlığı və təsdiqi, nüsxə "Paylaşılıb" ilə | T-181 |
| Referral (backend) | `m8-01-home-bonus`, `m8-02-notification` | Dəvət olunanın kartla ödənmiş çapından (KSK-9002, job 817248) sonra dəvət edəndə +10 bonus səhifə və bildiriş | T-182 |
| Referral (mobil) | `m9-01`, `m9-02`, `m9-04`, `m9-05`, `m9-07` | Profildə giriş, "Dostunu dəvət et" (kod, link, QR, statistika), link kopyalandı, `#/r/{kod}` ilə qeydiyyat (kod doldurulmuş), qeydiyyatdan sonra | T-183 |
| Admin referral və fayllar | `m10-01`, `m10-02`, `m10-04…06` | "Referral" səhifəsi (rəqəmlər, top, tarixçə, filtr), müştəri səhifəsində bonus sətri, dəvətlər, maskalanmış fayl metadatası | T-184, T-193 |
| Mobil skaner (Android) | `m4-00…06` | ML Kit skanerinin kamerası, çəkilmiş səhifə, iki səhifə "Skanı hazırla"da (vəsiqə tipi, "Həssas" məcburi), "Vəsiqə ön + arxa" A4 önizləməsi, "Yadda saxla", Fayllarım-da `Skan` + `Həssas` + "Çapdan sonra silinəcək" (ad "Şəxsiyyət vəsiqəsi - bank kartı.pdf"), "Kiosk üçün hazırla"da real ölçü kilidi | T-178, T-196, T-197 |
| Addımın bağlanması (9.7 axını telefondan) | `m11-01…05` | Tətbiq bağlanıb açılandan sonra sessiya qalır və tab etiketi "Planlı"; Android paylaşım vərəqi (referral mətni); telefonda skan edilən fayl KSK-9002-də real ölçü kilidi ilə; çap olunmuş A4 (hər kart 85,60 × 54,02 mm, Ghostscript xəbərdarlıqsız); çapdan sonra Fayllarım-da skan yoxdur | T-185, T-194, T-195, T-198 |
