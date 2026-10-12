# Provisioning skriptlərinin etdiyi dəyişikliklər

`desktop/provisioning/windows/provision.ps1` və
`desktop/provisioning/linux/provision.sh` kiosk maşınında nəyi dəyişir — hər
bəndi və səbəbi (plan 7.12). Skriptlər **idempotentdir**: təkrar işə salınanda
yalnız fərqləri düzəldir, ona görə uzaqdan `ReapplyProvisioning` (Addım 7)
təhlükəsizdir. Quraşdırma addımları: `docs/KIOSK_SETUP.md`.

Hər iki skript: parametrlər `ApiUrl`, `EnrollmentToken` (istəyə görə, tək
istifadəlik), `Channel` (`pilot`/`stable`), `OfflinePackage`; log —
`C:\ProgramData\NeoPrint\provision.log` / `/var/log/neoprint/provision.log`;
sonda `state/provisioning-version`. İmzalı paketin backend-dən endirilməsi —
yeniləmə sistemi ilə (Addım 7).

## Windows (`provision.ps1`, administrator kimi)

| Sahə | Dəyişiklik | Niyə |
|---|---|---|
| Edition | IoT Enterprise / Enterprise deyilsə dayanır | Shell Launcher və Keyboard Filter yalnız bunlarda var |
| Komponentlər | `Client-EmbeddedShellLauncher`, `Client-KeyboardFilter` yandırılır; ilk dəfə restart tələb olunur, sonra skript təkrar işə salınır | Kiosk rejiminin OS qatı |
| Hesab | Lokal `kiosk` (yalnız `Users`, heç vaxt `Administrators`), təsadüfi parol hər qaçışda yenilənir | UI-ın minimal hüquqlu hesabı |
| Avtomatik giriş | `Winlogon`: `AutoAdminLogon=1`, `DefaultUserName=kiosk`; parol **LSA secret** (`DefaultPassword`), registry-də açıq mətn yoxdur | Plan 7.12 |
| Shell Launcher v2 | `kiosk` hesabının shell-i = `C:\Program Files\NeoPrint\current\NeoPrint.Kiosk.exe` (`AllAppsFullScreen`, çıxanda `RestartShell`); digər hesablar — Explorer. MDM bridge (`MDM_AssignedAccess`) yalnız SYSTEM-i qəbul etdiyi üçün bir dəfəlik planlaşdırılmış tapşırıqla SYSTEM kimi tətbiq olunur | Ekranda NeoPrint-dən başqa heç nə; texniki giriş administrator hesabı ilə |
| Yenidən yükləmə hüququ | Dəyişdirilmir: `kiosk` hesabı Windows-un standart "Shut down the system" hüququ ilə qalır; tətbiq yalnız `shutdown /r /t 0` çağırır (`PowerPermissionTests`) | Uzaqdan `Reboot` əmri və gizli admin menyusu bununla işləyir. **Məhdudiyyət:** Windows-da "yalnız reboot" hüququ yoxdur — bu hüquq söndürməyə də icazə verir. Müştəri ona çata bilmir (Shell Launcher-də Start menyusu yoxdur, Keyboard Filter `Ctrl+Alt+Del`-i bağlayır). Hüququ `kiosk` hesabından tam almaq üçün reboot Agent-ə (SYSTEM) köçürülməlidir — Addım 7B (T-219) |
| Keyboard Filter | Bloklanır: Windows, Application, Alt+Tab, Alt+Esc, Alt+F4, Alt+Space, Ctrl+Esc, Ctrl+Alt+Del, Shift+Ctrl+Esc, Win+L/D/E/R/Tab; administratorlar üçün söndürülür | OS-ə çıxış yolları |
| Məlumat qovluğu | `C:\ProgramData\NeoPrint\{device,spool,state,logs}` — ACL yalnız `SYSTEM` (tam) + `kiosk` (dəyişmə) | Spool müştəri sənədləridir, `device` cihaz kimliyidir |
| Bildirişlər | Bildiriş mərkəzi, toast, consumer features, tips/Spotlight, Copilot, Widgets, OneDrive (silinir), Teams çat quraşdırılması, xəta hesabatı UI-sı, aşağı disk / yeni cihaz bildirişləri, Defender bildirişləri (qoruma açıq qalır) söndürülür | Müştərinin qarşısına heç nə çıxmamalıdır |
| AutoPlay | Söndürülür | Flaşı tətbiq özü idarə edir |
| Toxunma, ekran | Kənar sürüşdürmə jestləri, toxunma klaviaturasının avtomatik açılması, kilid ekranı, ekran qoruyucu söndürülür; enerji planı "yüksək performans", yuxu / hibernasiya yoxdur | Kiosk həmişə açıq görünməlidir |
| Windows Update | Yeniləmələr quraşdırılır; avtomatik restart və bildirişlər yoxdur (`NoAutoRebootWithLoggedOnUsers`, bildiriş səviyyəsi 2) | Restart Agent-in qərarıdır (yeniləmə pəncərəsi, boş növbə) |
| Printer | "Windows default printeri özü idarə etsin" söndürülür | Printer admin paneldən seçilir |
| Saat | `Azerbaijan Standard Time`, NTP sinxronizasiyası | |
| Konvertorlar | `-DependenciesPath`-dən LibreOffice (MSI, səssiz) və Ghostscript (səssiz), artıq varsa keçilir | Kioskda sənəd konvertasiyası |
| NeoPrint | Paket → `C:\Program Files\NeoPrint\versions\<VERSION>`, `current` junction; `NeoPrintAgent` servisi (LocalSystem, avtomatik, dayanarsa 5 san-də yenidən başlayır); `NEOPRINT_KIOSK_Kiosk__ApiBaseUrl` maşın mühit dəyişəni | Yeniləmə kimliyə və spool-a toxunmur |
| Firewall | Daxil olan hər şey bloklanır. Çıxan: yalnız DNS (53), NTP (123), HTTP (80), HTTPS (443) və yerli şəbəkədəki printerlər — IPP 631, raw 9100, LPD 515, SNMP 161, mDNS 5353. Konvertor proqramlarına (`soffice.exe`, `soffice.bin`, `gswin64c.exe`, `gswin64.exe`, `gpcl6win64.exe`, `gxpswin64.exe`) çıxış **tam bloklanır** (blok qaydası icazəni üstələyir) | Kiosk untrusted edge device-dır; konvertor untrusted fayl açır (T-075) |

## Linux (`provision.sh`, root kimi, Ubuntu 24.04 Server)

| Sahə | Dəyişiklik | Niyə |
|---|---|---|
| Paketlər | `cage`, `cups`, `cups-ipp-utils` (native formatlar üçün `ipptool`), `printer-driver-cups-pdf` (test printeri), `udisks2`, LibreOffice (GUI-siz: core/writer/calc/impress), `ghostscript`, fontlar (DejaVu, Liberation, Carlito, Caladea), `chrony`, `sudo`, `unattended-upgrades`, `ufw`; masaüstü mühiti və bildiriş servisi **yoxdur** | Ekranda başqa pəncərə yarana bilməz |
| CUPS | Açıq, printerlər paylaşılmır; iş tarixçəsi (default) saxlanır | Restart-dan sonra "çap olubmu?" (T-066) |
| Hesablar | `neoprint` (Agent, sistem hesabı, `lpadmin`), `kiosk` (UI; `video`, `input`, `render`, `neoprint-convert` — konvertorun nəticəsini oxumaq üçün), `neoprint-convert` (konvertor, sistem hesabı, evsiz, login yoxdur) | Minimal hüquq; konvertorun şəbəkəsi yoxdur |
| Qovluqlar | `/var/lib/neoprint` `kiosk:neoprint 751` (konvertor yalnız keçə bilir); `device` `kiosk 700`; `spool` `kiosk:neoprint-convert 710`; `spool/work` `kiosk:neoprint-convert 2770` (setgid); `state` `kiosk:neoprint 750`; `/var/log/neoprint` `kiosk:neoprint 770`; `/opt/neoprint/versions` | UI öz məlumatının sahibidir; Agent vəziyyəti oxuyur; konvertor yalnız iş qovluğuna keçə bilir (T-078, T-075) |
| Konsol | `logind`: `NAutoVTs=0`, `ReserveVT=0`, güc / yuxu düymələri ignore; `ctrl-alt-del.target` mask; SysRq söndürülür; kernel mesajları ekrana çıxmır | Klaviaturadan konsola, rebootа yol yoxdur |
| Boot | GRUB timeout 0, `quiet splash loglevel=3 consoleblank=0 vt.global_cursor_default=0` | Ekranda yalnız NeoPrint |
| Yeniləmələr | Yalnız təhlükəsizlik yeniləmələri, avtomatik reboot yox; `apport` söndürülür | Reboot Agent-in qərarıdır |
| Flaş | polkit: yalnız `kiosk` mount / unmount / eject edə bilir, başqa blok cihaz əməliyyatı yoxdur; udisks2 mount seçimləri `noexec,nosuid,nodev`. Mount-u kiosk tətbiqi özü edir (avtomounter yoxdur) | Flaşdan heç nə icra olunmur |
| Konvertor sandbox-u | `/usr/local/lib/neoprint/neoprint-convert` (root, 755): yalnız `soffice`/`gs`/`gpcl6`/`gxps`-i, yalnız `/var/lib/neoprint/spool/work/*`-də, `timeout -k 5` (≤600 san) + `prlimit` (2 GB yaddaş, 120 san CPU) altında işə salır | Limitləri çağıran dəyişə bilməz (T-075) |
| sudoers | `neoprint`: `systemctl reboot`, `restart neoprint-kiosk/agent`, `lpadmin`, `cupsenable`. `kiosk`: `systemctl start neoprint-maintenance.service`, `systemctl reboot`, və `neoprint-convert` kimi yalnız sandbox wrapper-i. **Güc əmrlərindən yalnız `reboot`:** `poweroff`, `halt`, `shutdown`, wildcard və `NOPASSWD: ALL` yoxdur — `PowerPermissionTests` bu siyahını saxlayır, genişlənsə test düşür (T-233) | Hər hesab yalnız öz işini görə bilir; uzaqdan `Reboot` əmri (plan 7.8) kioskun bu icazəsi ilə icra olunur |
| Firewall (`ufw`) | Daxil olan: hamısı bloklanır. Çıxan: DNS 53, NTP 123, HTTP 80, HTTPS 443, yerli şəbəkələrə (10/8, 172.16/12, 192.168/16) IPP 631, raw 9100, LPD 515, SNMP 161, mDNS 5353. `neoprint-convert` hesabının **bütün** trafiki (loopback daxil) `REJECT` — `/etc/ufw/before.rules` və `before6.rules`-da owner qaydası | T-075 |
| Saat | `Asia/Baku`, `chrony` | |
| NeoPrint | Paket → `/opt/neoprint/versions/<VERSION>` (root-a məxsus, yazılmaz), `current` symlink; systemd: `neoprint-agent`, `neoprint-kiosk` (`tty1`-də `kiosk` sessiyası, `cage -s`, `UMask=0007`, çıxarsa 3 san-də yenidən), `neoprint-maintenance` (admin menyusundan: kiosk dayanır, `tty1`-də login); drop-in `10-environment.conf`: `ConverterUser=neoprint-convert`, `ApiBaseUrl`; `getty@tty1` söndürülür | Yeniləmə kimliyə və spool-a toxunmur |

## Golden image

`prepare-image.ps1` / `prepare-image.sh` cihaz kimliyini silir: cihaz açarı
(Windows — CNG konteyneri `NeoPrint.Device`, Linux — `key.pem`), **spool
açarı** (Windows — `NeoPrint.Spool` + `spool.key.wrapped`, Linux —
`spool.key`), enrollment token, keş, spool (iş qovluğunun özü icazələri ilə
qalır), loglar; Linux-da `machine-id` və SSH açarları, Windows-da
`sysprep /generalize`. `provisioning-version` saxlanılır. Sonda nəticə
yoxlanılır — nəsə qalıbsa image çıxarılmamalıdır.
