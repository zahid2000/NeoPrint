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
