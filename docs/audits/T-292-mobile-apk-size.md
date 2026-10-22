# Mobil APK ölçüsü və yüklənmə — audit (T-292)

Tarix: 2026-10-22. Ölçülən: release APK, `maplibre_gl` 0.27.1 ilə (T-217), lokal build (Windows, Flutter
3.47.7, JDK 21). Bayt rəqəmləri `ls -l`, tərkib `unzip -lv` (sıxılmamış ölçü; native kitabxanalar APK-da
sıxılmadan saxlanılır) və `flutter build apk --analyze-size` ilə.

## 1. Əvvəl / sonra

| Build | APK, bayt | MB | Qeyd |
|---|---|---|---|
| Xəritədən əvvəl, universal (CI, son deploy) | 85 683 621 | 85,7 | `flutter_map`, 3 ABI |
| Xəritə ilə, universal (3 ABI) | 115 198 974 | 115,2 | +29,5 MB — T-286 həddi keçildi |
| Xəritə ilə, `--target-platform android-arm,android-arm64` | 91 179 894 | 91,2 | **Tapıntı:** plugin-lərin x86_64 kitabxanaları yenə də içindədir |
| + ABI süzgəci (`app/build.gradle.kts`, T-292) | 73 942 074 | 73,9 | x86_64 tam çıxdı |
| + `--split-debug-info` (simvollar artefaktda) | **70 583 354** | **70,6** | `libapp.so` −1,4 / −1,8 MB; **deploy-un yeni APK-sı** |
| Yalnız arm64 (hesablanmış) | ≈ 40 300 000 | ≈ 40 | 70,6 − armeabi-v7a kitabxanaları (≈ 28,5 MB) − kiçik fərqlər; ölçülmədi |
| Yalnız arm64 + ML Kit unbundled (hesablanmış) | ≈ 35 000 000 | ≈ 35 | aşağıda, bənd 3.1 — qərar layihə sahibinin |

**Realist hədəf (vəd deyil):** test serveri üçün indiki **≈ 70,6 MB** (arm + arm64). Layihə sahibinin qərarı ilə
yalnız arm64 → **≈ 40 MB**, üstəlik ML Kit unbundled → **≈ 35 MB**. Play üçün AAB (T-275) bir telefona yalnız
öz ABI-sini verir — orada endirmə də ≈ 35–40 MB olar.

## 2. Tərkib (arm + arm64, ABI süzgəcli, `--split-debug-info`-suz build — 73,9 MB)

| Hissə | arm64-v8a, MB | armeabi-v7a, MB | Haradan |
|---|---|---|---|
| `libflutter.so` | 11,20 | 8,22 | Flutter mühərriki |
| `libapp.so` (Dart kodu, AOT) | 10,13 (split-debug-info ilə 8,69) | 11,38 (9,61) | bizim kod + Dart paketləri |
| `libmaplibre.so` | 10,35 | 7,56 | `maplibre_gl` (T-217) |
| `libbarhopper_v3.so` | 4,72 | 3,09 | `google_mlkit_barcode_scanning` — **bundled** ML Kit barkod mühərriki |
| Digər native (`libimage_processing_util_jni`, `libsurface_util_jni`, `libdatastore_shared_counter`) | 0,04 | 0,02 | `camera` (CameraX), androidx datastore |
| **Cəmi native** | **36,44** | **30,27** | |

| Ortaq hissə | MB | Qeyd |
|---|---|---|
| `classes.dex` (Java / Kotlin, R8 ilə) | 1,73 (zip; açılmış 3,75) | MapLibre Java, okhttp, CameraX, Play Services, Firebase |
| ML Kit barkod modelləri (`assets/mlkit_barcode_models`) | 0,83 | bundled variantın modelləri |
| `Inter-Variable.ttf` | 0,84 | dəyişən şrift (bütün çəkilər bir faylda) |
| `NOTICES.Z`, `PublicSuffixDatabase.list`, shader-lər | 0,27 | Flutter / okhttp |
| `MaterialIcons-Regular.otf` | 0,02 | **tree-shake işləyir** (1,6 MB → 19,8 KB) |
| `resources.arsc` + `res/` | 0,43 | resurs sıxılması işləyir (adlar qısaldılıb) |

### Dart kodu, paket üzrə ilk 15 (arm64, `--analyze-size`, cəmi ≈ 10 MB)

| # | Paket | KB |
|---|---|---|
| 1 | `package:flutter` | ≈ 4 000 |
| 2 | `package:neoprint` (bizim kod) | ≈ 1 000 |
| 3 | `package:image` (skanın emalı) | 858 |
| 4 | `dart:mixin_deduplication` | 410 |
| 5 | `package:flutter_localizations` | 344 |
| 6 | `dart:core` | 315 |
| 7 | `dart:ui` | 236 |
| 8 | `dart:typed_data` | 227 |
| 9 | `dart:async` | 142 |
| 10 | `package:camera_android_camerax` | 140 |
| 11 | `dart:io` | 127 |
| 12 | `package:intl` | 108 |
| 13 | `dart:collection` | 97 |
| 14 | `package:go_router` | 86 |
| 15 | `package:material_color_utilities` | 77 |

**Paket üzrə yük (arm + arm64):** `maplibre_gl` ≈ 17,9 MB native + Java; ML Kit barkod (bundled) ≈ 8,6 MB
(7,8 native + 0,83 model); Flutter mühərriki 19,4 MB; Dart kodu 18,3 MB (split-debug-info ilə).

## 3. Araşdırma

### 3.1 ML Kit barkod: bundled → unbundled — **qərar layihə sahibinin**
`google_mlkit_barcode_scanning` 0.16.1 `com.google.mlkit:barcode-scanning:17.3.0`-ü (bundled: mühərrik + model
APK-nın içində) gətirir. Unbundled variant — `com.google.android.gms:play-services-mlkit-barcode-scanning` — eyni
API, model Google Play Services-dən **ilk istifadədə endirilir**.

| | Bundled (indi) | Unbundled |
|---|---|---|
| APK (arm + arm64) | — | **−8,6 MB** (bir telefona ≈ −5,6 MB) |
| İlk skan oflayn | işləyir | **işləmir**, model endirilənə qədər (bir neçə MB, bir dəfə) |
| Play Services olmayan cihaz (məs. Huawei) | işləyir | **işləmir** |
| Kod dəyişikliyi | — | Gradle-də asılılığın əvəzlənməsi (plugin-in Java kodu eynidir); real telefonda (MT-07) yoxlanmalı |

Qeyd: sənəd skaneri (`play-services-mlkit-document-scanner`) artıq unbundled-dir, yəni tətbiq Play Services-ə onsuz
da bağlıdır. Ödəniş QR-ı oxunmasa tətbiq `neoprint://pay` linki ilə eyni ödənişi açır (mövcud yol).

### 3.2 Şriftlər və ikonlar
- Material Icons tree-shake olunur (19,8 KB). `cupertino_icons` şrifti APK-da yoxdur.
- `Inter-Variable.ttf` 0,84 MB: dəyişən şrift, bütün çəkilər bir faylda. Latın + kiril + Azərbaycan simvollarına
  subset ≈ 0,4–0,5 MB qazanc verər, amma alət (fonttools) tələb edir — **edilmədi**, qazanc kiçikdir.

### 3.3 Assets
Şəkil və animasiya yoxdur. İstifadə olunmayan asset tapılmadı.

### 3.4 R8 / resurs sıxılması
Flutter release-də hər ikisi **artıq açıqdır**: `classes.dex` 1,73 MB, resurs adları qısaldılıb. Əlavə iş yoxdur.

### 3.5 ABI-lər — **tapıntı və düzəliş**
`--target-platform` yalnız Flutter-in öz kitabxanalarını süzür, Flutter-in Gradle plugin-i isə `abiFilters`-i
bütün ABI-lərə qaytarır. Nəticədə "arm + arm64" APK plugin-lərin x86_64 kitabxanalarını da daşıyırdı
(+16,4 MB). Düzəliş: `gradle.properties` → `disable-abi-filtering=true`, `app/build.gradle.kts` → süzgəc
`target-platform`-dan (yoxdursa Flutter-in 3 ABI-si; `--split-per-abi`-də tətbiq olunmur).

### 3.6 Yalnız arm64 test APK-sı — **qərar layihə sahibinin**
S24 və 2017-dən sonrakı Android telefonların demək olar hamısı arm64-dür. armeabi-v7a yalnız köhnə və ya çox ucuz
32-bit telefonlar üçündür. Yalnız arm64: ≈ 40 MB (−30 MB). Risk: 32-bit telefon APK-nı quraşdıra bilməz.

## 4. Yüklənmə

### 4.1 Test serverindən endirmə (Caddy, `deploy/test-server/Caddyfile`)
Ölçülüb (2026-10-22, 85,7 MB-lıq köhnə APK): `Content-Type: application/vnd.android.package-archive`,
`Accept-Ranges: bytes` (Range sorğusu 206 qaytarır — kəsilən endirmə davam edir), ETag, `Cache-Control: no-store`.
Endirmə bu kompüterdən 26,6 s (≈ 3,2 MB/s ≈ 26 Mbit/s — məhdudiyyət bu xətdədir; 100 Mbit/s-də 70,6 MB ≈ 6 s).
Caddy `file_server` faylı Linux-da sendfile ilə verir (nginx-in `sendfile` / `tcp_nopush` parametrləri burada
aid deyil); APK artıq sıxılmışdır, `encode` yoxdur — düzgündür.
Dəyişiklik: `no-store` → `no-cache` (ETag ilə yoxlanılır, eyni build 304 alır) və `Content-Disposition:
attachment; filename="neoprint-test.apk"`.

### 4.2 Soyuq açılış (emulyator, x86_64 release, `am start -W`, `TotalTime`)
| Build | Ölçmələr, ms | Median |
|---|---|---|
| Xəritədən əvvəl (`335ca50`) | 7851 8477 8553 12098 9148 7886 7387 | ≈ 8 500 |
| Xəritə ilə | 9299 9182 9836 9453 9605 7998 8136 7935 | ≈ 9 200 |

Emulyator proqram renderi ilə və Docker stack-i ilə resurs bölüşərək işləyir: səpələnmə ±2 s-dir, mütləq rəqəmlər
real telefon üçün göstərici deyil (Docker-siz ilk ölçmə ≈ 4,5 s idi). Fərq səpələnmə daxilindədir.
**Açılışda xəritə yüklənmir:** `maplibre_gl` açılışda yalnız kanalı qeydiyyatdan keçirir (`onAttachedToEngine`),
`MapLibre.getInstance` və `libmaplibre.so` xəritə yaradılanda yüklənir; logcat-da açılışda MapLibre sətri yoxdur.
`main.dart`-da sinxron inisializasiya yoxdur (Firebase push xidmətində sonradan). Lazy etmək üçün iş tapılmadı.
Real telefonda (S24) ölçmə — MT-07 ilə.

## 5. Tətbiq olunanlar (T-292)
1. ABI süzgəci (`app/build.gradle.kts`, `gradle.properties`) — −17,2 MB.
2. Deploy: `--target-platform android-arm,android-arm64` (T-287) + `--split-debug-info`, simvollar
   `neoprint-test-apk-symbols` artefaktında (90 gün) — −3,4 MB; APK ölçüsü workflow xülasəsində.
3. Caddy: `no-cache` + `Content-Disposition`.

Edilmədi (qərar tələb edir): ML Kit unbundled (3.1), yalnız arm64 (3.6), şrift subset (3.2).
