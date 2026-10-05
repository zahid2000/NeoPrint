# Əl ilə yoxlamalar

Avtomatik testlə və ya lokal stack-də təkrarlana bilməyən yoxlamalar: real
printer, real elektrik kəsilməsi, real cihaz. Hər yoxlamanın `TASKS.md`-də
tapşırığı var; nəticə həmin tapşırığın "Sübut" sütununa yazılır.

## MT-01. Çapın ortasında elektrik kəsilməsi (T-089)

**Nə vaxt:** Addım 7, pilotdan əvvəl. Ödəniş (Addım 4) və
`KioskHeartbeatMonitor` / `Suspected` (Addım 7) hazır olandan sonra.

**Niyə əl ilə:** fake printer işi dərhal "çap edir", ona görə kəsiləcək an yoxdur.
Kioskun bərpa məntiqi avtomatik testlərlə yoxlanılıb
(`PrintQueueWorkerTests`: jurnal, spooler, sayğac), amma real printerin,
real spooler-in və real elektrik kəsilməsinin birlikdə necə davrandığını
yalnız cihazda görmək olar.

**Gözlənilən qayda** (`FAILURE_RECOVERY.md`, plan Bölmə 2.5 və 7.12):

- iş **heç vaxt ikinci dəfə çapa göndərilmir**;
- işin bütün vərəqləri çıxıbsa → `Completed`, əks halda → `Interrupted`
  (səbəb `printer.restarted`, çıxan vərəq sayı ilə);
- hər cəhd `print_attempts`-də bir sətirdir, bərpadan sonra müəyyən edilən
  cəhddə `Recovered = true` olur;
- refund yalnız plan Bölmə 2.5-in qaydası ilə edilir, ikiqat refund olmur.

### Hazırlıq

1. Kiosk `KIOSK_SETUP.md` ilə quraşdırılıb, aktivdir, admin paneldə `Online`-dır.
2. BIOS-da "elektrik gələndə avtomatik açıl" (AC power recovery → On) aktivdir.
3. Real printer qoşulub və kiosk üçün seçilib (admin → Kiosklar → kiosk →
   Printerlər). Test səhifəsi çıxır.
4. Mümkünsə printerin SNMP səhifə sayğacı var. Admin → Printerlər cədvəlində
   sayğac görünür. Sayğacı olmayan printerlə yoxlamanı ayrıca bir dəfə də
   təkrarlayın.
5. Uzun çap işi hazırlayın: **30–50 səhifəlik PDF** flaşda. Çap bir neçə
   dəqiqə çəkməlidir ki, ortasında kəsmək mümkün olsun.
6. Kağız sayğacını qeyd edin: admin → kiosk → Sərf → A4 qalığı.
7. İki sifariş növü hazırlayın:
   - **anonim, kioskda bank kartı / QR ilə ödənilmiş** sifariş;
   - **qeydiyyatlı istifadəçinin** sifarişi (mobil tətbiqdən).
8. Kəsilən işin arxasında növbədə **ikinci iş** olsun (məsələn, 2 səhifəlik
   sifariş). Bərpadan sonra onun davam etdiyi yoxlanılır.
9. Admin panelini başqa kompüterdə açın: Dashboard və Sifarişlər səhifəsi.

### Nəyi kəsmək

Hər variant ayrıca sınaqdır. Hər dəfə yeni sifariş verin.

| Variant | Nə kəsilir | Nəyi yoxlayır |
|---|---|---|
| A | Yalnız **kiosk kompüterinin** elektriki (kabeli çıxarın, "Shut down" etməyin) | Printer işi çap etməyə davam edə bilər. Kiosk qalxanda spooler / sayğac işin sonunu görməlidir |
| B | Yalnız **printerin** elektriki | Kiosk işləyir. Spooler işi "uğursuz" deyir, sayğac qərar verir |
| C | **Hər ikisi** eyni anda (binada elektrik kəsilməsi) | Ən pis hal: nə spooler, nə yaddaş kömək edir, yalnız jurnal və sayğac qalır |
| D | Variant C, amma elektriki **20 dəqiqədən sonra** qaytarın | Server bu müddətdə kioskdan xəbər almır → job `Suspected` olur, sonra kiosk qayıdanda avtomatik həll olunur |

Kəsmə anı: printerdən **3–5 vərəq çıxandan sonra**, iş hələ davam edərkən.

### Addımlar

1. Uzun işi kioskda sifariş edin (ödəniş növü — yuxarıdakı iki növdən biri).
2. Admin paneldə sifarişin `Printing` olduğunu görün, kodunu yazın.
3. 3–5 vərəq çıxanda seçdiyiniz variant üzrə elektriki kəsin. Vaxtı yazın.
4. **Printerdən çıxan vərəqləri sayın** və yazın.
5. 1 dəqiqə gözləyin (variant D-də 20 dəqiqə), elektriki qaytarın.
6. Kiosk özü qalxana qədər toxunmayın. 5 dəqiqə ərzində kiosk ekranı ana
   ekrana qayıtmalıdır.
7. Printeri 5 dəqiqə müşahidə edin: **heç bir əlavə vərəq çıxmamalıdır**
   (növbədəki ikinci işin vərəqlərindən başqa).

### Nəyi yoxlamaq

| # | Yoxlama | Harada | Gözlənilən |
|---|---|---|---|
| 1 | Kiosk özü qalxdı | kiosk ekranı, admin → Kiosklar | Aktivləşdirmə kodu soruşulmur; status `Online` (təhlükəsiz rejim deyil — elektrik kəsilməsi çöküş sayılmır) |
| 2 | **İkiqat çap yoxdur** | printer, spooler | Kəsilən işdən əlavə vərəq çıxmayıb. Spooler növbəsi boşdur: Windows — `Get-PrintJob -PrinterName "<ad>"`, Linux — `lpstat -o` |
| 3 | Job statusu | admin → Sifarişlər → sifariş | Bütün vərəqlər çıxıbsa `Completed`. Əks halda `Interrupted`, səbəb "printer yenidən başladı" (`printer.restarted`), "Çap olunan vərəq" 4-cü addımdakı sayla eynidir (sayğac yoxdursa 0). Variant D-də əvvəl `Suspected`, kiosk qayıdandan sonra `Completed` / `Interrupted` |
| 4 | Status tarixçəsi | sifariş detalı → Tarixçə | `Queued → Printing → Completed` və ya `… → Printing → Interrupted`. İkinci `Printing` keçidi **yoxdur** |
| 5 | `print_attempts` | sifariş detalı → Çap cəhdləri | **Bir** sətir. "Restart-dan sonra müəyyən edilib" yazılıb. Printer adı, spooler istinadı, sayğac əvvəl → sonra (sayğac varsa fərq çıxan vərəqlərə uyğundur) |
| 6 | Növbədəki ikinci iş | admin → kiosk → Növbə, printer | Kəsilən iş həll olunandan sonra çap olunur, `Completed` |
| 7 | Kağız sayğacı | admin → kiosk → Sərf | Yalnız həqiqətən çıxan vərəqlər qədər azalıb, bir dəfə (hərəkətlər cədvəlində iş üçün bir sətir) |
| 8 | **Refund qərarı** | sifariş detalı, ödəniş / refund səhifəsi, kiosk ekranı | Aşağıdakı cədvələ bax |
| 9 | Kiosk jurnalı | Windows `C:\ProgramData\NeoPrint\logs`, Linux `/var/log/neoprint` | "was at the printer when the kiosk stopped … It is not printed again" sətri. Log-da fayl məzmunu, token, açar **yoxdur** |

**Refund qərarı** (plan Bölmə 2.5):

| Nəticə | Anonim, kioskda bankla ödənilib | Qeydiyyatlı istifadəçi |
|---|---|---|
| `Completed` | Refund yoxdur | Refund yoxdur |
| `Interrupted` | Avtomatik ləğv + karta refund; kiosk ekranında refund məlumatı | Avtomatik refund yoxdur; istifadəçiyə refund təklifi (bildiriş) |
| `Suspected` (variant D, kiosk hələ qayıtmayıb) | Avtomatik refund **yoxdur**; incident + admin → "Baxış gözləyən job-lar" | Eyni |
| `Suspected` → kiosk qayıtdı | Yuxarıdakı `Completed` / `Interrupted` sətri | Eyni |

Hər halda bir sifariş üçün **ən çox bir refund** olmalıdır: ödəniş səhifəsində
bir refund sətri, provayderdə bir qaytarma.

### Lokal stack-də yoxlamaq üçün sorğular (istəyə görə)

Pilot mühitində eyni sorğular pilot Mongo-suna qarşı işlədilir.

```js
// docker exec -it neoprint-mongo mongosh -u neoprint -p devMongoPassword --authenticationDatabase admin neoprint
const job = db.print_jobs.findOne({ JobCode: "<kod>" });
job.Status; job.PrintedSheets;
db.print_attempts.find({ JobId: job._id }).toArray();          // bir sətir, Recovered: true
db.state_transitions.find({ EntityId: job._id }).sort({ CreatedAt: 1 }).toArray();
db.supply_events.find({ KioskId: job.KioskId }).sort({ _id: -1 }).limit(3).toArray();
```

### Nəticəni yazmaq

Hər sınaq üçün bir sətir. Cədvəli T-089-un "Sübut" sütununa (və ya pilot
hesabatına) əlavə edin.

| Tarix | Kiosk | Printer (sayğac var/yox) | Variant | Ödəniş növü | Çıxan vərəq (sayılan) | Job statusu | `print_attempts` sətri | Əlavə vərəq çıxdı? | Refund | Nəticə |
|---|---|---|---|---|---|---|---|---|---|---|
|  |  |  |  |  |  |  |  |  |  |  |

Hər hansı yoxlama gözləniləndən fərqlidirsə: sifariş kodunu, vaxtı və kiosk
jurnalını saxlayın, `TASKS.md`-yə tapıntı kimi yazın.

## MT-02. Real printer (T-092)

**Nə vaxt:** Addım 7, pilotdan əvvəl. MT-01-dən əvvəl.

**Hazırlıq:** kiosk `KIOSK_SETUP.md` ilə quraşdırılıb, real printer USB və ya
şəbəkə ilə qoşulub, OS-də quraşdırılıb (Windows — sürücü; Linux — CUPS).

| # | Addım | Gözlənilən |
|---|---|---|
| 1 | Admin → kiosk → **Printerlər** | Real printer siyahıdadır. Fake printer yalnız `UseFakePrinter` açıqdırsa görünür. İmkanlar (rəng, ikitərəfli, A3) printerin real imkanlarına uyğundur |
| 2 | Printeri **Əsas et** → **Test çap** | Test səhifəsi çıxır, əmr "Uğurlu", kiosk `Online`, A4 sayğacı 1 azalır |
| 3 | Rəngli printerdə "Rəng"i söndürün (imkanı söndür) | Kioskun parametr ekranında rəngli seçim deaktivdir, yanında səbəb |
| 4 | Flaşdan 3 səhifəlik PDF, ikitərəfli (printer bacarırsa) | Kağız sayı, üzlər və admin → sifariş → "Çap cəhdləri" uyğundur |
| 5 | SNMP sayğacı olan printerdə sifariş | "Printer sayğacı" sütununda əvvəl → sonra, fərq çıxan vərəqlərə bərabərdir |
| 6 | Çap zamanı kağız qutusunu boşaldın / qapağı açın | Job `Interrupted`, kiosk `Paused`, ekranda "printer dayandı" izahı; admin dashboard-da kiosk "Diqqət tələb edir"-də |
| 7 | Printerin kabelini çıxarın, 1 dəq sonra taxın | Kiosk printerin itdiyini göstərir, qoşulanda özü `Online` olur, növbə davam edir |
| 8 | Real printer yoxdursa: Windows-da "Microsoft Print to PDF", Linux-da `cups-pdf` printerini seçin; flaşdan hər format qrupundan bir fayl (PDF, DOCX, XLSX, PPTX, JPG/PNG, TXT, PS) | Hər biri açılır, baxış və qiymət düzgündür, PDF faylı çıxır (plan Addım 3 DoD, "hər iki OS-də") |

## MT-03. Windows kiosk provisioning (T-093)

**Nə vaxt:** Addım 7, pilotdan əvvəl. **Harada:** Windows 11 IoT Enterprise
LTSC virtual maşını (Hyper-V / VirtualBox), snapshot ilə — səhv olarsa geri
qayıtmaq üçün.

Quraşdırma: `KIOSK_SETUP.md` bölmə 3 (`-EnrollmentToken` ilə). Sonra:

| # | Yoxlama | Gözlənilən |
|---|---|---|
| 1 | Restart-dan sonra | `kiosk` hesabı ilə avtomatik giriş, ekranda yalnız NeoPrint (Explorer, tapşırıq paneli yoxdur) |
| 2 | Aktivləşdirmə | Kod ekranı olmadan admin paneldə kiosk aktivdir (token) |
| 3 | Klaviatura: `Win`, `Alt+Tab`, `Alt+F4`, `Ctrl+Shift+Esc`, `Ctrl+Alt+Del` | Heç biri NeoPrint-dən çıxarmır (Keyboard Filter + hook) |
| 4 | Texniki girişdə: `Get-Service -DisplayName "NeoPrint agent"`; sonra `Stop-Process -Name NeoPrint.Kiosk` | Agent `Running`; öldürülən tətbiqi Agent özü yenidən açır |
| 5 | Konvertorun şəbəkəsi: texniki girişdə `Get-NetFirewallRule -Group "NeoPrint converters"` | LibreOffice / Ghostscript üçün "no network" qaydaları var; DOCX çapı yenə işləyir |
| 6 | Gizli admin çıxışı (loqoya 5 toxunuş → PIN → "Texniki giriş") | Logon ekranı, administrator ilə Explorer açılır; restart → yenidən kiosk |
| 7 | Skripti ikinci dəfə işə salın | Xətasız bitir, heç nəyi təkrarlamır (idempotent) |
| 8 | `C:\ProgramData\NeoPrint` ACL-i | Yalnız `SYSTEM` və `kiosk`; adi istifadəçi `device`-i oxuya bilmir |

## MT-04. Linux kiosk cihazı və real flaş (T-094)

**Nə vaxt:** Addım 7, pilotdan əvvəl. **Harada:** Ubuntu 24.04 Server olan
real kompüter və ya ekranlı VM (USB passthrough ilə).

Quraşdırma: `KIOSK_SETUP.md` bölmə 4. Sonra:

| # | Yoxlama | Gözlənilən |
|---|---|---|
| 1 | `sudo reboot` | `tty1`-də `cage` NeoPrint-i bütün ekrana açır, login soruşulmur |
| 2 | FAT32 / exFAT / NTFS flaş taxın | 5 saniyə ərzində fayl siyahısı açılır ("USB" nişanı) |
| 3 | Flaşı sifarişdən sonra, çapdan əvvəl çıxarın | Çap yenə olur (fayl şifrəli spool-dadır) |
| 4 | Flaşı fayl siyahısında çıxarın | Ana ekrana qayıdır, xəta pəncərəsi yoxdur |
| 5 | Gizli admin çıxışı → "Texniki giriş" | `tty1`-də konsol login; `sudo systemctl start neoprint-kiosk` kiosku qaytarır |
| 6 | `sudo -u neoprint-convert curl -m 5 https://example.com` | Bağlantı rədd olunur (konvertorun şəbəkəsi yoxdur) |
| 7 | `ls -l /var/lib/neoprint` | Sahiblər `KIOSK_PROVISIONING.md`-dəki kimi (`device` — `kiosk 700`) |
| 8 | Flaşdan foto → **Səhifədə yerləşdir** (T-180): şəkli iki barmaqla böyüdüb-kiçildin, bir barmaqla sürüşdürün | Şəkil barmaqla birlikdə hərəkət edir, kənar boşluğa və mərkəzə yapışır (nazik xətt görünür); konteyner kioskunda yalnız bir göstərici var, iki barmaq real toxunma ekranında yoxlanır |
| 9 | Eyni ekranda **Vəsiqə 85,6 × 54 mm** → **8 kart** → **Hazır** → real printerdə çap | Parametrlərdə A4, 100%, vərəqdə 1 səhifə kilidlidir; xətkeşlə hər kart 85,6 × 54 mm (±0,5 mm) |

## MT-05. Addım 4 — ödəniş və qaytarma, lokal stack (T-099)

**Nə vaxt:** hər ödəniş / refund dəyişikliyindən sonra və addım bağlananda.
**Harada:** lokal stack (`docker compose up -d`). Lazım olanlar:

- kiosk: `UseFakePrinter`, `SkipPayment: false`;
- admin paneli;
- mobil tətbiq: Chrome-da (`flutter run -d chrome`) və ya telefonda.

Bank və fiskal provayder mock-dur. Əmrlər `DEV_ENVIRONMENT.md` bölmə 7c-dədir. Son keçid: 2026-10-01, ekran görüntüləri `docs/screenshots/step-4/`.

**Hazırlıq:**

1. Admin → **Tənzimləmələr**-də `Payment.TimeoutMinutes = 3` və `Job.SuspectedAfterMinutes = 1` qoyun. Sonda 5 və 15-ə qaytarın.
2. Flaşa 3 səhifəlik PDF qoyun.
3. Fake printerin qovluğu `<spool>/printed`-dir:
   - Windows: `%ProgramData%\NeoPrint\spool\printed`;
   - Linux: `/var/lib/neoprint/spool/printed`.

| # | Addım | Gözlənilən |
|---|---|---|
| 1 | Kioskda sifariş → "Bank kartı və ya bank tətbiqi" | QR və geri sayım **3:00**-dan (paneldəki dəyər); son 60 saniyədə rəqəm sarı + ikon |
| 2 | `curl -X POST http://localhost:5080/api/dev/payments/<id>/simulate-success` | Kioskda "Ödəniş uğurlu", sifariş kodu, bir neçə saniyədə fiskal çekin QR-ı; fayl `printed`-də; admin → Ödənişlər-də `Ödənilib`, satış çeki `Verilib` |
| 3 | Qəbz ekranında "Email ünvanını yazın" → ünvan → **Göndər** | Klaviatura ayrıca paneldə, kəsilmədən; göndəriləndən sonra panel bağlanır, kartda "Göndərildi: a***@..."; məktub Mailpit-də (http://localhost:8025) |
| 4 | Parametrlər ekranında **Promokod** → səhv kod → **Tətbiq et** | Yazma paneli; səbəb sahənin sağında, klaviatura yerindən oynamır; **Bağla** yazılanı saxlayır |
| 5 | `printed` qovluğuna `PaperJam` yazılmış `printer-fail-next.txt` qoyun, sonra sifariş edib ödəyin (ssenari A) | Kiosk loqunda `Interrupted`; ödəniş `Qaytarılıb` (karta, `SystemError`), qaytarma fiskal çeki verilir; "Bitir"-dən sonra kioskun ana ekranında "Çap tamamlanmadı", məbləğ və status QR-ı |
| 6 | `printed` qovluğuna `PaperJam` yazılmış `printer-state.txt` qoyun, sifariş edib ödəyin | Sifariş **gözləyir** (refund yoxdur), kiosk heartbeat göndərməyə davam edir — loqda `429` yoxdur (T-121); faylı silin → çap olunur |
| 7 | Kioskun şəbəkəsini kəsin (kabel / `docker network disconnect`) ödənilmiş, çap olunmamış sifarişlə, 1+ dəqiqə gözləyin (ssenari B) | Sifariş admin → **Baxış gözləyən sifarişlər**-də; avtomatik refund **yoxdur**; araşdırma kartında çap cəhdləri və kiosk hadisələri |
| 8 | Həmin sifarişdə **Qərar ver** → "Tam qaytar", səbəb + qeyd | "Qərar qeydə alındı", siyahı boşalır; ödəniş `AdminManual` ilə karta qaytarılıb |
| 9 | Kioskda "NeoPrint tətbiqi" → mobil tətbiqdə QR-ı oxudun (Chrome-da `#/pay/<paymentId>`) → kartla ödəyin | Mobil "Ödəniş uğurlu"; kioskda qəbz ekranı, fayl çap olunur; mobil **Profil → Qəbzlər**-də yeni qəbz, fiskal çek "verildi" |
| 10 | Admin → həmin ödəniş → **Qaytar** → "Balansa" | Ödəniş `Qaytarılıb`, qaytarma "Balansa · Admin qərarı"; mobil **Balans**-da qaytarma sətri |
| 11 | Admin → anonim bank QR ödənişi → **Qaytar** | Yalnız "Karta" seçimi; ödəniş `Qaytarılıb`, qaytarma fiskal çeki verilir |
| 12 | Admin → **Qəbzlər** → PDF | PDF açılır: Azərbaycan hərfləri, ₼, fiskal QR (QuestPDF Linux image-də) |
| 13 | Eyni `simulate-success`-i `?duplicate=true` ilə göndərin | Bir ödəniş, bir çap, bir fiskal çek (idempotency) |
| 14 | `mock-bank/refunds-fail?fails=true` və `mock-fiscal/mode?mode=Down`, sonra №5 (ssenari A) | Refund `Refund.ProviderMaxAttempts` cəhddən sonra `Failed`, ödəniş "Qaytarma alınmadı", xəta mətni panelin dilində (T-127); satış çeki "Hazırlanır", xətası oxunaqlı |
| 15 | Ödəniş səhifəsində uğursuz refund → təyinat "Voucher kodu" → **Yenidən göndər** | Voucher `RF-XXXX-XXXX`, refund "Qaytarılıb" |
| 16 | `mock-fiscal/mode?mode=Ok`, `mock-bank/refunds-fail?fails=false` | Növbəti planlaşdırılmış cəhddə (backoff, ən çox 30 dəq) satış, sonra qaytarma çeki verilir; e-kassa rədd edən çek (`mode=Reject`) Fiskal çeklər-də **Təkrar cəhd** ilə təkrarlanır |
| 17 | `mock-fiscal/mode?mode=Reject`, mobil tətbiqdə balansı 1 ₼ artırın; sonra `mode=Ok` | Fiskal çeklər səhifəsinin başında "Açıq incident-lər (1)": "E-kassa 1 ₼ məbləğində balans artırma fiskal çekini rədd etdi…" (xam `TopUp` deyil). **Təkrar cəhd** → çek "Verilib", kart yox olur (T-129) |
| 18 | №5-dəki kimi `printer-fail-next.txt`, ödənişdən sonra qəbz ekranında gözləyin ("Bitir" basmayın) | Bir neçə saniyədən sonra başlıq "Çap tamamlanmadı", xəbərdarlıq ikonu, "0,30 ₼ kartınıza qaytarılır…" və QR. Kart tam görünür. "Bitir"-dən sonra ana ekranda arayış təkrar çıxmır (T-133) |
| 19 | Admin → Promokodlar: etibarlı (məs. 20%), vaxtı bitmiş və yalnız başqa kiosk üçün kod yaradın; hər birini kioskun promokod panelində və tətbiqlə ödəniş ekranında yazın | Vaxtı bitmiş — "Promokodun müddəti bitib."; başqa kiosk — "Bu promokod bu kioskda keçərli deyil."; etibarlı — endirim sətri və azalmış məbləğ. Ödənişdən sonra siyahıda istifadə sayı və verilən endirim artır, vaxtı bitmiş kod "Vaxtı bitib", məhdudiyyət kodun altında görünür (T-130, T-139) |
| 20 | Qaytarmalar → səbəb filtri, Ödənişlər → kanal filtri | Yalnız seçilmiş səbəb / kanalın sətirləri (T-137, T-138) |

## MT-06. Addım 4 — real provayderlər və telefon (T-099)

**Nə vaxt:** Addım 7, pilotdan əvvəl, real bank / e-kassa müqaviləsindən sonra.
**Harada:** staging + real kiosk + real Android / iOS telefon.

| # | Addım | Gözlənilən |
|---|---|---|
| 1 | Real bank tətbiqi ilə kioskdakı QR-ı ödəyin | Callback imzası yoxlanılır, kiosk 5 saniyə ərzində "Ödəniş uğurlu"; bankdakı məbləğ kioskdakı ilə eynidir |
| 2 | Callback-i bloklayın (staging firewall) və ödəyin | `Payment.ReconcileAfterMinutes`-dan sonra reconciliation ödənişi tapır, sifariş çap olunur |
| 3 | Real printerdə çapın ortasında kağızı çıxarın (MT-02 №6) | Ssenari A: avtomatik karta qaytarma, bankda refund görünür (1–3 iş günü), qaytarma fiskal çeki e-kassada |
| 4 | Telefonun öz kamerası ilə "NeoPrint tətbiqi" QR-ı | Tətbiq `neoprint://pay/...` linki ilə ödəniş ekranında açılır (Android və iOS) |
| 5 | Tətbiqdə **QR oxut** (ML Kit) | QR 2 saniyə ərzində tanınır, ödəniş ekranı açılır |
| 6 | Fiskal çekin QR-ını telefonla oxudun | e-kassa saytında çek açılır, məbləğ və tarix düzgündür |
| 7 | Balansdan və bonus səhifələrlə ödəniş | Fiskal çekdə bonus hissəsi 0 ₼, yalnız pulla ödənilən hissə; balans heç vaxt karta çıxarılmır |

## MT-07. Addım 5 — real telefon, kamera, FCM (T-149)

**Nə vaxt:** Addım 5-in sonunda (lokal stack + real telefon) və Addım 7-də staging-də təkrar.
**Harada:** real Android və iOS telefon, telefon kompüterlə eyni şəbəkədə
(`DEV_ENVIRONMENT.md` bölmə 6 — real telefon). Bu cədvəl Addım 5 irəlilədikcə doldurulur.

**Vəziyyət (`v0.5.0`, 2026-10-04): yoxlanmayıb** — layihə sahibi real cihazlarda sonra icra edəcək. Lokal Chrome-da yalnız real FCM web push tətbiq öndə ikən yoxlanıb (T-163, `docs/screenshots/step-5/push-01-fcm-foreground.png`).

| # | Addım | Gözlənilən |
|---|---|---|
| 1 | Kiosklar → **Yaxınımda** (ilk dəfə) | Telefon məkan icazəsi soruşur (izah mətni: "Yaxınlığınızdakı kioskları göstərmək üçün…"); icazədən sonra siyahı məsafə ilə, ən yaxın birinci |
| 2 | İcazəni rədd edin, sonra yenidən **Yaxınımda** | Warning sətri "Məkana icazə verilmədi…"; ikinci seçimdə icazə yenidən soruşulur. "Bir daha soruşma" / iOS-da rədd — "telefonun tənzimləmələrində … açın" mətni |
| 3 | Telefonda məkan xidmətini söndürüb **Yaxınımda** | "Telefonda məkan xidməti söndürülüb…" mətni, siyahı məkansız qalır |
| 4 | **Xəritə** — barmaqla sürüşdürmə, iki barmaqla böyütmə | Xəritə səlis hərəkət edir, marker toxunuşla seçilir, aşağıda kart açılır, "© OpenStreetMap contributors" həmişə görünür |
| 5 | Release build (`flutter build apk --release`) **`NEOPRINT_MAP_TILE_URL`-siz** | "Xəritə" seçimi görünmür, yalnız siyahı (ictimai OSM serveri release-də istifadə olunmur — T-061) |
| 6 | Telefonun kamerası ilə kiosk ekranının altındakı kiosk QR-ını oxudun (Ana səhifə → **QR oxut**) | Həmin kioskun detalı açılır, aşağıda **Sessiya aç** |
| 7 | **Sessiya aç** → kiosk ekranına baxın | Kioskun başlığında "Telefon qoşulub" nişanı (kimin olduğu yazılmır); telefonda tabların üstündə "KSK-… · ad" və qalan dəqiqə |
| 8 | **Fayl seç** → fayl → seçimlər → **Sifariş et** → ödəyin | Qiymət seçim dəyişəndə yenilənir; ödənişdən sonra kiosk sifarişi özü yükləyib çap edir, sifariş tarixçədə "Tamamlandı" |
| 9 | Sessiya açıq ikən tətbiqi arxa plana atın, 10 dəqiqədən çox gözləyin, sonra açın | Banner yoxdur; tətbiq açıq qalıbsa "Sessiya bitdi. Çap üçün kioskda yenidən açın." bildirişi; kiosk nişanı itir |
| 10 | Sessiya açıq ikən telefonu Wi-Fi-dan ayırıb 1 dəqiqə sonra qaytarın | Bağlantı bərpa olunanda banner serverin vəziyyətinə uyğunlaşır (sessiya hələ varsa qalır, bitibsə gedir) |
| 11 | Kioska real USB QR skaner (HID, klaviatura rejimi) qoşun; tətbiqdə **Mənim QR kodum** ekranını skanerə tutun — Windows və Linux kioskda | Ekran "Hazırladığınız fayllar" açılır; ekranın parlaqlığı aşağı olsa da oxunur. Skaner sətir sonunu CR və ya LF göndərirsə hər ikisində işləyir |
| 12 | QR ekranını açıq saxlayıb 2 dəqiqə gözləyin, sonra oxudun | Kod özü yenilənib — kiosk qəbul edir. Köhnə kodun ekran görüntüsünü oxutsanız: "QR kodun vaxtı keçib…" |
| 13 | Skanerlə başqa QR (bank QR-ı, kiosk QR-ı) oxudun — ana ekranda və ödəniş ekranında | Heç nə dəyişmir; ödəniş gedən ekran yerində qalır |
| 14 | Tətbiqdə kiosk seçib **Əvvəlcədən ödə**, sonra həmin kioskda QR → **Çapa ver** | Sifariş növbəyə düşür və çap olunur; başqa kioskda eyni QR-da kart boz, "KSK-… kioskunda ödənilib" |
| 15 | Hazırladıqlarım → ödənilmiş → **Ləğv et** | Təsdiq dialoqu məbləği deyir; balans həmin məbləğ qədər artır, qaytarma fiskal çeki yaranır |
| 16 | Real telefonda (Android) **Fayllarım → menyu → Çapı planla**: tarix və saat seçiciləri, kiosk, ödəniş | Sistem tarix/saat dialoqları telefonun dilində və 24 saat formatında; 15 dəqiqədən tez vaxt seçiləndə izah çıxır, heç nə yaradılmır |
| 17 | Planlanmış çapın vaxtını 16–17 dəqiqə sonraya qoyun, tətbiqi açıq saxlayın | Vaxt çatanda (≤ 1 dəq gecikmə ilə) "Planlanmış çapın vaxtı çatıb" dialoqu; **Çapa ver** → kiosk çap edir. Tətbiq bağlı olanda gələn push bildirişi T-146-da əlavə olunur və orada yoxlanır |
| 18 | Vaxtı çatmış çapa cavab verməyin (və ya admin paneldə `Schedule.AutoCancelHours`-u 1 edin) | Müddət keçəndə siyahıda "Vaxtı keçdi · Pul balansa qaytarıldı", balans artır, qaytarma fiskal çeki yaranır |
| 19 | Açar OpenBao-da (DEV_ENVIRONMENT "Firebase (FCM) service account açarı"), `docker compose restart api`; Android telefonda tətbiqə daxil olun | Android 13+ bildiriş icazəsini soruşur; API loqunda "Push: FCM HTTP v1, project neoprint-8d6e1" və "Push device … (android, az) registered" |
| 20 | Tətbiqi arxa plana keçirin, planlanmış çapın vaxtını keçmişə çəkin (DEV_ENVIRONMENT 7e) | ≤ 1 dəq ərzində sistem bildirişi "Planlanmış çapın vaxtı çatdı" (telefonun dilində); toxunanda tətbiq **Planlanmışlar**-da açılır, "Vaxtı çatıb" kartında **Çapa ver** / **Vaxtı dəyiş** |
| 21 | Tətbiq açıq ikən kioskda bir sifariş çap olunsun | Sistem bildirişi yox, ekranın yuxarısında banner "Çap hazırdır" + **Aç**; zəngdə sayğac artır, bildiriş siyahıda |
| 22 | Tətbiq tam bağlı (son tətbiqlərdən silinib) ikən çap / refund | Sistem bildirişi gəlir; toxunanda tətbiq açılır və bildirişin ekranına keçir (ana səhifə / Balans), bildiriş oxundu olur |
| 23 | Telefonun dilini rus dilinə keçirin, tətbiqi açın, sonra №20-ni təkrarlayın | Push mətni rusca ("Время печати по расписанию") |
| 24 | Eyni telefonda çıxış edib başqa hesabla daxil olun, birinci hesab üçün №21-i təkrarlayın | Telefona heç nə gəlmir (token ikinci hesaba keçib); birinci hesabın siyahısında bildiriş var |
| 25 | iOS (Mac + Apple Developer hesabı): APNs Auth Key (.p8) Firebase Console → Cloud Messaging → Apple app configuration-a yüklənib, Xcode-da Push Notifications + Background Modes (Remote notifications) açılıb; №19–22 | iOS-da icazə dialoqu, push və toxunanda düzgün ekran |
| 26 | Deep link-lər: başqa skanerlə `neoprint://kiosk/KSK-9002`, `neoprint://voucher/<kod>` QR-ını oxuyun; brauzerdə `https://neoprint.az/r/<kod>` açın | Kiosk detalı; Balans + voucher dialoqu kodla doldurulmuş; çıxış vəziyyətində qeydiyyat dəvət kodu ilə doldurulmuş. `/r/` linkinin soruşmadan açılması üçün produksiya domenində `/.well-known/assetlinks.json` (Android) və Associated Domains (iOS) — domen olanda |
| 27 | Real telefonda (Android və iOS) Profil → **Məlumatlarımı yüklə** → Arxiv hazırla; tətbiqi arxa plana keçirin | Hazır olanda push "Arxiviniz hazırdır"; toxunanda ekran açılır; **Yüklə** telefonun brauzerini açır və ZIP yüklənir (Android: "Yükləmələr", iOS: Safari → Fayllar). Arxivdə qəbzlər PDF, fayllar orijinal + versiyalar |
| 28 | Telefonda faylın menyusu → Avtomatik silinmə → Çapdan sonra; həmin faylı kioskda çap edin | Çapdan sonra fayl siyahıdan gedir, push "Fayl silindi" (tətbiq bağlı olsa da) |
| 29 | Ayrıca test hesabı ilə: Profil → **Hesabımı sil** — əvvəl planlanmış çap varkən, sonra ləğv edib; şifrə + "sil" (kiçik hərflə) | Planlanmış çap varkən forma yox, siyahı və keçid; sonra düymə "sil" yazanda aktivləşir; silinəndə giriş ekranı + "Hesabınız silindi"; telefona həmin hesabın push-u artıq gəlmir |
| 30 | Telefonda tətbiq açıqkən admin panelində `scheduled_print`-i söndürün, tətbiqi arxa plana keçirib qaytarın; sonra kioskun "Tətbiqlə ödə" QR-ını `mobile_payment_at_kiosk` söndürülmüş halda oxudun | Fayl menyusunda "Çapı planla" yox, aktiv planlama yoxdursa "Planlanmışlar" tabı yox (varsa qalır, "Yeni planlama" yox); QR oxunanda ödəniş ekranı açılmır, "bank QR-ı ilə ödəyin" xəbərdarlığı; pul tutulmur. Sonra flag-ları geri açın |
| 31 | Real telefonda Profil → Dil → English; tətbiqi bağlayıb açın; sonra telefonun özünün dilini dəyişib "Telefonun dili"ni seçin və tətbiqə qayıdın. Bu vaxt push gələn bir əməliyyat edin (məs. çap) | Seçim tətbiq yenidən açılanda qalır; push mətni seçilmiş dildədir; "Telefonun dili"ndə telefonun dili dəyişəndə tətbiq də dəyişir və hesabın dili (admin → İstifadəçilər → dil) yenilənir |

## MT-08. Addım 6 — sənəd skanı real telefonda (T-178, T-186; iOS — T-187)

**Nə vaxt:** Addım 6-nın sonunda (lokal stack + real telefon); iOS hissəsi pilotdan əvvəl Mac-da (T-187).
**Harada:** **Samsung S24** (USB ilə qoşulub, USB debugging açıq), telefon kompüterlə eyni Wi-Fi-da
(`DEV_ENVIRONMENT.md` bölmə 6 — real telefon). Skaner Google ML Kit Document Scanner-dir: Google Play xidmətlərinin
içində işləyir, kamera icazəsi istəmir; ilk açılışda Play xidmətləri skaner modulunu bir dəfə endirə bilər (bir neçə saniyə).

**Hazırlıq**

```powershell
docker compose up --remove-orphans -d --build api converter       # Addım 6 API-si
cd mobile
flutter devices                                                    # S24 siyahıdadır (məs. R5CX...)
flutter run -d <S24_id> --dart-define=NEOPRINT_API_URL=http://<kompüterin_IP>:5080/
```

Razılıq addımını görmək üçün **yeni test hesabı** açın (`dev-user`-də seed bütün razılıqları qəbul edib). Xətkeş və
bir bank kartı / vəsiqə (və ya eyni ölçülü kart) lazımdır.

**Vəziyyət (2026-10-05): yoxlanmayıb** — layihə sahibi S24-də icra edəcək. Avtomatik yoxlananlar: backend skan API-si,
real ölçü (PDF-də 85.69 × 54.02 mm, `docs/screenshots/step-6/m2-*`, `m3-*`), mobil ekranların widget testləri
(`test/document_scan_test.dart`), Android APK ML Kit ilə build olunur.

| # | Addım | Gözlənilən |
|---|---|---|
| 1 | Fayllarım → **Fayl yüklə** | Alt vərəqdə üç sətir: "Fayl seç", "Şəkil çək", **"Sənəd skanı"** ("Vəsiqə, pasport, sənəd — kənarlar özü tapılır") |
| 2 | **Sənəd skanı** (yeni hesab) | "Şəxsi sənədlərin skanı" vərəqi admin paneldə dərc olunmuş mətnlə; **İndi yox** — skaner açılmır, Fayllarım-a qayıdış |
| 3 | Yenidən **Sənəd skanı** → **Qəbul edirəm** | ML Kit skaneri açılır (kamera görüntüsü, kənarlar avtomatik çərçivələnir). Admin → İstifadəçilər → hesab → Razılıqlar: `ScanNotice 0.1-draft` |
| 4 | Kartın **ön** üzünü, sonra **arxa** üzünü skan edin → skanerdə **Yadda saxla / Save** | "Skanı hazırla" ekranı: "Səhifə 1", "Səhifə 2"; sənəd tipi "Şəxsiyyət vəsiqəsi / bank kartı" seçili; "Həssas sənəd" açıq və deaktiv ("Şəxsi sənəd — həmişə həssas"); yerləşmə **"Vəsiqə ön + arxa"**, A4 önizləməsində iki kart üst-alt, mərkəzdə |
| 5a | Önizləmənin altında **Düzəliş et** → kartı sürüşdürün, **Hazır** | Yerləşmə chip-ləri "Öz yerləşməm" olur, önizləmə dəyişikliyi göstərir; saxlanan PDF də belədir |
| 5 | Səhifə 1-ə toxunun → küncləri sürüşdürüb kəsin, **Fırlat**, filtr **Ağ-qara**, parlaqlıq +30 → **Hazır** | Redaktorda dəyişiklik dərhal görünür; qayıdanda kiçik şəkil və A4 önizləməsi yenilənir. Fırlatdıqdan sonra kart A4-də dik dayanır (54 × 85.6) |
| 6 | **Səhifə əlavə et** → üçüncü səhifə → zibil qabı ilə silin | Səhifə sayı 3 → 2; yerləşmə yenə "Vəsiqə ön + arxa" |
| 7 | Sənəd tipini **Xüsusi ölçü** edin, en 0 yazın | "1 ilə 1000 mm arasında yazın", **Yadda saxla** deaktiv; düzgün ölçüdən sonra aktiv. Sonra tipi yenidən vəsiqəyə qaytarın |
| 8 | Telefonu təyyarə rejiminə keçirib **Yadda saxla** | Düymədə "Hazırlanır…", sonra qırmızı blok "Skan saxlanılmadı. …" — Fayllarım-da yeni fayl yoxdur; şəbəkəni qaytarıb **Yadda saxla** — uğurlu |
| 9 | Uğurdan sonra | Snackbar "Skan saxlanıldı"; siyahıda "Vəsiqə…" faylı: `Skan`, `Həssas`, "Çapdan sonra silinəcək" chip-ləri; menyuda redaktə yoxdur |
| 10 | Debug build-də: `adb shell run-as az.neoprint.neoprint ls cache` | `scan-*.jpg` qalmayıb (səhifələr saxlanandan sonra silinir) |
| 11 | Faylın menyusu → **Kiosk üçün hazırla** | Yuxarıda info: "real ölçüdə (100%) A4 kağızında, vərəqdə 1 səhifə…"; kağız A4 və "1" seçili və dəyişdirilmir (`m3-mobile-prepare-real-size-lock.png` kimi) |
| 12 | Kioskda (Linux konteyner KSK-9002 və ya real kiosk) şəxsi QR → faylı seçin → ödəyin → çap | Kioskda seçimlər kilidli (`m3-kiosk-real-size-lock.png`); çap edilmiş PDF (`spool/printed`) və ya real printerdə vərəq: **xətkeşlə kart 85.6 × 54 mm ± 1 mm** (real printer — MT-02 ilə birlikdə) |
| 13 | Çapdan sonra | Fayl Fayllarım-dan gedir, push "Fayl silindi" (9.7, addım 5) |
| 14 | Admin → Hüquqi sənədlər → **Sənəd skanı barədə məlumat** yeni versiya dərc edin; telefonda yenidən **Sənəd skanı** | Razılıq vərəqi yenidən çıxır (yeni versiya) |
| 15 | Play xidmətləri olmayan / köhnə Android (mümkünsə ikinci cihaz) | "Bu telefonda sənəd skaneri işləmir…" xəbərdarlığı + **Şəkil çək** düyməsi kamera ekranını açır |
| 16 | **iOS (T-187, Mac + real iPhone):** `flutter build ios` (və ya Xcode-da Run); №1–13 | `AppDelegate.swift`-dəki VisionKit skaneri compile olunur və işləyir: `VNDocumentCameraViewController` açılır, səhifələr eyni ekrana gəlir, ləğv edəndə heç nə yaranmır |
