# Kioskun quraşdırılması — addım-addım

Bu sənəd yeni NeoPrint kioskunu sıfırdan işə salmaq üçündür və **texniki bilik az olan** şəxs üçün yazılıb: hər addımda
nə etmək, ekranda nə görmək və səhv olsa nə etmək yazılıb. Skriptlərin Windows / Linux-da etdiyi hər dəyişiklik ayrıca
`docs/KIOSK_PROVISIONING.md`-dədir.

**Pilot cihazı Windows-dur** (Touch-pie 19", T-329) — əsas yol Windows-dur (§1–§9). Linux yolu §11-dədir.

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
   ```

   sonra kompüteri restart edin. Klon öz cihaz açarını yaradır, açarla **ayrıca kiosk** kimi aktivləşir (hər birinin
   öz `KSK-…` kodu). Panelde açarın istifadəsi `1 / 3`, `2 / 3`… artır.

Imicdə **qalmır**: cihaz açarı, spool açarı, qeydiyyat açarı, parol (kiosk hesabının parolu hər quraşdırmada təsadüfi
yaranır və yalnız Windows-un gizli yaddaşındadır), loglar, Wi-Fi parolu. Windows tərəfi real cihazda M19b-də yoxlanacaq (T-331).

## 10. Texniki giriş (admin PIN)

Müştəri üçün görünən çıxış yoxdur. Başlıqdakı **NeoPrint loqosuna 3 saniyə ərzində 5 dəfə** toxunun → PIN
klaviaturası → kioskun admin PIN-i. Menyu:

- **Təhlükəsiz rejimdən çıx və yenidən başlat** — yalnız kiosk "Texniki xidmət" ekranındadırsa (10 dəqiqədə 3 çöküş);
- **Tətbiqi yenidən başlat**;
- **Texniki giriş: kiosk rejimindən çıx** — Windows-da `kiosk` hesabından çıxılır, giriş ekranında **öz administrator
  hesabınızla** daxil olun; Linux-da kiosk sessiyası dayanır, `tty1`-də konsol girişi çıxır. Kiosku qaytarmaq:
  Windows-da restart; Linux-da `sudo systemctl start neoprint-kiosk` və ya restart.

5 səhv PIN menyunu 5 dəqiqəlik bağlayır.

**Aktivləşdirilməmiş kioskda PIN yoxdur**, ekrandan çıxış da yoxdur. Belə halda (məs. `-ApiUrl` səhv yazılıb):

- Windows: kompüteri restart edin, açılışda **Shift** düyməsini basılı saxlayın — avtomatik giriş keçilir, administrator
  hesabı ilə daxil olun, skripti düzgün parametrlə yenidən işə salın;
- Linux: açılışda **Esc** ilə GRUB menyusu → *Advanced options* → *recovery mode* → root shell, `provision.sh`-i yenidən.

## 11. Linux (Ubuntu 24.04 Server) — alternativ yol

Pilot üçün tələb deyil (cihaz Windows-dur); Linux skripti konteynerdə tam sınanır (`tools/provision-test`).

1. Ubuntu 24.04 LTS **Server** (minimal) quraşdırın, öz admin hesabınızı yaradın. Wi-Fi saxlamayın.
2. Flaşı qoşun (`sudo mount /dev/sdb1 /mnt`) və:

   ```bash
   sudo bash /mnt/provisioning/provision.sh --api-url https://api.neoprint.az \
       --offline-package /mnt/package --channel pilot --enrollment-token <AÇAR>
   ```

   və ya paketsiz: `--download` (imzanı flaşdakı `verifier/NeoPrint.Kiosk.Agent` yoxlayır).
3. Skript `step 1/13` … `step 13/13` yazır, sonda `provisioning finished: 13 of 13 steps`. Səhvdə: `ERROR: step N/13 (...)`,
   `what to do: ...` və dəyişdirdiyi sistem fayllarının qaytarıldığını deyir (`put back as they were`). Jurnal:
   `/var/log/neoprint/provision.log`. Addımlar: 1 paketlər, 2 hesablar, 3 qovluqlar, 4 qeydiyyat açarı, 5 konsolun
   bağlanması, 6 avtomatik yeniləmələr, 7 çıxarılan daşıyıcılar, 8 çevirici qutusu, 9 sudo qaydaları, 10 firewall,
   11 vaxt, 12 NeoPrint, 13 xidmətlər.
4. `sudo reboot` — `tty1`-də `cage` NeoPrint-i bütün ekrana verir.

**Golden image (Linux):** `sudo bash /mnt/provisioning/prepare-image.sh --check`, sonra `sudo bash
/mnt/provisioning/prepare-image.sh` — cihaz və spool açarları, qeydiyyat açarı, keş, loglar, jurnal, `machine-id`,
SSH host açarları, shell tarixçəsi silinir, yoxlanır (Wi-Fi parolu varsa dayanır); söndürüb surət çıxarın. Klon ilk
açılışda öz `machine-id`-ni və SSH açarlarını yaradır (`neoprint-firstboot`), kiosk açılanda öz cihaz açarını. Açar:
`sudo neoprint-enroll` (soruşur, görünmür) və ya `sudo neoprint-enroll --from-file /mnt/token.txt`.
