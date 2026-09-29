# Addım 3 — ekran görüntüləri (real stack, 2026-09-29)

Hamısı `docker compose up -d --build` edilmiş stack-dən çəkilib.

- **Admin paneli və mobil tətbiq:** Playwright + Chrome ilə.
- **Kiosk tətbiqi:** Linux build-i (`dotnet publish -r linux-x64`) Docker konteynerində, Xvfb ekranında (1920×1080) çəkilib.
  - Konteyner compose şəbəkəsinə qoşulub, API-yə birbaşa gedir.
  - Kiosk `KSK-9002` enrollment token ilə aktivləşdirilib.
  - Printer `UseFakePrinter` ilədir.
  - "Flaş" `/media/kiosk/NEOSTICK` altına mount edilmiş tmpfs-dir, kiosk onu `/proc/mounts`-dan tapır.
  - Toxunuşlar `xdotool` ilə verilib.

Windows kiosku (Shell Launcher, Keyboard Filter), real printer və real USB flaş əl ilə yoxlanmalıdır: `KIOSK_SETUP.md`, `KIOSK_PROVISIONING.md`.

## Kiosk

| Fayl | Nə göstərir |
|---|---|
| `kiosk-01-not-configured.png` | Aktivləşmədən dərhal sonra, printer seçilməyib: "Kiosk quraşdırılır" |
| `kiosk-02-home.png` | Admin printeri seçib test səhifəsi keçəndən sonra ana ekran. Seçilmiş dil başlıqda fərqlənir (T-086) |
| `kiosk-03-usb-files.png` | Flaş taxılan kimi fayl siyahısı, "USB" nişanı, addım göstəricisində "Ödəniş" yoxdur (Addım 4-ə qədər) |
| `kiosk-04-preview.png` | PDF baxışı (PDFium), səhifə miniatürləri |
| `kiosk-05-options.png` | Parametrlər və canlı qiymət |
| `kiosk-06-range-keypad.png` | Səhifə aralığı ekran klaviaturası ilə (T-072, T-081). Səhv aralıq sahənin altında, banner-də yox |
| `kiosk-07-range-price.png` | Düzgün aralıq "1-2" → 0,20 ₼, 2 vərəq |
| `kiosk-08-order-placed.png` | Sifariş kodu və növbədəki yer. İş fake printerdə 2 saniyəyə çap olunub |
| `kiosk-09-text-editor.png` | TXT redaktoru, Azərbaycan hərfli ekran klaviaturası. Az/ru mətn düzgün oxunur (T-076) |
| `kiosk-10-backend-offline.png` | API yenidən başlayarkən: "Onlayn ödəniş müvəqqəti əlçatmazdır", yeni sifariş qəbul olunmur. Kiosk özü bərpa olunub |
| `kiosk-11-cancel-by-code.png` | Kodla ləğv: çap olunmuş sifariş ləğv olunmur |
| `kiosk-12-admin-pin.png` | Loqoya 5 toxunuş → admin PIN (T-082-dən sonra "Daxil ol" ağ mətnlə) |
| `kiosk-13-admin-menu.png` | Texniki menyu. Təhlükəsiz rejim olmadığı üçün "rejimdən çıx" gizlidir |
| `kiosk-14-safe-mode.png` | Tətbiq 3 dəfə qəfil öldürüldükdən sonra "Texniki xidmət" (təhlükəsiz rejim) |
| `kiosk-15-admin-menu-safe-mode.png` | Təhlükəsiz rejimdə menyu: "Təhlükəsiz rejimdən çıx və yenidən başlat" |
| `kiosk-16-english.png` | İngilis dili |
| `kiosk-17-paper-blocked.png` | Lotokda 1 vərəq qalıb, sifariş 3 vərəqdir: izah + "Sifariş et" bağlıdır (`Paper.BlockWhenInsufficient`). Kağız sonra geri dolduruldu |

`kiosk-01`, `kiosk-09` … `kiosk-15` T-086 düzəlişindən əvvəl çəkilib. Onlarda başlıqdakı seçilmiş dil hələ fərqlənmir; qalan hər şey eynidir.

## Admin paneli

| Fayl | Nə göstərir |
|---|---|
| `admin-01-dashboard.png` | "Kioskların vəziyyəti": status sayları və "Diqqət tələb edir" siyahısı. KSK-9004-ün heartbeat-i köhnədir → siyahıda "Əlaqə yoxdur" (T-083), sayda da "Oflayn" (T-091). KSK-9002 (təhlükəsiz rejimdən çıxandan sonra) "Onlayn"-dadır |
| `admin-02-kiosk-printers.png` | Kiosk → Printerlər: seçilmiş printer, imkanlar (yalnız söndürmək olar), əmrlər |
| `admin-04-kiosk-safe-mode.png` | Kiosk redaktorunda təhlükəsiz rejim banner-i, status "Servisdə" |
| `admin-05-print-jobs.png` | Sifarişlər səhifəsi: filtrlər, "Tarix aralığı" `dd.MM.yyyy` ilə (T-090), status chip-ləri, "Ödənişsiz (sınaq mühiti)". Brauzer ingiliscədir — format yenə `28.09.2026` |
| `admin-06-print-job-detail.png` | Sifariş detalı: parametrlər, qiymət snapshot-u, çap cəhdləri. Sayğac yoxdursa "Sayğac yoxdur" (T-084); "Sifarişlərə qayıt" solda (T-085) |
| `admin-07-kiosk-supplies.png` | Kiosk → Sərf: lotok səviyyələri, doldurma forması (operator hesabı ilə "Tam doldur"), hərəkətlərdə kağız "A4 kağızı" (T-085) |
| `admin-08-kiosk-queue.png` | Kiosk → Növbə (boş vəziyyət) |
| `admin-09-kiosk-orders.png` | Kiosk → Sifarişlər (siyahı kiosk ilə süzülüb) |
| `admin-10-feature-flags.png` | Funksiya açarları: insan dilində ad + izah, texniki açar kiçik şriftlə (T-059) |
| `admin-11-date-invalid.png` | Tarix aralığına `2026-09-27` yazılıb: sahə qırmızı, altında nə olduğu və necə yazmaq lazım olduğu; filtr tətbiq olunmur, digər sahələr yerindən oynamır (T-090) |
| `admin-12-date-calendar.png` | Təqvim Azərbaycan dilində: "Sentyabr 2026", həftə bazar ertəsindən (Be, Ça, Ç, Ca, C, Ş, B), seçilmiş aralıq 28–29 (T-090) |

## Mobil

| Fayl | Nə göstərir |
|---|---|
| `mobile-01-home.png` | Chrome-da ana səhifə (`dev-user`). Balans kartı yoxdur, yarımçıq element görünmür (T-060) |

`admin-01`, `admin-05` … `admin-08` T-085 / T-087 düzəlişləri real stack-ə yığılandan sonra yenidən çəkilib: sidebar-da "Sifarişlər" ikonu, boş vəziyyət ikonu və hərəkətlərdə "A4 kağızı" görünür.
