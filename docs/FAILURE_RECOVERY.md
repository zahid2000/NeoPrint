# NeoPrint --- Failure & Recovery

NeoPrint printer və kiosk səviyyəsində hardware failure-ları normal
sistem vəziyyəti kimi qəbul edir.

## Əsas failure ssenariləri

-   internet itir
-   backend unavailable
-   Redis unavailable
-   RabbitMQ unavailable
-   printer offline
-   printer paper out
-   printer jam/error
-   kiosk reboot
-   electricity loss
-   application crash
-   payment timeout
-   provider timeout
-   fiscal provider unavailable
-   converter crash
-   storage unavailable

## Source of truth

Recovery MongoDB-dəki biznes state-dən başlamalıdır.

Redis/RabbitMQ müvəqqəti itərsə biznes state itmiş hesab olunmamalıdır.

## Power loss

Kiosk yenidən qalxdıqda:

1.  device identity bərpa olunur;
2.  local encrypted journal/spool oxunur;
3.  backend ilə state reconciliation edilir;
4.  `Printing` job üçün printer execution evidence yoxlanılır;
5.  eyni job ikinci dəfə çap edilməməlidir;
6.  təhlükəsiz olduğu halda `Queued` job davam edir.

## Print execution

PrintJob ilə real printer əməliyyatı arasında ayrıca execution/attempt
məlumatı saxlanmalıdır.

Tövsiyə:

-   JobId
-   AttemptNo
-   StartedAt
-   FinishedAt
-   PrinterName
-   PrinterCounterBefore
-   PrinterCounterAfter
-   ReportedSheets
-   ReportedSides
-   Status
-   ErrorCode

## Recovery rule

Sistem "çap olubmu?" sualını yalnız application memory/state ilə
cavablandırmamalıdır.

Mümkün printer counter/status/evidence istifadə edilməlidir.

## Nəticəsi naməlum job (`Suspected`)

Bu sənədin qalan hissəsi sistemin **nə baş verdiyini bildiyi** halları
təsvir edir. Ayrıca bir hal da var: kiosk ilə server arasında əlaqə itir və
sistem çapın olub-olmadığını **bilmir** (plan Bölmə 2.5, ssenari B).

Qayda:

1.  `Job.SuspectedAfterMinutes` (default 15) müddətində kioskdan status və ya
    heartbeat gəlməyən `Printing` / `Queued` job → **`Suspected`**.
2.  **Avtomatik refund edilmir.** Ödəniş `Succeeded` qalır, job ləğv olunmur.
3.  Incident (High) + admin panelində "Baxış gözləyən job-lar".

Səbəb: naməlum nəticədə avtomatik qaytarmaq çap olunmuş işi pulsuz verməkdir,
avtomatik qaytarmamaq isə çap olmayıbsa müştərinin pulunu udmaqdır. Hər ikisi
pul itkisidir. Sistem təxmin etmir — qərar məlumatı olan adama verilir.

**Kioskun özündə bərpa (Addım 3).** Kiosk hər cəhddən əvvəl jurnala
`Printing` + printer + başlama vaxtı + printer sayğacını (SNMP, varsa) yazır,
spooler işi qəbul edən kimi onun istinadını (CUPS request id / Windows sənəd
adı) əlavə edir. Restart-dan sonra `Printing` qalan iş üçün:

-   spooler-də hələ var → izlənir, növbəti iş gözləyir;
-   spooler "tamamlandı" deyir → `Completed`;
-   spooler tapmır və ya "uğursuz" deyir → sayğac qərar verir: işin bütün
    vərəqləri (sayğac vərəq sayırsa) və ya bütün üzləri (təəssürat sayırsa)
    çıxıbsa → `Completed`, əks halda → `Interrupted` (`printer.restarted`,
    çıxan vərəq sayı ilə). Sayğac yoxdursa → `Interrupted`.

Heç bir halda iş yenidən çapa göndərilmir. Hər cəhd `print_attempts`-ə
(sayğac əvvəl/sonra, `recovered`) gedir.

**Bərpadan sonra avtomatik həll.** Kiosk qalxanda lokal jurnalı və
`print_attempts` qeydlərini (printer sayğacının əvvəl/sonra dəyəri daxil)
göndərir. `SuspectedJobResolutionConsumer`:

-   çap tamamlanıb → `Completed`, refund yoxdur;
-   çap olmayıb və ya yarımçıqdır → `Interrupted` → **A ssenarisi**, avtomatik
    refund işə düşür.

Yəni admin qərarı yalnız kiosk **heç vaxt qayıtmayanda** lazım olur.
`Job.SuspectedAutoResolveHours` (default 72) keçəndə sistem yalnız
**xatırlatma** göndərir; avtomatik heç nə etmir.

Bu, "Power loss" bölməsindəki bərpa axınının davamıdır: orada kiosk özü
bərpa olunur, burada isə **backend** kiosk susduğu müddətdə nə edəcəyini
bilir.

## Converter failure

Converter: - timeout ilə dayandırılmalıdır; - resource limit tətbiq
edilməlidir; - temp files təmizlənməlidir; - failed conversion
retry-safe olmalıdır; - istifadəçiyə təhlükəsiz error göstərilməlidir.

## Recovery test

Ən azı bunlar avtomatlaşdırılmış test/simulyasiya kimi yoxlanmalıdır:

-   payment-dən sonra process crash
-   Printing zamanı reboot
-   Redis loss
-   RabbitMQ temporary loss
-   printer offline
-   duplicate payment callback
-   duplicate refund event
-   failed update + rollback
