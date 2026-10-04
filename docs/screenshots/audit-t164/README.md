# T-164 auditi və düzəlişləri (T-165, T-166, T-168) — ekran görüntüləri (real stack, 2026-10-04)

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
| `admin-t165-01-cancel-reason.png` | Sifarişi ləğv: səbəb məcburidir; o, `audit_logs`-a `printJobs.cancel` kimi düşür (Queued → Cancelled, səbəb, IP) | T-165 |
| `admin-t165-02-role-deleted.png` | Rol "T-165 silinəcək 69211" səbəblə silindi, siyahıda yoxdur. Səbəb `roles.delete` audit-indədir | T-165 |
| `admin-t166-panel-after-api-recreate.png` | `api` yeni IP ilə yenidən yaradılandan sonra admin restart olunmadan giriş edir, "Canlı" | T-166 |
| `kiosk-t168-01-docx.png` | Yeni kiosk image-i (LibreOffice ilə): flaşdan DOCX baxışı, Azərbaycan hərfləri | T-168 |
| `kiosk-t168-02-docx-paid-printed.png` | Həmin DOCX bank QR ilə ödənildi, fake printerdə bir dəfə çap olundu (542230) | T-168 |
| `kiosk-t168-03-xlsx.png` | XLSX cədvəl baxışı | T-168 |
| `kiosk-t168-04-pptx.png` | PPTX — 2 slayd, 2 səhifə | T-168 |
