# Addım 4 — ekran görüntüləri (real stack, 2026-10-01)

Hamısı `docker compose up --remove-orphans -d --build api admin` edilmiş stack-dən çəkilib.

- **Admin paneli və mobil tətbiq:** Playwright + Chrome ilə. Mobil tətbiq `flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/` ilə qurulub.
- **Kiosk tətbiqi:** Linux build-i (`dotnet publish -r linux-x64`) Docker konteynerində, Xvfb ekranında (1920×1080).
  - Konteyner compose şəbəkəsinə qoşulub, API-yə birbaşa gedir; kiosk `KSK-9002`.
  - Printer `UseFakePrinter` ilədir. Çap zamanı xəta `printer-fail-next.txt` ilə verilib (T-123).
  - "Flaş" konteynerə `--tmpfs /media/kiosk/NEOSTICK` ilə qoşulub. Konteyner imtiyazsızdır (`--privileged` yoxdur).
  - Toxunuşlar `xdotool` ilə verilib.
- **Bank və fiskal provayder:** mock-dur. Bank ödənişi `POST /api/dev/payments/{id}/simulate-success` ilə təsdiqlənib.
- **Yoxlama üçün müvəqqəti tənzimləmələr:** `Payment.TimeoutMinutes = 3` və `Job.SuspectedAfterMinutes = 1`. Sonra standart dəyərlərə (5 və 15) qaytarılıb.

Real bank, real fiskal provayder (e-kassa), real printer və Windows kiosku əl ilə yoxlanmalıdır — `docs/MANUAL_TESTS.md`, "Addım 4".

## Kiosk

| Fayl | Nə göstərir |
|---|---|
| `kiosk-01-range-symbols.png` … `kiosk-03-png-preview.png` | M8-dən əvvəlki düzəlişlər (T-097, T-100) |
| `kiosk-04-bank-qr-countdown.png` | Bank QR ödənişi. `Payment.TimeoutMinutes = 3` olanda geri sayım 2:53-dən başlayır — paneldəki dəyər kioska çatır |
| `kiosk-05-promo-sheet.png` | Promokod yazma paneli (T-120): tam klaviatura, yanında "Sil"; səhv kodun səbəbi sahənin sağındadır, klaviatura yerindən oynamır |
| `kiosk-06-paid-receipt.png` | "Ödəniş uğurlu": sifariş kodu, fiskal çekin QR-ı, "Email ünvanını yazın" (klaviatura ayrıca paneldə açılır — T-120) |
| `kiosk-07-printer-error-refund.png` | Ssenari A: printer çap zamanı qırılıb → 0,30 ₼ avtomatik karta qaytarılıb; ana ekranda "Çap tamamlanmadı" arayışı və status səhifəsinin QR-ı |
| `kiosk-08-app-payment-qr.png` | "NeoPrint tətbiqi" ilə ödəniş: tətbiqin QR-ı və geri sayım |

## Admin paneli

| Fayl | Nə göstərir |
|---|---|
| `admin-01-suspected-list.png` | Kioskla əlaqə kəsilib → sifariş 520127 `Suspected` (B ssenarisi, avtomatik qaytarma yoxdur) |
| `admin-02-suspected-investigation.png` | Araşdırma: fayl, kioskun bildirdiyi, çap cəhdləri, həmin vaxt kioskda baş verənlər |
| `admin-03-suspected-decision.png` | Qərar dialoqu: tam / hissəvi / qaytarma yoxdur / yenidən çap; səbəb və qeyd məcburidir |
| `admin-04-suspected-decided.png` | Qərar qeydə alınıb, siyahı boşdur (empty state) |
| `admin-05-dashboard-money.png` | İdarə panelində pul kartı |
| `admin-06-payments.png` | Ödənişlər: status chip-i, üsul, kiosk, sifariş, qaytarılan məbləğ |
| `admin-07-payment-refunded.png` | Ssenari A-nın ödənişi: `SystemError` ilə karta qaytarılıb, satış və qaytarma fiskal çeki |
| `admin-08-refunds.png` … `admin-14-revenue.png` | Qaytarmalar, fiskal çeklər, qəbzlər, voucher-lər, promokodlar, müştərilər, gəlir hesabatı (qrafik + cədvəl) |
| `admin-15-refund-wallet-dialog.png` | Manual qaytarma: hesabı olan müştərinin ödənişi — "Balansa" və ya "Karta" |
| `admin-16-refund-wallet-done.png` | Balansa qaytarılıb ("Balansa · Admin qərarı"), qaytarma fiskal çeki verilib |
| `admin-17-refund-card-done.png` | Anonim bank QR ödənişi karta qaytarılıb |
| `admin-18-refund-failed.png` | Bank refund-u 3 dəfə rədd edib (`mock-bank/refunds-fail`), ödəniş "Qaytarma alınmadı"; e-kassa `Down`, satış çeki "Hazırlanır" |
| `admin-19-refund-to-voucher.png` | Uğursuz refund anonim müştəri üçün voucher-ə yönəldilir |
| `admin-20-refund-voucher-done.png` | Voucher `RF-…` verilib, refund "Qaytarılıb"; tarixçədə "Qaytarma alınmadı" → "Qaytarıldı" |
| `admin-21-fiscal-pending.png` | Fiskal çeklər: qaytarma çeki satış çekini gözləyir. E-kassa bərpa olunandan sonra hər iki çek avtomatik verilib (backoff ilə) |
| `admin-22-fiscal-incident.png` | T-129: e-kassa (`mock-fiscal/mode=Reject`) balans artırma çekini rədd edib. Səhifənin başında "Açıq incident-lər" kartı göstərilir: ciddilik, nə baş verdiyi oxucunun dilində, "Təkrar cəhd" |
| `admin-23-fiscal-incident-retried.png` | E-kassa bərpa olunub, "Təkrar cəhd" basılıb: çek "Verilib", incident bağlanıb və kart yox olub |
| `admin-24-refunds-filter.png` | T-137: Qaytarmalar səhifəsində status və səbəb filtri ("Admin qərarı") |
| `admin-25-payments-channel.png` | T-138: Ödənişlər səhifəsində kanal filtri ("Mobil tətbiq") |

## Mobil tətbiq

| Fayl | Nə göstərir |
|---|---|
| `mobile-01-pay.png` | Kioskun QR-ından açılan ödəniş: məbləğ, geri sayım, sifariş, balans / kart seçimi, promokod |
| `mobile-02-paid.png` | "Ödəniş uğurlu" — sifariş kioskda növbəyə düşüb |
| `mobile-03-receipts.png` | Profil → Qəbzlər: `NP-2026-000004`, fiskal çek verilib |
| `mobile-04-wallet.png` | Balans: ledger, "Balansı artır", voucher kodu |
