# T-164 auditi — ekran görüntüləri (real stack, 2026-10-04)

Stack compose ilə qaldırılıb.

- **Admin:** Playwright + Chrome, 1440×900.
- **Kiosk:** Linux konteyneri `neoprint-kshot` (KSK-9002, fake printer, Xvfb 1920×1080). Toxunuşlar `xdotool` ilə yalnız kiosk pəncərəsinə verilib.
- **Test hesabları:** hər dəfə yeni yaradılıb; yoxlamadan sonra deaktiv edilib.

Audit cədvəli: `docs/audits/T-164-plan-audit-steps-1-4.md`.

| Fayl | Nə göstərir | Plan bəndi |
|---|---|---|
| `admin-rbac-01-limited-menu.png` | Yeni rol (`dashboard.view`, `kiosks.view`): menyuda yalnız iki bənd | Addım 1 DoD (RBAC) |
| `admin-rbac-02-granted-live.png` | `payments.view` verildi — "Ödənişlər" menyuya canlı əlavə olundu, səhifə yenilənmədi | Addım 1 DoD |
| `admin-rbac-03-granted-page.png` | Yeni icazə ilə Ödənişlər səhifəsi, yenidən giriş olmadan | Addım 1 DoD |
| `kiosk-device-01-revoked-activation.png` | "Cihazı ayır"-dan sonra kiosk Activation ekranında, yeni kodla | Addım 1 DoD |
| `kiosk-device-02-reactivated.png` | Həmin cihaz mövcud KSK-9002-yə aktivləşdirildi: altda QR + kod | Addım 1 DoD |
| `kiosk-queue-01-out-of-paper.png` | Kağız yoxdur: iki ödənilmiş sifariş növbədə gözləyir | Addım 3 DoD |
| `kiosk-queue-02-after-restart.png` | `docker restart`-dan sonra: qeydiyyat tələb olunmayıb, növbə yerində | Addım 3 DoD (reboot) |
| `admin-queue-01-two-waiting.png` | Admin → Kiosk → Növbə: həmin iki sifariş | Addım 3 DoD |
| `admin-queue-02-cancelled.png` | 532320 admindən ləğv edildi. Səbəb saxlanmayıb (T-165) | Addım 3 DoD |
| `admin-queue-03-empty-live.png` | Kağız qayıtdı, 708538 bir dəfə çap olundu, tab özü boşaldı | Addım 3 DoD |
| `kiosk-flash-01-jpg.png` | Flaşdan JPG baxışı | Addım 3 DoD (formatlar) |
| `kiosk-flash-02-docx-no-libreoffice.png` | DOCX: dev kiosk konteynerində LibreOffice yoxdur, kiosk izah verir (T-168) | Addım 3 DoD (formatlar) |
