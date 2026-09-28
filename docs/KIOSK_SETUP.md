# Kioskun quraşdırılması (texnik üçün)

Bu sənəd yeni kiosk maşınını sıfırdan işə salmaq üçündür: OS quraşdırılması,
provisioning skripti, aktivləşdirmə, printer və yoxlama. Skriptin OS-də
etdiyi hər dəyişiklik ayrıca `docs/KIOSK_PROVISIONING.md`-dədir.

Kiosk **universal build**-dir: heç bir maşın üçün ayrıca build, fayl və ya
açar yoxdur. Kioskun adı, printeri, qiymətləri, admin PIN-i — hamısı
aktivləşdirmədən sonra backend-dən gəlir (plan 7.11).

---

## 1. Nə lazımdır

| | Windows | Linux |
|---|---|---|
| OS | Windows 11 **IoT Enterprise LTSC** və ya **Enterprise** (Pro dəstəklənmir — Shell Launcher və Keyboard Filter yoxdur) | Ubuntu 24.04 LTS **Server** (masaüstü mühiti olmadan) |
| Ekran | 21", 1920×1080, toxunma | eyni |
| Şəbəkə | backend-ə HTTPS (443), DNS, NTP; printer yerli şəbəkədə | eyni |
| NeoPrint paketi | `NeoPrint.Kiosk.exe` + `NeoPrint.Kiosk.Agent.exe` bir qovluqda (aşağıda) | `NeoPrint.Kiosk` + `NeoPrint.Kiosk.Agent` bir qovluqda |
| Konvertorlar | `LibreOffice_*_x64.msi`, `gs*w64.exe` bir qovluqda (`-DependenciesPath`) | skript `apt` ilə özü quraşdırır |

Konvertorları rəsmi saytlardan endirin: LibreOffice — libreoffice.org
(x64 MSI), Ghostscript — ghostscript.com (`gs*w64.exe`, AGPL — ayrıca
proses kimi, dəyişdirilmədən işlədilir, plan 2.0).

## 2. NeoPrint paketinin hazırlanması

İmzalı paketin backend-dən endirilməsi yeniləmə sistemi ilə gəlir (Addım 7).
Hələlik paket əl ilə, repo-dan publish edilir. UI və Agent **eyni qovluğa**
publish olunur:

```powershell
# Windows paketi (repo kökündə)
dotnet publish desktop/src/NeoPrint.Kiosk -c Release -r win-x64 --self-contained -o out/neoprint-win
dotnet publish desktop/src/NeoPrint.Kiosk.Agent -c Release -r win-x64 --self-contained -o out/neoprint-win
Set-Content out/neoprint-win/VERSION "0.3.0"
# PDF səhifə şəkilləri üçün PDFium (backend image-i ilə eyni buraxılış, chromium/8066):
#   github.com/bblanchon/pdfium-binaries → pdfium-win-x64.tgz → bin/pdfium.dll → out/neoprint-win/
```

```bash
# Linux paketi
dotnet publish desktop/src/NeoPrint.Kiosk -c Release -r linux-x64 --self-contained -o out/neoprint-linux
dotnet publish desktop/src/NeoPrint.Kiosk.Agent -c Release -r linux-x64 --self-contained -o out/neoprint-linux
echo "0.3.0" > out/neoprint-linux/VERSION
# PDFium: pdfium-linux-x64.tgz (chromium/8066) → lib/libpdfium.so → out/neoprint-linux/
tar -czf neoprint-linux-0.3.0.tar.gz -C out/neoprint-linux .
```

PDFium olmadan kiosk işləyir, amma baxış ekranında səhifə şəkilləri
əvəzinə yalnız səhifə nömrələri görünür.

Qovluğu (və ya `.zip` / `.tar.gz`-ni) flaşla kiosk maşınına köçürün.

## 3. Windows

1. Windows 11 IoT Enterprise LTSC quraşdırın, **administrator** hesabı ilə
   daxil olun (bu hesab sonra texniki giriş üçündür — parolunu saxlayın).
2. PowerShell-i **administrator kimi** açın və skripti işə salın:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\provision.ps1 -ApiUrl https://api.neoprint.az `
       -OfflinePackage D:\neoprint-win `
       -DependenciesPath D:\deps `
       -Channel pilot
   ```

   İstəyə görə `-EnrollmentToken <token>` — tam uzaqdan aktivləşdirmə üçün
   (admin panel → Cihazlar → Enrollment token).
3. İlk qaçışda skript Shell Launcher və Keyboard Filter komponentlərini
   yandırır və **"RESTART and run this script again"** yazır: maşını restart
   edin, administrator ilə daxil olun, **eyni əmri yenidən** işə salın. Skript
   idempotentdir — ikinci qaçış yalnız qalanını edir.
4. Sonda restart. Maşın `kiosk` hesabı ilə avtomatik daxil olur və ekranda
   yalnız NeoPrint görünür.

## 4. Linux

1. Ubuntu 24.04 Server quraşdırın (minimal), öz admin hesabınızı yaradın.
2. Repo-dakı `desktop/provisioning/linux/` qovluğunu və paketi maşına
   köçürün:

   ```bash
   sudo ./provision.sh --api-url https://api.neoprint.az \
       --offline-package ./neoprint-linux-0.3.0.tar.gz \
       --channel pilot
   ```

   İstəyə görə `--enrollment-token <token>`.
3. `sudo reboot`. `tty1`-də `kiosk` hesabının sessiyası açılır və `cage`
   NeoPrint-i bütün ekrana verir.

## 5. Aktivləşdirmə

Ekranda aktivləşdirmə kodu və QR görünür. Admin panel → **Cihazlar** →
kodu daxil edin (və ya operator QR-ı skan edir) → yeni kiosk yaradın və ya
mövcud kiosku seçin, **admin PIN** təyin edin, printeri seçin. Enrollment
token ilə quraşdırılıbsa bu addım avtomatikdir.

## 6. Printer və yoxlama

1. Admin panel → kiosk → **Printerlər**: kioskun gördüyü printerlər; birini
   seçin → **Test çap**. Uğurlu test səhifəsindən sonra kiosk `Online` olur.
2. Admin panel → kiosk → **Sərf** (və ya `docs/DEV_ENVIRONMENT.md`-dəki API
   əmri): lotoka kağız qoyun və sayı daxil edin — boş lotokla sifariş qəbul
   olunmur.
3. Flaşdan bir PDF və bir DOCX çap edin.

## 7. Texniki giriş (admin PIN)

Müştəri üçün görünən çıxış yoxdur. Başlıqdakı **NeoPrint loqosuna 3 saniyə
ərzində 5 dəfə** toxunun → PIN klaviaturası → kioskun admin PIN-i. Menyu:

- **Təhlükəsiz rejimdən çıx və yenidən başlat** — yalnız kiosk "Texniki
  xidmət" ekranındadırsa (10 dəqiqədə 3 çöküş);
- **Tətbiqi yenidən başlat**;
- **Texniki giriş: kiosk rejimindən çıx** — Windows-da `kiosk` hesabından
  çıxılır, logon ekranında **öz administrator hesabınızla** daxil olun
  (Explorer açılır); Linux-da kiosk sessiyası dayanır, `tty1`-də konsol
  login çıxır — öz hesabınızla daxil olun. Kiosku qaytarmaq: Windows-da
  restart; Linux-da `sudo systemctl start neoprint-kiosk` və ya restart.

5 səhv PIN menyunu 5 dəqiqəlik bağlayır.

**Aktivləşdirilməmiş kioskda PIN yoxdur**, ekrandan çıxış da yoxdur. Belə
halda (məsələn, `-ApiUrl` səhv yazılıb):

- Windows: maşını restart edin, açılışda **Shift** düyməsini basılı saxlayın
  — avtomatik giriş keçilir, administrator hesabı ilə daxil olun, skripti
  düzgün parametrlə yenidən işə salın;
- Linux: açılışda **Esc** ilə GRUB menyusunu açın → *Advanced options* →
  *recovery mode* → root shell, `provision.sh`-i yenidən işə salın.

## 8. Golden image (çox kiosk üçün)

Bir maşını yuxarıdakı kimi hazırlayın (aktivləşdirmədən!), sonra:

- Windows: `.\prepare-image.ps1 -RunSysprep` — cihaz açarı, spool açarı,
  loglar silinir, `sysprep /generalize`; maşın söndükdən sonra image çıxarın.
- Linux: `sudo ./prepare-image.sh` — cihaz açarı, spool açarı, `machine-id`,
  SSH açarları silinir; söndürüb image çıxarın.

Hər klon ilk açılışda öz açarını yaradır və ayrıca cihaz kimi qeydiyyatdan
keçir.
