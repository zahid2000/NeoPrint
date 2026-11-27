# iOS: test build-i və iPhone-da yoxlama (T-468, T-469)

Araşdırma: 2026-11-26 (T-468). Qurulum: 2026-11-27 (T-469). Plan iOS üçün yalnız **build** tələb edir, App Store publikasiyası scope xaricindədir (plan 15).

Bizim yol: Mac olmadan **GitHub macOS runner** tətbiqi yığır, imzalayır və App Store Connect-ə yükləyir. Oradan tətbiq **TestFlight**-ın daxili testçilərinə gedir. App Store-da heç nə dərc olunmur. `PILOT.md` 11-ci bənd də budur.

## 1. Nə hazırdır (T-469)

| Nə | Harada |
|---|---|
| **İmzasız iOS build-i hər push-da** (Swift kodumuzun və plugin-lərin kompilyasiyası) | `ci.yml` → `ios (unsigned build)`, `macos-26` runner-i, Xcode 26. İlk run 2026-11-27-də yaşıl oldu (Xcode 26.6), o vaxtdan bloklayıcıdır |
| **TestFlight build-i** — test serverinə qoşulan tətbiq | "Deploy (test server)" workflow-u → `ios` seçimi |
| **İmza qurulumu skripti** — sertifikat, profil, GitHub secret-ləri | `tools/ios/setup-signing.mjs` |
| Minimum iOS **15.5** (ML Kit tələbi), `Podfile` | `mobile/ios` |
| Şifrələmə sualı avtomatik cavablanır | `Info.plist`: `ITSAppUsesNonExemptEncryption = false` (bizdə yalnız HTTPS var) |
| **Push hazırlığı** | `Runner.entitlements` (`aps-environment`), `Info.plist`-də `UIBackgroundModes: remote-notification`. Tətbiq APNs tokenini gözləyib Firebase tokenini alır. Backend APNs hissəsini artıq göndərir (`FcmPushSender`) |
| Firebase iOS konfiqurasiyası | `firebase_options.dart`-da `ios`, Bundle ID `az.neoprint.neoprint` |

**Bundle ID** `az.neoprint.neoprint` qalır. İstifadəçi onu görmür. Ad dəyişikliyi (T-336) yalnız görünən adı (`CFBundleDisplayName`) və App Store Connect-dəki adı dəyişir, onlar sonra da dəyişdirilə bilər.

## 2. Layihə sahibinin etməli olduqları

Bu addımlar bir dəfə edilir. Açar fayllarını (`.p8`) söhbətə yazmayın. Faylı kompüterdə saxlayın və mənə yalnız **yolunu** deyin.

### Addım 1 — Apple Developer Program ($99/il)

1. iPhone-dakı Apple ID-də **iki addımlı doğrulama** açıq olmalıdır.
2. <https://developer.apple.com/programs/enroll/> səhifəsinə daxil olun. Ən rahatı iPhone-da **Apple Developer** tətbiqindən qeydiyyatdır.
3. Hesab növünü seçin:
   - **Fərdi (Individual):** adətən 1–2 günə açılır. Hələlik test üçün bu kifayətdir.
   - **Təşkilat (Organization):** D-U-N-S nömrəsi tələb edir, həftələrlə çəkə bilər.

   Fərdi hesab sonra təşkilata köçürülə bilər.
4. Ödənişdən sonra "Welcome" məktubunu gözləyin.

### Addım 2 — App Store Connect API açarı

1. <https://appstoreconnect.apple.com> → **Users and Access** → **Integrations** → **App Store Connect API** → **Team Keys** → **+**.
2. Ad: `NeoPrint CI`. Giriş: **Admin**. Sertifikat və profil yaratmaq üçün bu rol lazımdır.
3. **Download API Key**: `.p8` faylı **yalnız bir dəfə** endirilir. Onu məsələn `C:\Users\<siz>\keys\` qovluğunda saxlayın. Bu qovluq repo daxilində olmamalıdır.
4. Səhifədə görünən iki dəyəri qeyd edin:
   - **Key ID** (10 simvol);
   - **Issuer ID** (cədvəlin üstündə, UUID).
5. Mənə yazın: "açar hazırdır", faylın **yolu**, Key ID və Issuer ID.

Bundan sonra mən bu kompüterdə əvvəlcə yoxlama rejimində (`--apply`-sız), sonra qurulum rejimində (`--apply` ilə) bu əmri işlədəcəyəm:

```bash
node tools/ios/setup-signing.mjs --key <yol>/AuthKey_XXXX.p8 --key-id XXXX --issuer-id <uuid>
```

Skript bunları edir:
- Bundle ID-ni qeydiyyatdan keçirir və Push Notifications-ı açır;
- sertifikatı və profili yaradır;
- 6 GitHub secret-i yazır.

Açar repoya düşmür, ekrana çıxmır.

### Addım 3 — App Store Connect-də tətbiq qeydi

Bunu Addım 2-dən **sonra** edin, çünki Bundle ID siyahıda o zaman görünür.

1. <https://appstoreconnect.apple.com> → **Apps** → **+** → **New App**.
2. Sahələri doldurun:
   - Platform **iOS**;
   - Name: məs. `NeoPrint Test`. Ad App Store-da unikal olmalıdır, sonra dəyişdirilə bilər;
   - Primary Language: **Azerbaijani** (siyahıda yoxdursa English);
   - Bundle ID: **az.neoprint.neoprint**;
   - SKU: `neoprint-test`;
   - User Access: Full Access.
3. **Create**. Qalan sahələri (şəkillər, təsvir) doldurmaq lazım deyil: onlar yalnız App Store üçündür.

### Addım 4 — TestFlight testçiləri

1. Tətbiq → **TestFlight** → **Internal Testing** → **+**, qrup adı: `NeoPrint`.
2. Özünüzü (və App Store Connect-ə əlavə etdiyiniz digər insanları) qrupa əlavə edin. Daxili testçi App Store Connect istifadəçisi olmalıdır, ən çox 100 nəfər ola bilər.
3. "Automatic distribution" açıq qalsın: hər yeni build qrupa özü gedir.

### Addım 5 — Push bildirişləri (APNs açarı → Firebase)

1. <https://developer.apple.com/account/resources/authkeys/list> → **+**.
2. Ad `NeoPrint Push`, **Apple Push Notifications service (APNs)** seçin. Environment: **Sandbox & Production**.
3. **Continue** → **Register** → **Download**. Bu `.p8` faylı da bir dəfə endirilir. Key ID-ni qeyd edin.
4. **Team ID**: <https://developer.apple.com/account> → Membership details.
5. <https://console.firebase.google.com> → NeoPrint layihəsi → ⚙ **Project settings** → **Cloud Messaging** → **Apple app configuration** (`az.neoprint.neoprint`) → **APNs Authentication Key** → **Upload**: faylı, Key ID-ni və Team ID-ni daxil edin.

Bu açarı GitHub-a yazmaq lazım deyil: push Firebase üzərindən gedir.

### Addım 6 — iPhone

1. App Store-dan **TestFlight** tətbiqini quraşdırın (Apple-ın öz tətbiqidir).
2. İlk build yükləndikdən sonra e-poçtunuza dəvət gəlir. Onu iPhone-da açın, **Accept** → TestFlight-da **Install** basın.
3. Sonrakı build-lər TestFlight-da "Update" kimi görünəcək.

## 3. Claude-un edəcəkləri (Addım 2-dən sonra)

1. `setup-signing.mjs`-i işlətmək (əvvəlcə yoxlama, sonra `--apply`).
2. "Deploy (test server)" workflow-unu `ios=true` ilə işə salmaq. İlk dəfə imza və ya Swift xətaları çıxa bilər, onları düzəldib yenidən işə salmaq.
3. Build App Store Connect-də emal olunandan sonra (5–30 dəqiqə) sizə xəbər vermək.
4. Sonrakı deploy-larda iOS build-ini **sizin istəyinizlə** etmək. Hər push-da TestFlight-a yükləmə olmur, çünki macOS build-i uzun çəkir və Apple build-ləri emal etməlidir.

## 4. iPhone-da yoxlama siyahısı (MT-09-un iOS hissəsi)

| # | Nə | Gözlənən |
|---|---|---|
| 1 | Qeydiyyat / giriş, PIN | Açılır, PIN saxlanır (Keychain) |
| 2 | Face ID ilə açmaq | Face ID sorğusu, "NeoPrint-i PIN yazmadan açmaq üçün" mətni |
| 3 | Bildiriş icazəsi və push | İcazə sorğusu. Paneldən göndərilən bildiriş tətbiq bağlı olanda da gəlir. Toxunanda düzgün ekran açılır |
| 4 | Kamera: tək və bir neçə şəkil (T-466, T-470) | "Şəkil çək" və "Hazırdır" düymələri görünür |
| 5 | QR oxumaq (ML Kit) | Kioskun ödəniş QR-ı oxunur |
| 6 | Skaner (VisionKit) | Sənəd kənarları tapılır, səhifələr gəlir |
| 7 | Fayl seçimi (bir neçə fayl), HEIC şəkil | Yüklənir, HEIC çevrilir |
| 8 | Office redaktoru (WKWebView, T-459) | Sənəd açılır, saxlanır |
| 9 | Paylaşma, brauzerdə açmaq | Sistem paylaşma pəncərəsi, Safari |
| 10 | `neoprint://` linkləri | Tətbiq açılır, düzgün ekrana gedir |
| 11 | Xəritə və məkan | İcazə sorğusu, yaxın kiosklar |

## 5. Risklər

- **Swift kodu ilk dəfə kompilyasiya olunur.** İlk CI run-larında xətalar çıxa bilər. Onlar CI-da görünür və düzəldilir, amma işləməyi yalnız real iPhone təsdiqləyir.
- **ML Kit (QR) və VisionKit simulyatorda tam işləmir.** Ona görə yoxlama TestFlight və real iPhone ilə edilir.
- **Sertifikat və profil 1 il işləyir.** Bitəndə `setup-signing.mjs --apply` yenidən işlədilir. Apple-da ən çox 2–3 distribution sertifikatı ola bilər, köhnəsi developer.apple.com → Certificates-də ləğv olunur.
- **TestFlight build-i 90 gün işləyir.** Ondan sonra yeni build lazımdır.
- **SDK 26 ilə yığılan tətbiqdə sistem elementləri yeni görünüş (Liquid Glass) alır.** Bizim UI Flutter-dir, dəyişiklik əsasən sistem dialoqlarına aiddir.

Mənbələr:
- [Apple — SDK minimum tələbləri](https://developer.apple.com/news/upcoming-requirements/)
- [Apple — TestFlight icmalı](https://developer.apple.com/help/app-store-connect/test-a-beta-version/overview-of-testflight/)
- [Apple — App Store Connect API açarı](https://developer.apple.com/documentation/appstoreconnectapi/creating-api-keys-for-app-store-connect-api)
- [Firebase — APNs açarının yüklənməsi](https://firebase.google.com/docs/cloud-messaging/ios/certs)
- [GitHub — Actions ödənişi (açıq repolarda standart runner-lər pulsuzdur)](https://docs.github.com/en/billing/managing-billing-for-github-actions/about-billing-for-github-actions)
