# Addım 5 — ekran görüntüləri (real stack, 2026-10-02 … 2026-10-04)

Hamısı `docker compose up --remove-orphans -d --build api admin` edilmiş stack-dən çəkilib.

- **Mobil tətbiq:** web build (`flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/`), Playwright + Chrome, 430×932. Dəyişən hesablar üçün hər dəfə **yeni test hesabı** (qeydiyyat + Mailpit-dən kod); `dev-user` yalnız oxunan ssenarilərdə.
- **Kiosk:** Linux build-i Docker konteynerində (`neoprint-kshot`, KSK-9002, Xvfb 1920×1080, fake printer). Yeni kodla yeniləmə — `tools/dev-kiosk/update-linux-kiosk.sh`. Şəxsi QR skanerin yerinə `xdotool type` ilə yalnız kiosk pəncərəsinə yazılıb (`DEV_ENVIRONMENT.md` 7d), toxunuşlar `xdotool`.
- **Push:** dev-də `LoggingPushSender` və tətbiq daxili banner. Real FCM (web token qeydiyyatı işləyir; Google IAM rolu gözlənilir — T-163) və real telefon — `docs/MANUAL_TESTS.md` MT-07.
- **Bank / fiskal:** mock; kart ödənişi mock kart provayderi ilə.

| Qrup | Fayllar | Nə göstərir | Tapşırıq |
|---|---|---|---|
| Kiosk axtarışı | `mobile-01…08`, `mobile-en-01` | Siyahı, yaxınlıq, xəritə (OSM), kiosk detalı (qiymətlər, kağız), tapılmayan kiosk, `neoprint://kiosk/…` linki | T-142 |
| Mobil sessiya | `session-01…09`, `kiosk-session-01…02` | Kioskda sessiya açma, fayl, parametrlər, ödəniş, banner, sessiyanın bitməsi; kioskda "telefon qoşulub" nişanı | T-143 |
| Şəxsi QR | `userqr-01…10`, `kiosk-userqr-01…07`, `kiosk-scan-guarded-01` | Kiosk üçün hazırla (ödənişsiz / ödənişli), Hazırladıqlarım, Mənim QR kodum, kioskda ekran 10, götürmə, ləğv; skript yalnız kiosk pəncərəsinə yazır | T-144, T-153 |
| Planlanmış çap | `schedule-01…18` | Tab, fayl menyusu, tarix/saat (Azərbaycan həftə günləri), ödəniş, siyahı, vaxt çatanda dialoq, çap, ləğv → balans, kiosk dayandırılıb, vaxtı dəyiş, avtomatik ləğv (admin ayarı) | T-145, T-154…156 |
| Planlanmış çap (əlavə) | `plan-01…04` | Ana səhifədə "Planlanıb · {vaxt}"; eyni dəqiqəyə düşən iki plan bir dialoqda, ikisi də çap olundu | T-158, T-159 |
| Bildirişlər | `notif-01…07` | Zəng nişanı, banner, siyahı, oxunma, bildirişdən ekrana keçid | T-146 |
| Məxfilik | `privacy-01…09` | Avtomatik silinmə, "Məlumatlarımı yüklə" (hazırlanır / hazırdır), "Hesabımı sil" (mane olan, forma, çıxış) | T-147 |
| Feature flag (mobil) | `flags-01…06` | Söndürüləndə giriş nöqtələri yoxdur, ödənilmiş planlama tabı qalır; açılanda qayıdır | T-148 |
| Feature flag (kiosk) | `kiosk-flags-01…04` | Açıq ekranda canlı keçid: "Promokod" itir, ödənişdə yalnız bank QR, açılanda "NeoPrint tətbiqi" qayıdır | T-160 |
| Dil | `lang-01…05` | Profil → Dil, English seçimi, ikinci "telefon"da hesabın dili, "Telefonun dili" | T-152 |
| Hüquqi sənədlər | `legal-01…04` | Profil → Hüquqi sənədlər, tərcümə yoxdur qeydi, qeydiyyatda "Oxu", girişsiz açılış | T-152, T-161 |
| Admin | `admin-01…03` | Fiskal insident mətnləri az / en / ru | T-140 |
