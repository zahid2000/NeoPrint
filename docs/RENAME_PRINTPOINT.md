# Ad dəyişikliyi: NeoPrint → PrintPoint (M22, T-336) — plan

**Vəziyyət: təxirə salınıb (2026-11-08).** Layihə sahibi PrintPoint adını ləğv etdi; heç nə dəyişdirilməyib. Yeni ad qərarı gələndə bu plan həmin adla yenilənir və yenidən təsdiqə təqdim olunur. Çərçivə — `STEP_7B_PLAN.md` §4.

**Prinsip:** istifadəçinin gördüyü hər şey və xarici identifikatorlar (mağaza, domen, link) dəyişir; daxili
identifikatorlar (kod adları, baza, secret yolları, kiosk cihazındakı yollar) **qalır** — onları dəyişmək data və
secret köçürməsi, quraşdırılmış kiosklarda miqrasiya tələb edər, istifadəçiyə isə heç nə vermir.

## 1. Dəyişən — istifadəçinin gördüyü mətnlər

| Harada | Nə | Təxmini yer sayı |
|---|---|---|
| Backend mesaj kataloqu (`messages.az/en/ru.json`) | Email şablonları (qeydiyyat kodu, qəbz, incident), push başlıqları, xəta mətnlərində ad | ~28 × 3 dil |
| Backend kodu | Email göndərənin adı (`NEOPRINT_SMTP_FROM_NAME` default-u), qəbz PDF-inin başlığı, Hangfire panelinin başlığı | 3 |
| Admin panel | `<title>`, giriş ekranı və yan paneldəki ad, PWA manifest (`name`: "PrintPoint operator", `short_name`), tərcümə faylları, qəbz | ~10 fayl |
| Mobil tətbiq | `app_az/en/ru.arb` mətnləri, splash ekranı, ödəniş / skan ekranlarındakı ad, Android launcher adı (`android:label`), iOS `CFBundleDisplayName` | ~12 × 3 dil + 2 |
| Kiosk | Pəncərə başlığı, yuxarı paneldəki ad, açılış ekranı, `Strings.cs` mətnləri (az/en/ru) | ~16 + 3 |
| Sənədlər (oxucu üçün) | `KIOSK_SETUP.md`, `PILOT.md`, README, hüquqi sənədlərin qaralamaları, gələcək `ADMIN_GUIDE` / `OPERATOR_GUIDE` | — |

## 2. Dəyişən — xarici identifikatorlar (qərar lazımdır)

| Nə | İndi | Təklif | Qeyd |
|---|---|---|---|
| Android `applicationId` / `namespace` | `az.neoprint.neoprint` | **`az.printpoint.app`** | Google Play-ə ilk yükləmədən sonra **heç vaxt dəyişmir** (T-275). Test telefonlarında köhnə tətbiq silinib yenisi quraşdırılır; Firebase-də yeni Android tətbiqi + yeni `google-services.json` |
| iOS bundle id | `az.neoprint.neoprint` | **`az.printpoint.app`** | Apple hesabı olanda (T-273) |
| Deep link sxemi | `neoprint://` | **`printpoint://`** əlavə olunur, `neoprint://` **alias kimi qalır** | Çap olunmuş QR-lar və köhnə linklər sınmasın |
| Domen | `neoprint.az` (default-larda: referral linki, refund QR, `noreply@`) | **`printpoint.az`** (mövcudluğu layihə sahibi yoxlayır) | M21 (domen, SMTP, SPF/DKIM) bu adla edilir; App Links (`/r/`) yeni domenlə |
| Email göndərən | `noreply@neoprint.az` | `noreply@printpoint.az` | M21-də |

## 3. Dəyişməyən — daxili identifikatorlar

- .NET solution və namespace-lər (`NeoPrint.*`), Dart paket adı, Angular layihə adı, GitHub repo adı.
- Mongo bazası, kolleksiyalar, RabbitMQ növbələri, OpenBao yolları (`neoprint/…`), mühit dəyişənləri (`NEOPRINT_*`).
- Kiosk cihazında: `/var/lib/neoprint`, `/opt/neoprint`, `C:\ProgramData\NeoPrint`, `neoprint` hesabı, systemd və
  Windows servis adları, buraxılış paketlərinin fayl adları.
- Cookie / `localStorage` açarları, JWT issuer / audience (dəyişsə hamı yenidən daxil olmalıdır).
- Docker konteyner və image adları.
- Texniki sənədlərdəki kod adları (`NeoPrint.Kiosk.Agent` və s.).

## 4. Ardıcıllıq və sübut

1. Təsdiqdən sonra: mətnlər (backend, admin, mobil, kiosk) — bir commit, `i18n-check`, testlər.
2. Android / iOS identifikatorları və deep link alias-ı — ayrıca commit; yeni APK test telefonunda quraşdırılır,
   köhnə `neoprint://` linki açılır.
3. Domen və email — M21-də, domen alınandan sonra.
4. Logo, ikon, splash — M23-də (redizayn), yeni adla bir dəfə.

**Sübut:** admin / mobil / kiosk walk-u az / en / ru-da köhnə ad görünmür (avtomatik `grep` + screenshot-lar);
APK yeni `applicationId` ilə quraşdırılır; köhnə `neoprint://` linki işləyir; CI yaşıl; test serverinə deploy.

## 5. Layihə sahibinə suallar

1. Yazılış hər dildə **"PrintPoint"** (bir söz, iki böyük hərf) — düzdürmü?
2. Android / iOS identifikatoru **`az.printpoint.app`** — razısınızmı? (Play-ə yüklənəndən sonra dəyişmir.)
3. Domen **`printpoint.az`** olacaqmı? Alınıbmı / boşdurmu?
4. Figma faylının adı "ZSPrint Kiosk"-dur (T-303) — dizayn PrintPoint adı ilə yenilənəcəkmi?
5. Admin panelin PWA adı "PrintPoint operator" — uyğundurmu?
