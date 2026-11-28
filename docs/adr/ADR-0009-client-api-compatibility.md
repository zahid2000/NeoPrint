# ADR-0009 — Müxtəlif versiyalı kiosk və tətbiqlər üçün API uyğunluğu (T-489)

### Status

Accepted — 2026-11-28, layihə sahibinin qərarı (T-489: "Api ilə bağlı təkliflərini qəbul edirəm").

### Context

Backend bir anda yenilənir, onun klientləri isə yox:

- **Kiosk** yeniləməni mərhələli alır (Pilot → faiz → hamısı, `STATE_MACHINES.md` "Release rollout"); yayım dayandırıla və ya geri qaytarıla bilər, kiosk günlərlə oflayn qala bilər. Deməli eyni anda sahədə bir neçə kiosk versiyası olur.
- **Provisioning skriptləri** flaşdan işləyir — flaşdakı skript aylarla köhnə qala bilər.
- **Mobil tətbiqi** istifadəçi özü yeniləyir — köhnə versiya aylarla işləyə bilər.
- Backend gündüz deploy oluna bilər, kiosk paketi isə axşam çıxır.

Backend köhnə klientin işlətdiyi endpoint-i silsə, adını və ya cavabın sahəsini dəyişsə, sahədəki kiosk ödəniş, çap və ya yeniləmə zamanı sınır. Yeniləmə özü də API ilə gəldiyi üçün kiosk özünü düzəldə bilməyə də bilər.

İndiyə qədər bu, yazılı qayda deyildi. Yeni sahələr könüllü idi, kiosk naməlum sahəni buraxırdı (`System.Text.Json`), Mongo artıq sahələrə baxmır (`IgnoreExtraElementsConvention`) — amma bunu heç nə yoxlamırdı.

### Decision

**1. Əsas qayda.** Backend sahədəki **ən köhnə dəstəklənən** klientlə işləməlidir. Backend həmişə birinci yenilənir, klientlər sonra gəlir.

**2. Sındıran dəyişiklik üç ayrı buraxılışdadır — genişləndir → köçür → daralt.**

| Mərhələ | Backend | Klient |
|---|---|---|
| Genişləndir | Yeni endpoint / sahə əlavə olunur, köhnəsi işləyir | Köhnə klient köhnəni, yeni klient yenini işlədir |
| Köçür | Hər ikisi işləyir | Klientlər tədricən yenilənir |
| Daralt | Köhnə endpoint / sahə yalnız **dəstəklənən minimum versiya** ondan sonrakı olanda silinir | — |

**3. Konkret qaydalar.**

- Klientin işlətdiyi endpoint, metod, cavab sahəsi və SignalR hadisəsi **silinmir, adı dəyişdirilmir, tipi və mənası dəyişdirilmir.** Yenisi əlavə olunur.
- Sorğuya əlavə olunan yeni sahə **könüllüdür.** Köhnə klient onu göndərmir, backend default dəyər götürür.
- Cavaba yeni sahə əlavə etmək olar. Klient tanımadığı sahəni buraxır (bu, klient tərəfdə də qaydadır).
- Enum-a yeni dəyər əlavə etmək köhnə klient üçün naməlum dəyərdir. Klient naməlum dəyəri sındırmadan qəbul etməlidir. Belə dəyər yalnız feature flag açılandan sonra göndərilir.
- Yeni davranışı **feature flag** açır: kod deploy olunur, funksiya klientlər yeniləndikdən sonra açılır (nümunə: `kiosk_camera`).
- Xəta kodları (məs. `camera.archiveOff`) dəyişdirilmir. Köhnə klient tanımadığı kodu ümumi xəta kimi göstərir.
- **Baza ikitərəfli:** yeni backend köhnə formatı oxuyur. Köhnə backend də yeni yazılanı oxuya bilməlidir, çünki backend geri qaytarıla bilər.
- **Kiosku backend heç vaxt bloklamır.** Kioskun yeniləməsi də API ilə gəlir — köhnə kiosk bloklansa, özünü yeniləyə bilməz. Köhnə kiosk üçün yalnız admin paneldə xəbərdarlıq olur.
- **Mobil tətbiq** üçün dəstəklənən minimum versiyadan köhnə tətbiq "tətbiqi yeniləyin" ekranı görür (`426`). Mobil köhnə versiyalar daha uzun dəstəklənir — mağazadan yeniləmə istifadəçidən asılıdır.
- Admin paneli backend ilə birlikdə deploy olunur və bu qaydaya tabe deyil.

**4. Müqavilə faylı — `contracts/client-api.json` (T-491).**

- Klientlərin (kiosk, provisioning skriptləri, mobil) işlətdiyi hər endpoint (metod + yol) və SignalR hadisəsi bu faylda qeyd olunur.
- `minSupported` — hər klient üçün dəstəklənən minimum versiya.
- Klient endpoint-i işlətməyi dayandıranda sətir **silinmir.** Ona `retiredIn` yazılır: hansı klient versiyasından bəri işlədilmir. Backend həmin endpoint-i yalnız `minSupported` ≥ `retiredIn` olanda silə bilər.
- Sətir faylda yalnız `minSupported` ≥ `retiredIn` olandan sonra silinə bilər.
- **CI yoxlayır:**
  - klientin kodunda işlədilən hər yol faylda var;
  - faylda aktiv olan hər endpoint backend-də var;
  - aktiv endpoint-lərin cavab sahələri silinməyib və tipi dəyişməyib (snapshot);
  - aktiv hadisəni backend hələ göndərir.

**5. Dəstəklənən minimum versiya (T-492).**

- Mənbə eynidir: `contracts/client-api.json` → `minSupported`, backend-in içinə yerləşdirilir.
- Admin panel bu versiyadan köhnə kiosklar üçün xəbərdarlıq göstərir.
- Mobil üçün admin ayarı da var. Əsl hədd ayar ilə fayldakı dəyərin böyüyüdür, yəni ayar həddi yalnız qaldıra bilər.
- `minSupported`-u qaldırmazdan əvvəl admin → "Versiyalar" səhifəsində ondan köhnə kiosk qalmadığı yoxlanılır.

### Alternatives

- **URL ilə API versiyaları (`/api/v1`, `/api/v2`).** Hər sındıran dəyişiklik bütöv yeni versiya tələb edir, iki nüsxə kod saxlanılır. Bizim dəyişikliklər əksərən bir sahə və ya bir endpoint-dir. Rədd edildi. Böyük yenidənqurma lazım olanda ayrıca ADR ilə yenidən baxılır.
- **Yalnız yazılı qayda, yoxlamasız.** Unudulur — elə buna görə sual yarandı. Rədd edildi.
- **OpenAPI faylını tam müqayisə etmək.** Swagger yalnız Development-də qeydiyyatdadır, admin API-si də daxil olardı və hər dəyişiklik CI-ı qırmızı edərdi. Yalnız klientlərin işlətdiyi endpoint-lər lazımdır. Rədd edildi.
- **Köhnə kiosku backend-də bloklamaq.** Yeniləmə də API ilə gəlir — kiosk özünü düzəldə bilməz. Rədd edildi.

### Consequences

- Müsbət: backend və kiosk buraxılışları istənilən ardıcıllıqla və vaxtda çıxa bilər; yarımçıq yayım və geri qaytarma təhlükəsizdir; sındıran dəyişiklik CI-da görünür.
- Mənfi: köhnə endpoint və sahələr bir müddət kodda qalır. Klientin yeni endpoint işlətməsi müqavilə faylına sətir tələb edir (CI xatırladır).
- Yoxlama yalnız yolu, metodu və cavab sahələrinin adlarını və tiplərini görür. Sahənin **mənasının** dəyişməsini görmür — bu, kod baxışında yoxlanılır.

### Security Impact

Yoxdur. Təhlükəsizlik düzəlişi üçün endpoint dərhal dəyişdirilə və ya bağlana bilər. Bu halda qayda pozulur, `DECISIONS.md`-yə yazılır və kiosklara təcili yeniləmə (`urgent`) göndərilir.

### Operational Impact

- Deploy ardıcıllığı: backend → kiosk paketi (Pilot → faiz → hamısı) → sonrakı buraxılışlarda köhnə yolun silinməsi.
- Admin → "Versiyalar": dəstəklənməyən versiyalı kiosklar görünür.

### Migration

Mövcud endpoint-lər müqavilə faylına ilk dəfə klientlərin indiki kodundan yazılır. Bütün `minSupported` dəyərləri indiki sahə versiyalarıdır.

### Related Plan Step

Addım 7B (yeniləmə və yayım), Addım 8. Tapşırıqlar T-489–T-492.

### Date

2026-11-28
