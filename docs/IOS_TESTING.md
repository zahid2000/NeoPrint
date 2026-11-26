# iOS: test build-i və iPhone-da yoxlama (T-468)

Araşdırma: 2026-11-26. Plan iOS üçün yalnız **build** tələb edir, App Store publikasiyası scope xaricindədir (plan 15). Burada məqsəd **TestFlight** vasitəsilə iPhone-da test etməkdir. `PILOT.md` 11-ci bənd də budur.

## 1. Repo-nun hazırkı vəziyyəti

| Nə | Vəziyyət |
|---|---|
| `mobile/ios` layihəsi | Var. Bundle ID `az.neoprint.neoprint`, minimum iOS 15.0, imza "Automatic", komanda (Team) yazılmayıb |
| Öz Swift kodumuz (`AppDelegate.swift`, 436 sətir) | 6 kanal: `secure_storage` (Keychain), `biometrics` (Face ID), `document_scanner` (VisionKit), `heic`, `share`, `browser`. **Heç vaxt kompilyasiya olunmayıb** (`DECISIONS.md` 2026-10-05): bu maşında Xcode yoxdur, CI-da macOS job-u yoxdur |
| iOS-da olmayan kanallar | `cloud_picker`, `file_saver`. Dart tərəfi bunları yalnız Android-də çağırır, iOS-da sistemin fayl seçicisi və paylaşma işləyir. Xəta vermir |
| İcazə mətnləri (`Info.plist`) | Kamera, foto kitabxanası, məkan, Face ID var. `neoprint://` sxemi var |
| Firebase iOS konfiqurasiyası | `firebase_options.dart`-da `ios` var |
| Push (APNs) | **Hazır deyil**: entitlements faylı (`aps-environment`) yoxdur, `UIBackgroundModes: remote-notification` yoxdur, Firebase-ə APNs açarı yüklənməyib |
| `ITSAppUsesNonExemptEncryption` | Yoxdur. Əlavə olunmasa, hər build-də şifrələmə sualı əl ilə cavablandırılmalı olur (bizdə yalnız HTTPS var, yəni `false`) |
| Test serverinin ünvanı | APK-da `--dart-define=NEOPRINT_API_URL=https://<server>` ilə verilir. iOS build-i də eyni yolla almalıdır |
| CocoaPods / Swift Package Manager | `Podfile` yoxdur. Mac-da ilk `flutter build ios` lazım olanı özü yaradır |

## 2. Apple tərəfinin tələbləri (2026)

- **Apple Developer Program:** ildə $99. Onsuz TestFlight yoxdur. Pulsuz Apple ID ilə yalnız Mac + Xcode + kabel ilə öz telefonuna 7 günlük quraşdırma mümkündür, bizdə Mac olmadığı üçün bu yol yoxdur.
  - **Fərdi** qeydiyyat: tez olur, App Store-da şəxsin adı görünür.
  - **Təşkilat** qeydiyyatı: D-U-N-S nömrəsi tələb edir, günlərlə, bəzən həftələrlə çəkir, şirkətin adı görünür.
- **SDK tələbi:** 28 aprel 2026-dan App Store Connect-ə yüklənən build-lər **Xcode 26 və iOS 26 SDK** ilə yığılmalıdır. Minimum iOS versiyası (bizdə 15.0) dəyişmir.
- **TestFlight:**
  - **daxili testçilər** (App Store Connect istifadəçiləri): ən çox 100 nəfər, Apple-ın yoxlaması yoxdur, build emal olunan kimi əlçatandır;
  - **xarici testçilər:** ən çox 10 000 nəfər, ilk build Apple-ın Beta App Review yoxlamasından keçir;
  - hər build 90 gün işləyir.

## 3. Mac olmadan build: GitHub Actions macOS runner

Repo 2026-10-11-dən açıqdır (public). Açıq repolarda standart GitHub runner-ləri, o cümlədən macOS, **pulsuzdur**. Ona görə `DECISIONS.md`-dəki "macOS runner baha başa gəlir" səbəbi artıq keçərli deyil.

Təklif olunan iki iş:

1. **CI-a imzasız iOS build-i** (`flutter build ios --no-codesign`, hər push-da). Heç bir hesab tələb etmir. Swift kodumuzun kompilyasiya olunduğunu ilk dəfə yoxlayır. Apple hesabı alınmamışdan əvvəl də edilə bilər.
2. **"iOS test (TestFlight)" workflow-u** (əl ilə işə salınır):
   - `flutter build ipa` test serverinin ünvanı ilə yığılır;
   - imza üçün App Store Connect API açarı və avtomatik imza (`-allowProvisioningUpdates`) işlədilir;
   - build TestFlight-a yüklənir.

   Açar GitHub secret-lərində saxlanır, repoya yazılmır. Lazım olan secret-lər: açar faylı (`.p8`), Key ID, Issuer ID, Team ID.

## 4. Layihə sahibinin etməli olduqları

1. Apple Developer Program-a qeydiyyat ($99/il): fərdi və ya təşkilat.
2. App Store Connect-də:
   - **API açarı** yaratmaq (Users and Access → Integrations, rol "App Manager");
   - **Team ID**-ni götürmək;
   - açarı və ID-ləri GitHub secret-lərinə yazmaq. Mən bunun üçün əmrləri verəcəyəm, açarı söhbətə yazmayın.
3. **Bundle ID-ni qəti seçmək.** App Store Connect-də tətbiq yaradılandan sonra Bundle ID dəyişdirilmir. Hazırda `az.neoprint.neoprint` yazılıb, amma ad dəyişikliyi (T-336) hələ gözləyir.
4. App Store Connect-də tətbiq qeydini yaratmaq. Ad sonra dəyişdirilə bilər.
5. Özünü daxili testçi kimi əlavə etmək, iPhone-a **TestFlight** tətbiqini quraşdırmaq.
6. Push üçün (sonra): APNs açarını (`.p8`) Firebase layihəsinə yükləmək.

## 5. Claude-un edəcəkləri

1. CI-a imzasız iOS build job-u əlavə etmək. Çıxan Swift xətalarını düzəltmək.
2. `Info.plist`-ə `ITSAppUsesNonExemptEncryption = false` əlavə etmək. Push hazırlığı: entitlements faylı və `remote-notification`.
3. TestFlight workflow-unu yazmaq və `docs/TEST_SERVER.md`-yə iOS bölməsi əlavə etmək.
4. iPhone-da yoxlama siyahısını hazırlamaq (`MANUAL_TESTS.md` MT-09-un iOS hissəsi): PIN, Face ID, Keychain, kamera, QR, VisionKit skaneri, Office redaktoru (WKWebView), `neoprint://` linkləri.

## 6. Risklər

- **Swift kodu heç vaxt kompilyasiya olunmayıb.** İlk build-də xətalar çıxacaq. Onları CI-da görüb düzəltmək olar, amma işləməsini yalnız real iPhone yoxlayır.
- **Bəzi şeylər simulyatorda yoxlanmır:** ML Kit (QR) və VisionKit skaneri simulyatorda tam işləmir. Ona görə TestFlight və real iPhone lazımdır.
- **SDK 26 ilə tətbiq UI görünüşü (Liquid Glass) dəyişə bilər.** iOS 26 SDK ilə yığılan tətbiqdə sistem elementləri yeni görünüş alır. Bizim UI Flutter-dir, ona görə əsasən sistem dialoqlarına aiddir.
- **Bundle ID sonra dəyişdirilmir.** Ad dəyişikliyi (T-336) ilə toqquşmasın deyə qərar tətbiq qeydi yaradılmamışdan əvvəl verilməlidir.

Mənbələr: [Apple — SDK minimum tələbləri](https://developer.apple.com/news/upcoming-requirements/), [Apple — TestFlight icmalı](https://developer.apple.com/help/app-store-connect/test-a-beta-version/overview-of-testflight/), [GitHub — Actions ödənişi (açıq repolarda standart runner-lər pulsuzdur)](https://docs.github.com/en/billing/managing-billing-for-github-actions/about-billing-for-github-actions).
