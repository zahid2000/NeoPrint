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
| `Camera.Quality` | `Medium` | `Low` / `Medium` / `High` | Sıxılma dərəcəsi (T-486): aşağı keyfiyyət az yer tutur |

Rejim, ölçü və keyfiyyət paneldə açılan siyahıdan seçilir.

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
  - Saat dəqiqəsi ilə bölünən 1 dəqiqəlik fragmented MP4. Hər 2 saniyə diskə yazılır, ona
    görə elektrik kəsiləndə də yarımçıq fayl oynadılır.
  - Səs yazılmır.
- **Sıxılma** (T-487):
  - Əvvəlki kadrla eyni olan kadr atılır (`mpdecimate`). Ən azı 5 saniyədə bir kadr qalır
    ki, vaxt axsın.
  - Kodlayıcı sabit keyfiyyət rejimində işləyir, üstündə tavan var. CRF: Low 32, Medium 28,
    High 23. Tavan 720p, 10 kadr/san üçün 600 / 1020 / 1800 kbit/s.
  - Ölçülüb (Ubuntu FFmpeg, `libx264`, 720p, bir dəqiqə):

    | Səhnə | Əvvəl | Medium | Low |
    |---|---|---|---|
    | Boş kiosk (tərpənməz) | ~1,4 MB | **~30–40 KB** | ~5 KB |
    | Daim hərəkət | ~9 MB | ~7,6 MB | ~4,6 MB |

  - Fasiləsiz yazılışda boş saatlar demək olar yer tutmur. Gün ərzində yer, əsasən,
    kioskun önündə hərəkət olan vaxt qədərdir.
  - Şifrəli faylı zip ilə sıxmaq yer qazandırmır, çünki video artıq sıxılıb.
  - Kodlayıcı "irəli baxma" buferindən istifadə etmir (`zerolatency`). Tərpənməz
    səhnədə kadr 5 saniyədə bir gəlir, irəli baxan kodlayıcı dəqiqələrlə videonu yaddaşda
    saxlayardı, elektrik kəsiləndə isə o itərdi. Bu, sınaqda görüldü.
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

## 2a. Fayl serveri (T-485)

Görüntü kioskun diskindən əlavə **fayl serverinə** də yazıla bilər.

- **Hara yazılır.** Admin → Saxlama səhifəsindəki S3 provayderlərindən biri (MinIO, RustFS və ya başqa S3) seçilir.
  - Hamı üçün: Parametrlər → Kiosk kamerası → `Camera.ArchiveProvider`, siyahıdan. Boşdursa heç bir kiosk serverə yazmır.
  - Kiosk üçün ayrıca: Kiosk → "Qiymət" sekmesi → "Kamera görüntüsü — fayl serveri". Seçimlər: ümumi ayar, "serverə yazılmasın" və ya başqa server. Bunun üçün `storage.manage` icazəsi lazımdır.
- **Necə gedir.**
  - Kiosk şifrəli dəqiqəni `POST /api/kiosks/me/camera/segments` ilə **backend-ə** göndərir, backend onu serverə yazır.
  - Kioskda serverin parolu yoxdur.
  - Serverə gedən fayl kioskun diskindəki eyni şifrəli fayldır. Onu yalnız backend-in kamera açarı açır, ona görə server özü də görüntünü görə bilmir.
  - Yer: provayderin Files bucket-i, `camera/{kiosk}/{il}/{ay}/{gün}/…npcam`.
- **Kiosk tərəfi.**
  - Göndərilən dəqiqə `sealed/sent/` qovluğuna köçür və kioskda da öz müddəti qədər qalır.
  - Ən köhnə dəqiqə əvvəl gedir, bir dövrədə ən çox 20 dəqiqə.
  - Server əlçatan deyilsə bir dəqiqə sonra yenidən cəhd olunur.
  - Eyni dəqiqəni ikinci dəfə göndərmək yeni yazı yaratmır.
  - Serverin heç vaxt qəbul etməyəcəyi dəqiqə (vaxtı və ya forması səhv) yalnız kioskda qalır, digərlərini saxlamır.
- **Saxlama müddəti.** `Camera.ArchiveRetentionDays`: default **30**, 1–365. Saatlıq iş vaxtı keçəni serverdən silir.
- **Nəzarət.** Admin → Kiosk → Vəziyyət → Kamera bölməsində "Serverə göndərilməyib" (dəqiqə sayı) və "Serverə son göndəriş" görünür.
- **Xətt.** Kiosk görüntünü internetlə göndərir. `Activity` rejimində və tərpənməz kadrlar atıldığı üçün adətən gündə yüzlərlə MB-dır. Fasiləsiz rejimdə və hərəkətli yerdə bir neçə GB ola bilər.

## 2b. Paneldən baxış (T-483)

Admin → Kiosk → **"Kamera"** tabı (`camera.view` icazəsi). Hər baxış audit jurnalına yazılır.

- **Canlı.**
  - FFmpeg yazdığı görüntünü eyni zamanda kiçik JPEG kadrlar kimi də verir: saniyədə 2, eni 640 piksel, təxminən 12 KB. Kadrlar FFmpeg-in standart çıxışına gedir və yalnız kioskun yaddaşında qalır, diskə yazılmır.
  - "Canlı bax" basılanda kiosk hub-la xəbər alır və son kadrı saniyədə iki dəfə backend-ə göndərir.
  - Backend yalnız son kadrı Redis-də 10 saniyə saxlayır.
  - Panel kadr istəməyi dayandıranda (düymə, tabdan çıxmaq, səhifəni bağlamaq) 20 saniyə ərzində kiosk da dayanır.
  - Kimsə baxmırsa kioskdan heç nə getmir.
  - Kamera `Activity` rejimində də canlı görünür: FFmpeg həmişə işləyir, yalnız boş dəqiqələr saxlanmır.
- **Yazılmış.**
  - Tarix, saat və müddət (5–60 dəqiqə) seçilir.
  - Fayl serverində (T-485) olan dəqiqələr dərhal oynayır.
  - Olmayanlar üçün **"Kioskdan gətir"** var: kiosk həmin aralığın şifrəli dəqiqələrini göndərir. Backend onları əsas saxlama provayderində **24 saat** saxlayır.
  - Kiosk oflayndırsa sorğunu qoşulanda yerinə yetirir: hub hadisəsi gəlir, kiosk 2 dəqiqədən bir də özü yoxlayır. Sorğu 24 saat açıq qalır.
  - Backend dəqiqəni açır və panelə MP4 kimi verir. Açıq görüntü serverdə saxlanmır: müvəqqəti fayl ötürmə bitəndə silinir.
  - Dəqiqə bitəndə növbətisi özü başlayır. "Bu dəqiqəni yüklə" MP4 faylını endirir.
- **Trafik.**
  - Canlı baxış saniyədə təxminən 25 KB-dır.
  - Bir dəqiqəni gətirmək 1–8 MB-dır: keyfiyyətdən və hərəkətdən asılıdır.

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

## 4a. Panel "FFmpeg yoxdur" deyirsə (T-495)

Kiosk quraşdırılanda asılılıqlar qovluğunda FFmpeg zip-i olmayıb. Kamera yazmır, paneldə Vəziyyət → Kamera: "Yazmır", "Niyə yazmır: FFmpeg yoxdur".

- **Windows:** kioskda administrator PowerShell-də:

  ```powershell
  powershell -ExecutionPolicy Bypass -File toolskiosk-camerainstall-ffmpeg.ps1
  ```

  Skript BtbN-in LGPL build-ini endirir (~170 MB) və `C:Program FilesNeoPrintfmpeg`-ə açır. İnternetsiz kiosk üçün zip-i fləşkada gətirin: `-Zip D:fmpeg-n8.1-latest-win64-lgpl-8.1.zip`. Sonra kiosk tətbiqi FFmpeg-i 10 dəqiqə ərzində özü tapır; dərhal görmək üçün panel → kiosk → Əmrlər → tətbiqi yenidən başladın.
- **Linux:** `sudo apt install ffmpeg` (və ya provisioning 0.9.0+ ilə `repair`).
- Yeni kioskda bu lazım deyil: asılılıqlar qovluğuna `ffmpeg-*-win64-lgpl*.zip` qoyun (§4).

## 5. Lokal sınaq (kamera olmadan)

`Kiosk:FakeCamera=true` olanda kamera əvəzinə FFmpeg-in hərəkətli test şəkli
(`testsrc2`) yazılır. Bütün zəncir işləyir: FFmpeg, şifrələmə, saxlama, telemetriya,
nişan. Bunun üçün FFmpeg PATH-da olmalıdır və ya `Kiosk:FfmpegPath` ilə göstərilməlidir.
Ətraflı: `DEV_ENVIRONMENT.md` §7z-n.
