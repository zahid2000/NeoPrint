# Kioskun quraşdırılması — addım-addım

Bu sənəd yeni NeoPrint kioskunu sıfırdan işə salmaq üçündür və **texniki bilik az olan** şəxs üçün yazılıb: hər addımda
nə etmək, ekranda nə görmək və səhv olsa nə etmək yazılıb. Skriptlərin Windows / Linux-da etdiyi hər dəyişiklik ayrıca
`docs/KIOSK_PROVISIONING.md`-dədir.

Kiosk **həm Windows, həm Linux**-da işləyir. **Pilot cihazı Linux Ubuntu-dur** (T-332) — onu quraşdırırsınızsa
birbaşa **§11**-ə keçin (orada hər şey ardıcıl yazılıb; §4, §7, §8 iki platformada eynidir). Windows kompüteri üçün
yol §1–§9-dadır.

Kiosk **universal build**-dir: heç bir kiosk üçün ayrıca proqram, fayl və ya açar hazırlanmır. Kioskun adı, printeri,
qiymətləri, admin PIN-i — hamısı aktivləşdirmədən sonra serverdən gəlir (plan 7.11).

Bir kioskun quraşdırılması təxminən **1–2 saat** çəkir (Windows quraşdırması daxil).

---

## 0. Nə lazımdır

- Kiosk kompüteri, **kabel ilə internet** (Ethernet), printer eyni şəbəkədə (`docs/PILOT.md` — avadanlıq tələbləri).
- Quraşdırma vaxtı üçün **USB klaviatura və siçan**.
- **Quraşdırma flaşı** — NeoPrint komandası hazırlayır (§2). Flaşda:

  ```
  NEOPRINT (flaş)
  ├─ provisioning\     quraşdırma skriptləri (provision.ps1, check-windows.ps1, enroll.ps1, prepare-image.ps1, edition.ps1)
  ├─ package\          NeoPrint: app\, agent\, VERSION  (imzalı buraxılışdan, yoxlanmış)
  ├─ verifier\         yalnız -Download üçün: imza yoxlayıcısı (production açarlı Agent)
  └─ deps\             LibreOffice_*_x64.msi, gs*w64.exe (sənəd çevirənlər)
  ```

- Admin paneldə **`devices.enroll`** icazəsi olan hesab (qeydiyyat açarı yaratmaq üçün).

## 1. Cihaz gələndə: Windows nüsxəsini yoxlayın (5 dəqiqə)

Heç nə quraşdırmazdan və lisenziya almazdan **əvvəl**:

1. Cihazı yandırın, Windows-a daxil olun, flaşı taxın (məs. `D:`).
2. **Başlat** → `PowerShell` yazın → **"Run as administrator"** (Administrator kimi işə sal).
3. Bu əmri yazın (flaşın hərfi fərqlidirsə `D:` yerinə onu yazın) və **Enter**:

   ```powershell
   powershell -ExecutionPolicy Bypass -File D:\provisioning\check-windows.ps1
   ```

4. Ən aşağıdakı sətrə baxın:
   - **`RESULT: SUITABLE`** — cihaz kiosk ola bilər, §3-ə keçin;
   - **`RESULT: NOT SUITABLE`** — altındakı `-` ilə başlayan sətirlər səbəbi deyir. Ekranın şəklini çəkib NeoPrint
     komandasına göndərin.

Skript heç nə dəyişmir. Sətirlərin mənası:

| Sətir | Nə olmalıdır | Olmasa |
|---|---|---|
| `Windows` | `Windows 10 IoTEnterpriseS (build 19044...)` | Başqa nüsxədirsə (məs. `Professional`) — kiosk ola bilməz: Windows 10 IoT Enterprise LTSC 2021 quraşdırılmalıdır (`docs/PILOT.md` — lisenziya sualları) |
| `LTSC` | `yes` | `no` — adi (tez-tez böyük yeniləmə alan) nüsxədir; pilot üçün LTSC lazımdır |
| `Kiosk edition` | `yes` | `NO - ...` səbəbi yazılıb (Pro-da Shell Launcher və Keyboard Filter yoxdur) |
| `Activation` | `activated` | Windows aktivləşdirilməyib — satıcıdan lisenziya istəyin |
| `Intel generation` | `6 - Windows 11 is NOT supported...` | Bu normaldır: biz Windows **10** LTSC istifadə edirik |
| `Memory` | `8 GB` və ya çox | Az — kiosk yavaş işləyər |
| `System disk` | ≥ 40 GB boş | Az — yeniləmələr üçün yer çatmaz |
| `Screen` | `1280x1024` | Başqa ölçü — NeoPrint komandasına deyin |
| `Touch` | `yes (...)` | `NO touch screen found` — toxunma paneli kabelini / sürücüsünü yoxlayın |
| `Network` | `Ethernet (..., Up)` | Kabeli taxın |
| `TPM`, `Secure Boot` | `yes`, `on` | BIOS-da yandırılmalıdır (satıcıdan soruşun) |

## 2. Quraşdırma flaşının hazırlanması (NeoPrint komandası)

**Skript internetdən endirilib birbaşa işlədilmir** (`curl … | sh` və ya "bu linki PowerShell-ə yapışdırın" yolu yoxdur).
Səbəb: skript **Administrator / root** kimi işləyir — internetdən gələn skripti birbaşa işlətmək şəbəkəyə və serverə
kioskun tam idarəsini vermək deməkdir. Flaş imzalı buraxılışdan hazırlanır və flaşı hazırlayan özü imzanı yoxlayır:

1. GitHub → Actions → **"Release packages"** → son uğurlu işin **production** artefaktları
   (`kiosk-release-<versiya>-win-x64-production`).
2. Hər paketi yoxlayın (`docs/RELEASE_SIGNING.md` §4):

   ```powershell
   release-sign.exe verify --manifest <paket>.manifest.json --package <paket> --key-set production
   ```

   Hər biri `OK: ...` deməlidir. Deməsə — **flaşa yazmayın**.
3. Paketləri açın: `provisioning` paketi → flaşda `provisioning\`; `app` → `package\app\`, `agent` → `package\agent\`;
   `package\VERSION` faylına versiyanı yazın. `-Download` istifadə olunacaqsa `agent` paketi → `verifier\` də.
4. `deps\`: LibreOffice (libreoffice.org, x64 MSI) və Ghostscript (ghostscript.com, `gs*w64.exe`; AGPL — ayrıca proses
   kimi, dəyişdirilmədən işlədilir, plan 2.0).

Flaşda **heç bir gizli məlumat yoxdur**: qeydiyyat açarı flaşa yazılmır (o, §4-də paneldə yaradılır və quraşdırma vaxtı
yazılır), parol yoxdur.

## 3. Windows-un quraşdırılması

1. Cihazda Windows 10 IoT Enterprise LTSC 2021 yoxdursa — satıcıdan alınmış lisenziya ilə quraşdırın (və ya satıcı
   quraşdırsın). Quraşdırma vaxtı **interneti kabeldən** qoşun.
2. Öz **administrator hesabınızı** yaradın (məs. `neoprint-admin`) və parolunu parol menecerinə yazın — bu, sonra
   texniki giriş üçündür. Wi-Fi şəbəkəsi saxlamayın (golden image üçün vacibdir, §9).
3. Windows Update ilə bütün yeniləmələri quraşdırın, restart edin.
4. `check-windows.ps1`-i yenidən işə salın (§1) — `RESULT: SUITABLE` olmalıdır.

## 4. Qeydiyyat açarı (admin panel)

Açar kioskun **heç kim ekrana baxmadan** aktivləşməsi üçündür.

1. Admin panel → **Monitorinq → Cihazlar** → **"Qeydiyyat açarı yarat"**.
2. Seçin:
   - **Yeni kiosk(lar) üçün** — kioskun adı, ünvanı, admin PIN-i (rəqəm, ən azı 6), **neçə kiosk** (bir kiosk üçün
     `1`; golden image-in klonları üçün klonların sayı — §9);
   - **Mövcud kiosk üçün** — paneldə artıq yaradılmış, cihazı olmayan kiosk.
3. **Etibarlılıq**: quraşdırma bu gün olacaqsa **24 saat**.
4. **"Yarat"** → açar **yalnız bir dəfə** göstərilir: kopyalayın (yanındakı düymə). Dialoqu bağlayandan sonra onu
   heç kim görə bilmir — itirsəniz yenisini yaradın, köhnəsini **"Ləğv et"** edin.
5. Açarı e-poçtla / mesajla göndərməyin; quraşdırma edən şəxsə yanında verin və ya parol menecerinin paylaşımı ilə.

Siyahıda açarın vəziyyəti görünür: **Etibarlı**, **İstifadə olunub** (`1 / 1`), **Vaxtı keçib**, **Ləğv edilib**.

## 5. Quraşdırma skriptinin işə salınması (Windows)

1. Administrator hesabı ilə daxil olun, flaşı taxın.
2. **PowerShell → Run as administrator** və (flaşın hərfi `D:` deyilsə dəyişin):

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   D:\provisioning\provision.ps1 -ApiUrl https://api.neoprint.az -OfflinePackage D:\package -DependenciesPath D:\deps -Channel pilot -EnrollmentToken <AÇAR>
   ```

   `<AÇAR>` — §4-də kopyaladığınız açar (64 simvol, `0-9` və `a-f`).
3. Skript addımları yazır: `step 1/13: Windows edition` … `step 13/13: Shell Launcher and Keyboard Filter`.
4. **İlk qaçışda** skript Shell Launcher və Keyboard Filter-i yandırır və sonda
   **`provisioning is half done: restart, then run it again`** yazır. Bu normaldır: **restart** edin, yenə administrator
   ilə daxil olun və **eyni əmri** təkrar işə salın. İkinci qaçış qalanı edir (skript təkrar işə salına bilər —
   eyni şeyi iki dəfə etmir).
5. Uğurlu son: **`provisioning finished; a restart is required to enter kiosk mode`**. Restart edin.
6. Kompüter `kiosk` hesabı ilə **özü** daxil olur və ekranda **yalnız NeoPrint** görünür.

**Flaşda paket yoxdursa** (internetdən ən son buraxılış): `-OfflinePackage D:\package` əvəzinə `-Download` yazın.
Skript ən son **dərc olunmuş, production imzalı** buraxılışı serverdən endirir və hər paketin imzasını **flaşdakı**
`verifier\` ilə özü yoxlayır — yoxlamadan keçməyən paket quraşdırılmır. Server mənbədir, etibar mənbəyi deyil.

## 6. Quraşdırma xətaları — nə etməli

Səhv olsa skript dayanır və iki sətir yazır:

```
ERROR: step 10/13 (NeoPrint) failed: ...
what to do: ...
```

və **bu qaçışda dəyişdirdiyi Windows ayarlarını əvvəlki kimi qaytarır** (`put back as they were`; firewall addımında
firewall-un əvvəlki vəziyyəti də). Səbəbi aradan qaldırıb **eyni əmri yenidən** işə salın — skript qaldığı yerdən
düzgün davam edir. Aşağıdakı cədvəl addım nömrəsinə görə:

| Addım | Nədir | Səbəb (adətən) | Nə etməli |
|---|---|---|---|
| 1 | Windows nüsxəsi | Pro / Home quraşdırılıb | §1 — `check-windows.ps1`; LTSC 2021 lazımdır |
| 2 | Windows komponentləri | Windows Update işləyir, nüsxə uyğun deyil | Yeniləmə bitsin, restart, yenidən |
| 3 | Qovluqlar | Disk dolub | Yer açın (`System disk` §1) |
| 4 | Kiosk hesabı və avtomatik giriş | Sətirdə yazılıb | Ekranın şəklini NeoPrint-ə göndərin |
| 5 | Məlumat qovluğunun icazələri | Başqa proqram faylı tutub | Restart, yenidən |
| 6 | Qeydiyyat açarı | Disk dolub | Yer açın; açarı sonra `enroll.ps1` ilə də vermək olar (§9) |
| 7 | Ekranın bağlanması | Sətirdə yazılıb | Ekranın şəklini göndərin |
| 8 | Yeniləmə, çap, vaxt | Sətirdə yazılıb | Ekranın şəklini göndərin |
| 9 | Sənəd çevirənlər | `deps\`-də quraşdırıcı yoxdur / zədəlidir | Flaşı yenidən hazırlayın (§2) |
| 10 | NeoPrint | Paket natamamdır, endirilmədi və ya **imza yoxlamasından keçmədi** | İnterneti yoxlayın; flaşı yenidən hazırlayın (§2) |
| 11 | Agent xidməti | Sətirdə yazılıb | Restart, yenidən |
| 12 | Firewall | Başqa təhlükəsizlik proqramı | Əvvəlki firewall qaytarılıb; ekranın şəklini göndərin |
| 13 | Shell Launcher və Keyboard Filter | İlk qaçışdan sonra restart edilməyib | Restart, eyni əmri yenidən |

Tam jurnal: `C:\ProgramData\NeoPrint\provision.log` — problemi bildirəndə bu faylı əlavə edin (içində parol və açar yoxdur).

## 7. Aktivləşdirmə — yoxlayın

- **Açar ilə quraşdırılıbsa:** heç nə etməyin. Kiosk açılan kimi qeydiyyatdan keçir; admin paneldə **Kiosklar**-da
  yeni kiosk görünür (adı §4-dəki kimi, kodu `KSK-…`), **Cihazlar → Qeydiyyat açarları**-nda açar **İstifadə olunub**.
- **Açar olmadan və ya açar işləməyibsə** (vaxtı keçib, ləğv edilib): ekranda aktivləşdirmə kodu və QR görünür.
  Admin panel → **Cihazlar** → kodu daxil edin (və ya operator QR-ı skan edir) → yeni kiosk və ya mövcud kiosk,
  **admin PIN**, printer.

## 8. Printer və son yoxlama siyahısı

Admin paneldə və kioskun qarşısında, ardıcıl:

- [ ] Admin panel → kiosk → **Printerlər**: kioskun gördüyü printer siyahıdadır; onu seçin → **Test çap** → səhifə çıxdı.
      Kiosk **Online** oldu.
- [ ] Printer **şəbəkə** printeridir və panelde toner səviyyəsi görünür (USB printer toner göstərmir — `docs/PILOT.md`).
- [ ] Admin panel → kiosk → **Sərf**: lotoka kağız qoyun və sayını yazın (boş lotokla sifariş qəbul olunmur).
- [ ] Kioskda flaşdan **bir PDF** və **bir DOCX** seçin, baxışda səhifələr düzgün görünür, çap edin.
- [ ] Ekrana barmaqla toxunun: bütün düymələr ilk toxunuşda işləyir; mətn 1–1,5 m-dən oxunur.
- [ ] Klaviaturada **Ctrl+Alt+Del**, **Windows** düyməsi, **Alt+Tab** — heç nə açılmır.
- [ ] Kompüteri **söndürüb yandırın**: ekranda yenə yalnız NeoPrint açılır, kiosk paneldə **Online**.
- [ ] Şəbəkə kabelini 1 dəqiqəlik çıxarın: kiosk "əlaqə yoxdur" deyir, kabel taxılanda özü qayıdır.

## 9. Golden image — bir neçə eyni kiosk

Bir kioskun quraşdırılmasını (§3–§5) bir dəfə edib qalanlarını **disk surətindən** (image) çıxarmaq üçündür.

1. Bir kompüteri §3–§5 kimi quraşdırın, amma **qeydiyyat açarı vermədən** (`-EnrollmentToken` yazmayın) və **aktivləşdirmədən**.
2. Administrator PowerShell-də:

   ```powershell
   D:\provisioning\prepare-image.ps1 -Check        # nəyin qaldığını deyir, heç nə dəyişmir
   D:\provisioning\prepare-image.ps1 -RunSysprep   # təmizləyir, yoxlayır, sysprep; kompüter sönür
   ```

   Skript cihaz açarını, spool açarını, qeydiyyat açarını, keşi, logları silir və yoxlayır; **saxlanmış Wi-Fi şəbəkəsi**
   varsa dayanır (parolu hər klona gedərdi) — şəbəkəni silib yenidən işə salın. `sysprep` hər klona öz Windows kimliyini verir.
3. Kompüter sönəndən sonra **yandırmadan** disk surətini çıxarın (məs. Clonezilla) və digər kompüterlərə yazın.
4. Paneldə **bir** qeydiyyat açarı yaradın: **Yeni kiosk(lar) üçün**, **neçə kiosk** = klonların sayı.
5. Hər klonu yandırın, Windows-un ilk ayarlarını keçin, administrator ilə daxil olun və:

   ```powershell
   D:\provisioning\enroll.ps1          # açarı soruşur (yazılan görünmür)
   D:\provisioning\service-pin.ps1     # bu klonun öz servis PIN-i (T-334; image-də yoxdur)
   ```

   sonra kompüteri restart edin. Klon öz cihaz açarını yaradır, açarla **ayrıca kiosk** kimi aktivləşir (hər birinin
   öz `KSK-…` kodu). Panelde açarın istifadəsi `1 / 3`, `2 / 3`… artır.

Imicdə **qalmır**: cihaz açarı, spool açarı, qeydiyyat açarı, parol (kiosk hesabının parolu hər quraşdırmada təsadüfi
yaranır və yalnız Windows-un gizli yaddaşındadır), loglar, Wi-Fi parolu. Windows tərəfi real cihazda M19b-də yoxlanacaq (T-331).

## 10. Texniki giriş (admin PIN və servis PIN-i)

Müştəri üçün görünən çıxış yoxdur. Başlıqdakı **NeoPrint loqosuna 3 saniyə ərzində 5 dəfə** toxunun → PIN
klaviaturası → iki PIN-dən biri:

- **Admin PIN** — paneldə kiosk üçün təyin olunur, aktivləşdirmədən sonra kioska gəlir və internetsiz də işləyir;
- **Servis PIN-i** (T-334) — **bu cihazın öz PIN-i**, quraşdırma vaxtı təyin olunur (Linux: `install.sh` sonda soruşur
  və ya `sudo neoprint-service-pin`; Windows: `service-pin.ps1`). **Həmişə işləyir** — kiosk aktivləşməyibsə, serverə
  qoşula bilmirsə də. Cihazda yalnız hash-i saxlanır; hər cihazın öz PIN-i var (golden image-ə düşmür). **Parol
  menecerinə yazın** — unudulsa `sudo neoprint-service-pin` ilə yenisi təyin olunur (bunun üçün masaüstünə giriş lazımdır).

Menyu:

- **Təhlükəsiz rejimdən çıx və yenidən başlat** — yalnız kiosk "Texniki xidmət" ekranındadırsa (10 dəqiqədə 3 çöküş);
- **Tətbiqi yenidən başlat**;
- **Texniki giriş: kiosk rejimindən çıx** — kiosk bağlanır və giriş ekranı çıxır, **öz hesabınızla** daxil olun:
  - Windows — Windows-un giriş ekranı (toxunma klaviaturası ilə də);
  - Linux, **Desktop** nüsxəsi (pilot cihazı) — **Ubuntu-nun qrafik giriş ekranı**: barmaqla və ekran klaviaturası ilə
    daxil olub Wi-Fi-ı, ayarları dəyişə bilərsiniz;
  - Linux, Server nüsxəsi — `tty1`-də konsol girişi (klaviatura lazımdır).

  **Kiosku qaytarmaq: kompüteri yenidən başladın** (Ubuntu-da sağ yuxarı künc → ⏻ → *Restart*).

5 səhv PIN menyunu 5 dəqiqəlik bağlayır — hansı PIN yoxlanırsa yoxlansın.

**Nə admin PIN-i, nə servis PIN-i yoxdursa** (aktivləşməyib və quraşdırmada servis PIN-i təyin olunmayıb), ekrandan çıxış
yoxdur:

- Windows: kompüteri restart edin, açılışda **Shift** düyməsini basılı saxlayın — avtomatik giriş keçilir, administrator
  hesabı ilə daxil olun, skripti düzgün parametrlə yenidən işə salın;
- Linux: açılışda **Esc** ilə GRUB menyusu → *Advanced options* → *recovery mode* → root shell (klaviatura lazımdır).

Kompüteri söndürmək həmişə mümkündür: güc düyməsini **5 saniyə** basılı saxlayın (qısa basma qəsdən işləmir). Yandıranda
kiosk yenə özü açılır.

## 11. Linux (Ubuntu) — pilot cihazı (T-332, T-333)

Bu bölmə Linux kioskunu qurmaq üçündür, əvvəldən sona ardıcıl. Skript **Ubuntu 24.04 və 26.04**-də, **Server və
Desktop** nüsxəsində işləyir. Desktop-da qrafik giriş söndürülür (masaüstü silinmir) və kompüter kiosk kimi açılır.

### 11.0. Ən qısa yol: cihazdakı Ubuntu Desktop, yalnız toxunma ilə (pilot cihazı, T-333)

Ubuntu yenidən quraşdırılmır, **klaviatura lazım deyil** — hər şey barmaqla və ekran klaviaturası ilə. Təxminən **25 dəqiqə**.

**A. Kompüterdə (klaviatura ilə):** NeoPrint komandasının hazırladığı qovluğun **içindəkiləri** (`install.sh`,
`package`, `provisioning`) boş flaşın kökünə köçürün (test serveri üçün: `tools/dev-kiosk/make-stick.sh`,
`docs/DEV_ENVIRONMENT.md` §7y). Flaşda gizli məlumat yoxdur.

**B. Kiosk cihazında (barmaqla):**

1. **Ekran klaviaturasını yandırın:** ekranın sağ yuxarı küncünə toxunun → ⚙ (**Settings**) → **Accessibility** →
   **Typing** → **Screen Keyboard** — yandırın. Bundan sonra yazı sahəsinə toxunanda ekranın aşağısında klaviatura çıxır
   (`/`, `*` kimi işarələr — klaviaturadakı **?123** düyməsi ilə).
2. **Wi-Fi hamı üçün olsun** (masaüstü söndürüləndən sonra heç kim daxil olmur — Wi-Fi özü qoşulmalıdır): **Settings →
   Wi-Fi** → qoşulduğunuz şəbəkənin yanındakı ⚙ → **Details** → **"Make available to other users"** işarələyin →
   **Apply** (Wi-Fi parolunu soruşsa — yazın). Unutsanız, skript heç nəyi dəyişmədən dayanır və bunu deyir
   (`the Wi-Fi network "..." is this user's alone`). Kabel varsa — bu addım lazım deyil.
3. **Flaşı taxın** (Ubuntu onu açıb pəncərə göstərsə — bağlayın).
4. **Terminal:** ekranın sol aşağısındakı **⋮⋮⋮** (bütün proqramlar) → **Terminal**. Qara pəncərəyə toxunun (klaviatura
   çıxır) və yazın, sonra **Enter** (klaviaturada ↵):

   ```bash
   sudo bash /media/*/*/install.sh
   ```

5. **Parol:** öz Ubuntu parolunuzu yazın (yazılanlar görünmür — normaldır) → ↵.
6. **Qeydiyyat açarı** soruşulanda — sadəcə ↵ (açarsız). Skript `step 1/13` … `step 13/13` yazır (10–15 dəqiqə,
   ekrana toxunmayın). Sonda **`provisioning finished: 13 of 13 steps`** və **`Done`** — **şəklini çəkin**.
6a. **Servis PIN-i** (T-334) soruşulanda — 6–12 rəqəm yazın → ↵, **eynisini təkrar** → ↵ (yazılanlar görünmür). Bu,
   bu kioskun menyusuna **hər halda** girmək üçündür (§10) — **yadda saxlayın / parol menecerinə yazın**.
7. Flaşı çıxarın, yazın `sudo reboot` → ↵ (parol soruşsa — yazın). Kompüter masaüstü əvəzinə **yalnız NeoPrint**-lə
   açılır və ekranda **aktivləşdirmə kodu** göstərir — **şəklini çəkin**.

**C. Kompüterdə, admin paneldə:** **Cihazlar** → ekrandakı kodu daxil edin → **yeni kiosk** (ad, ünvan, admin PIN) — §7.
Kiosk bir neçə saniyəyə aktivləşir. Sonra §8-dəki yoxlamalar.

(Klaviatura varsa, B-6-da qeydiyyat açarını yapışdırmaq olar — §4: kiosk kodsuz, özü aktivləşir.)

**Xəta olsa** — `ERROR: step N/13` və `what to do` sətirlərinin şəklini göndərin (cədvəl §11.5-də).

**Masaüstünü qaytarmaq** (sınaq uğursuz olsa) — bunun üçün bir anlıq **USB klaviatura** lazımdır (konsolda parol və
əmr yazılır): açılışda **Esc** → *Advanced options* → *recovery mode* → *root*, sonra:

```bash
systemctl disable neoprint-kiosk && systemctl enable gdm && systemctl set-default graphical.target && reboot
```

Aşağıdakı §11.1–§11.7 — Ubuntu Server-in sıfırdan quraşdırılması (Desktop-da problem olsa).

**Real Linux cihazında hələ yoxlanmayıb** — skript Ubuntu 24.04 və 26.04 (+ "Desktop kimi") konteynerində tam sınanıb (`tools/provision-test`),
cihazda ilk quraşdırma M19b-də NeoPrint komandası ilə birgə edilir (T-331). Gözlənilməz bir şey görsəniz — ekranın
şəklini çəkib göndərin.

### 11.1. Cihaz gələndə — nə quraşdırılıb? (5 dəqiqə)

Cihazı yandırın, klaviatura qoşun, daxil olun (satıcının verdiyi istifadəçi və parol). Qrafik masaüstü (Desktop)
açılıbsa — **Terminal** proqramını açın (`Ctrl+Alt+T`). Bu əmrləri bir-bir yazın, cavabların şəklini çəkin:

```bash
lsb_release -ds                      # Ubuntu versiyası
systemctl get-default                # graphical.target = Desktop, multi-user.target = Server
lscpu | grep "Model name"            # prosessor
free -h | grep Mem                   # yaddaş
df -h /                              # diskdə boş yer
ip -br link                          # şəbəkə kartları (en... = kabel)
grep -i "name=" /proc/bus/input/devices   # giriş qurğuları (toxunma paneli "Touch" sözü ilə)
```

| Nəyə baxırıq | Nə olmalıdır | Olmasa |
|---|---|---|
| Versiya | `Ubuntu 24.04.x LTS` | Başqa versiya — §11.2 ilə 24.04 Server quraşdırılır |
| Növ | `multi-user.target` (Server) | `graphical.target` (Desktop) — §11.2 ilə **Server** quraşdırılır: Desktop-un öz qrafik girişi və bildirişləri kiosk ekranına çıxa bilər |
| Yaddaş / disk | ≥ 8 GB / ≥ 40 GB boş | NeoPrint komandasına deyin |
| Şəbəkə | `en...` adlı kart var | Kabel kartı yoxdursa — NeoPrint komandasına deyin |
| Toxunma | Siyahıda "Touch" / "Touchscreen" sözü olan qurğu | Yoxdursa — satıcıdan soruşun (toxunma paneli Linux-da HID kimi işləyirmi?) |

Cihazda artıq **Ubuntu 24.04 Server** varsa §11.2-ni keçin.

### 11.2. Ubuntu 24.04 Server-in quraşdırılması (30 dəqiqə)

Cihazdakı hər şey silinir — satıcının bərpa (recovery) image-i lazımdırsa əvvəl soruşun.

1. Başqa kompüterdə **ubuntu.com/download/server** → **Ubuntu Server 24.04 LTS** ISO-nu endirin və onu boş flaşa
   **balenaEtcher** və ya **Rufus** ilə yazın (bu, quraşdırma flaşı deyil — ayrıca flaş).
2. Cihaza flaşı taxın, yandırın, açılış menyusundan (adətən **F7**, **F11** və ya **F12**; satıcıdan soruşun) flaşı
   seçin.
3. Quraşdırıcıda: dil **English**, klaviatura **English (US)**, növ **Ubuntu Server (minimized)**, şəbəkə — **kabel**
   (avtomatik), disk — **Use an entire disk**, adınız və **öz admin istifadəçiniz** (məs. `neoprint-admin`; parolu
   parol menecerinə yazın), **Install OpenSSH server** — işarələməyin, əlavə paketlər — heç biri.
4. Bitəndə flaşı çıxarın, **Reboot**. Qara ekranda istifadəçi və parolunuzla daxil olun.
5. Yeniləmələr: `sudo apt update && sudo apt full-upgrade -y`, sonra `sudo reboot`.

**Wi-Fi saxlamayın** — kabel istifadə edin (golden image üçün vacibdir, §11.7).

### 11.3. Quraşdırma flaşı (NeoPrint komandası)

§2 kimi, amma **linux-x64** artefaktları ilə (`kiosk-release-<versiya>-linux-x64-production`); hər paket
`release-sign verify ... --key-set production` ilə yoxlanır. Flaşda:

```
NEOPRINT (flaş)
├─ provisioning/   provision.sh, prepare-image.sh, neoprint-firstboot, neoprint-enroll, ...
├─ package/        app/, agent/, VERSION   (imzalı, yoxlanmış)
└─ verifier/       yalnız --download üçün: production açarlı Agent
```

`deps` lazım deyil — LibreOffice və Ghostscript Ubuntu-nun öz paketlərindən quraşdırılır. Flaşda gizli məlumat yoxdur.

### 11.4. Qeydiyyat açarı

§4 kimi: admin panel → **Cihazlar** → **"Qeydiyyat açarı yarat"** → **Yeni kiosk üçün**, 24 saat → açarı kopyalayın.

### 11.5. Quraşdırma skripti (15–20 dəqiqə)

1. Quraşdırma flaşını taxın və bu əmrləri yazın (flaş adətən `/dev/sdb1`-dir; `lsblk` göstərir):

   ```bash
   sudo mount /dev/sdb1 /mnt
   sudo bash /mnt/provisioning/provision.sh --api-url https://api.neoprint.az \
       --offline-package /mnt/package --channel pilot --enrollment-token <AÇAR>
   ```

   `<AÇAR>` — §11.4-dəki açar. Flaşda paket yoxdursa `--offline-package /mnt/package` əvəzinə `--download` yazın
   (skript ən son imzalı buraxılışı endirir və imzanı flaşdakı `verifier/` ilə özü yoxlayır).
2. Skript `step 1/13` … `step 13/13` yazır (paketlər endirildiyi üçün 10–15 dəqiqə). Uğurlu son:
   **`provisioning finished: 13 of 13 steps`**.
3. `sudo umount /mnt`, flaşı çıxarın, `sudo reboot`.
4. Cihaz açılanda ekranda **yalnız NeoPrint** görünür (`cage`). Kiosk açarla özü aktivləşir — §7 kimi yoxlayın.

**Xəta olsa:** skript `ERROR: step N/13 (...)` və `what to do: ...` yazır, bu qaçışda dəyişdirdiyi sistem fayllarını
geri qaytarır (`put back as they were`). Səbəbi aradan qaldırıb **eyni əmri** yenidən işə salın.

| Addım | Nədir | Səbəb (adətən) | Nə etməli |
|---|---|---|---|
| 1 | Paketlər | İnternet yoxdur, tarix və saat səhvdir | Kabeli yoxlayın; `sudo apt-get update` nə deyir — ekranın şəklini göndərin |
| 2–3 | Hesablar, qovluqlar | Disk dolub | Yer açın |
| 4 | Qeydiyyat açarı | Açar səhv yazılıb | Açar 64 simvoldur (`0-9`, `a-f`); sonra `sudo neoprint-enroll` ilə də vermək olar |
| 5–11 | Konsol, yeniləmələr, flaş, çevirici, sudo, firewall, vaxt | Sətirdə yazılıb | Ekranın şəklini göndərin |
| 12 | NeoPrint | Paket natamamdır, endirilmədi və ya **imza yoxlamasından keçmədi** | İnterneti yoxlayın; flaşı yenidən hazırlayın |
| 13 | Xidmətlər | Sətirdə yazılıb | `sudo reboot`, eyni əmri yenidən |

Tam jurnal: `/var/log/neoprint/provision.log` (içində parol və açar yoxdur).

### 11.6. Son yoxlama

§8-dəki siyahı; Windows-a aid sətirlər əvəzinə:

- [ ] **Ctrl+Alt+Del**, **Ctrl+Alt+F2…F6**, **Alt+Tab** — heç nə açılmır, kompüter yenidən başlamır.
- [ ] Müştəri flaşını ön yuvaya taxın: kiosk faylları göstərir; flaşı çıxarın — kiosk bunu görür.
- [ ] Söndürüb yandırın: açılış mətnsizdir, ekranda yenə yalnız NeoPrint.

### 11.7. Golden image (bir neçə eyni kiosk)

§9-un Linux variantı. Bir cihazı §11.2–§11.5 kimi qurun, amma **`--enrollment-token` yazmadan**, sonra:

```bash
sudo bash /mnt/provisioning/prepare-image.sh --check   # nəyin qaldığını deyir, heç nə dəyişmir
sudo bash /mnt/provisioning/prepare-image.sh           # təmizləyir və yoxlayır
sudo poweroff
```

Cihaz açarı, spool açarı, qeydiyyat açarı, keş, loglar, jurnal, `machine-id`, SSH host açarları, shell tarixçəsi
silinir; saxlanmış Wi-Fi varsa skript dayanır. Söndükdən sonra **yandırmadan** disk surətini çıxarın (Clonezilla) və
digər cihazlara yazın. Paneldə **bir** açar: **Yeni kiosk(lar) üçün**, **neçə kiosk** = klonların sayı. Hər klonda:

```bash
sudo neoprint-enroll        # açarı soruşur (yazılan görünmür)
sudo neoprint-service-pin   # bu klonun öz servis PIN-i (T-334; image-də yoxdur)
sudo reboot
```

Klon ilk açılışda öz `machine-id`-sini və cihaz açarını yaradır (`neoprint-firstboot`), ayrıca kiosk kimi aktivləşir.
