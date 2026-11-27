# Kiosk kamerası (T-481)

Layihə sahibinin tələbi (2026-11-27): kioskda USB kamera olsun, çəkdiyi görüntü
admindən verilən gün sayı qədər kioskun diskində saxlanılsın. Məqsəd həm təhlükəsizlik
(vandalizm, oğurluq), həm də "çap olmadı" mübahisələridir.

| Hissə | Nə edir | Tapşırıq |
|---|---|---|
| M1 | Yazılış, şifrələmə, saxlama müddəti və disk limiti, kameranın vəziyyəti, ekranda nişan | T-482 |
| M2 | Admin paneldən canlı və saxlanmış görüntüyə baxış, `camera.view` icazəsi, audit | T-483 |
| M3 | Sifarişə və refund müraciətinə bağlama, məxfilik siyasəti, lövhə | T-484 |

## 1. Necə açılır

- **Feature flag `kiosk_camera`** (Admin → Funksiya açarları): default **söndürülüdür**.
  Kamera hər kiosk üçün ayrıca açılır (kiosk override-ı). Flag açıq olmayan kioska
  konfiqurasiyada `camera` bölməsi gəlmir, ona görə həmin kiosk heç nə yazmır.
- **Ayarlar** (Admin → Parametrlər → Kiosk kamerası). Bütün kiosklar üçün eynidir:

| Ayar | Default | Hədd | Nə edir |
|---|---|---|---|
| `Camera.Mode` | `Activity` | `Activity` / `Continuous` | `Activity` rejimində yalnız kioskda kimsə olanda, telefon qoşulanda və ya çap gedəndə yazır, bir dəqiqə əvvəli və sonrası ilə. `Continuous` fasiləsiz yazır |
| `Camera.RetentionDays` | 2 | 1–31 | Görüntü kioskun diskində bu qədər gün qalır |
| `Camera.MaxDiskGb` | 20 | 1–500 | Görüntünün ən çox tuta biləcəyi yer; dolanda ən köhnəsi silinir |
| `Camera.Resolution` | `1280x720` | `640x480`, `1280x720`, `1920x1080` | Kadrın ölçüsü |
| `Camera.Fps` | 10 | 2–30 | Saniyədə kadr sayı |

Dəyişiklik kiosklara dərhal gedir: konfiqurasiya yenilənir. Ölçü və ya kadr sayı
dəyişəndə FFmpeg yenidən başlayır. Kiosk hədləri özü də yoxlayır (31 gündən çox
saxlamır), dəyişdirilmiş backend də onu buna məcbur edə bilməz.

## 2. Kioskda nə baş verir

- Yazılışı **kiosk tətbiqi** aparır. Agent bunu etmir: Agent backend-ə danışmır,
  kameraya isə kioskun öz sessiyası çatır. FFmpeg **ayrıca proses** kimi işləyir, kod
  ona qoşulmur.
- Kameranı tapmaq:
  - Windows-da DirectShow ilə: FFmpeg-in `-list_devices` siyahısından birinci kamera.
  - Linux-da `/sys/class/video4linux` ilə: `index` faylı `0` olan birinci cihaz.
  - `Kiosk:CameraDevice` ilə konkret kamera seçmək olar.
- Kodlayıcı (H.264), sıra ilə:
  1. `libopenh264` (BSD; Windows LGPL build-ində var);
  2. `h264_mf` (Windows Media Foundation);
  3. `libx264` (Ubuntu-nun öz `ffmpeg` paketi).
  Heç biri yoxdursa kamera yazmır, səbəb kimi `noRecorder` göstərilir.
- Fayllar:
  - Saat dəqiqəsi ilə bölünən 1 dəqiqəlik fragmented MP4. Elektrik kəsiləndə də
    yarımçıq fayl oynadılır.
  - Səs yazılmır.
  - Bitrate: 720p, 10 kadr/san üçün ~1,2 Mbit/s. Fasiləsiz yazılışda gündə ~5 GB-a qədər,
    `Activity` rejimində adətən 0,3–1 GB.
- Qovluq `CameraDirectory`:
  - Linux: `/var/lib/neoprint/camera`, `kiosk:kiosk 700`.
  - Windows: `C:\ProgramData\NeoPrint\camera`.
  - `writing/`: FFmpeg-in yazdığı açıq fayllar.
  - `sealed/`: şifrələnmiş dəqiqələr, adı `20261127T101500Z-60.npcam`.
- **Şifrələmə** (`NeoPrint.Crypto.CameraSegmentCipher`):
  - Hər dəqiqə üçün təzə 256 bitlik açar yaranır. Məzmun chunked AES-256-GCM ilə
    şifrələnir (`EnvelopeEncryptor`, hər chunk autentifikasiyalıdır).
  - Açar backend-in kamera açıq açarı ilə RSA-OAEP-SHA256 ilə bağlanır.
  - Kiosk **yalnız açıq açarı** bilir, öz yazdığını oxuya bilmir. Oğurlanmış kiosk ən çox
    yazılmaqda olan dəqiqəni göstərə bilər. `Activity` rejimində bura saxlanıb-saxlanmayacağı
    hələ bilinməyən sonrakı dəqiqə də əlavə olunur.
- **Backend açarı** (`camera_keys`):
  - RSA-3072, ilk lazım olanda yaranır.
  - Gizli açar backend-in KEK-i ilə şifrəli envelope obyekti kimi Mongo-da saxlanılır.
  - Kioska konfiqurasiyada yalnız `keyId` və `publicKey` gedir.
- **Silmə**, hər 5 dəqiqədən bir:
  - `RetentionDays`-dən köhnə fayllar silinir.
  - `MaxDiskGb`-ni aşanda ən köhnələr silinir.
  - Diskdə 3 GB-dan az boş yer qalanda yenə ən köhnələr silinir. Telemetriyanın "disk
    azdır" həddi 2 GB-dır, ona görə kamera kioskun çap üçün yerini heç vaxt yemir. Yer yenə
    çatmırsa yazılış dayanır (`diskFull`).
  - Kamera söndürüləndə qalan yazılar son məlum saxlama müddəti bitəndə silinir.
- **Çöküşdən sonra:** FFmpeg-in prosesi `ffmpeg.pid` faylından tapılır və növbəti
  başlanğıcda dayandırılır. Kamera açıq ikən tətbiq bağlananda son dəqiqələr rejimdən
  asılı olmayaraq şifrələnib saxlanılır.
- **Ekranda** (qanuni tələb):
  - Kamera yazdığı müddətdə başlıqda, bütün ekranlarda, "Videomüşahidə aparılır" nişanı
    görünür (`DESIGN_SYSTEM.md`).
  - Kamera yazmırsa nişan yoxdur.

## 3. Nəzarət

- **Telemetriya** `camera` qrupu ilə gəlir: açıqdır, kamera tapılıb, yazır, rejim, səbəb,
  diskdə tutduğu yer, dəqiqə sayı, ən köhnə və son yazı. Admin → Kiosk → Vəziyyət bölməsində
  "Kamera" qrupu kimi görünür.
- **Incident `CameraDown`** (Medium) açılır: kamera açıqdır, amma 10 dəqiqə yazmır. Səbəb
  mətndə yazılır: kamera tapılmır, FFmpeg yoxdur, açar yoxdur, yazılış təkrar dayanır və ya
  diskdə yer azdır. Kamera yenidən yazanda incident özü bağlanır.

## 4. Quraşdırma

- **Linux** (provisioning **0.9.0**):
  - `apt` ilə `ffmpeg` quraşdırılır. Bu, Ubuntu-nun öz paketidir: GPL build, `libx264`
    ilə. NeoPrint onu paylamır, ayrıca proqram kimi işə salır, LibreOffice və Ghostscript
    kimi.
  - `kiosk` hesabı `video` qrupundadır.
  - `/var/lib/neoprint/camera` yaradılır.
- **Windows** (provisioning **0.7.0**):
  - Asılılıqlar qovluğuna BtbN-in **LGPL** build-i qoyulur: `ffmpeg-*-win64-lgpl*.zip`,
    məs. `ffmpeg-n8.1-latest-win64-lgpl-8.1.zip`. Skript onu
    `C:\Program Files\NeoPrint\ffmpeg\` qovluğuna açır.
  - Masaüstü tətbiqlərinə kamera icazəsi verilir (`ConsentStore\webcam` = `Allow`).
- **Lövhə:** kamera açılan kioskun üstündə "Videomüşahidə aparılır" lövhəsi olmalıdır
  (M3-də quraşdırma sənədinə əlavə olunur).

## 5. Lokal sınaq (kamera olmadan)

`Kiosk:FakeCamera=true` olanda kamera əvəzinə FFmpeg-in hərəkətli test şəkli
(`testsrc2`) yazılır. Bütün zəncir işləyir: FFmpeg, şifrələmə, saxlama, telemetriya,
nişan. Bunun üçün FFmpeg PATH-da olmalıdır və ya `Kiosk:FfmpegPath` ilə göstərilməlidir.
Ətraflı: `DEV_ENVIRONMENT.md` §7z-n.
