# NeoPrint — State Machines

> **Qeyd:** bu sənəd `NeoPrint-PLAN.md` ilə uzlaşdırılıb. Status adları plandakı adlarla eynidir; ziddiyyət görünərsə plan üstündür.

Bu sənəd biznes state-lərinin explicit və idarə olunan keçidlərini müəyyən edir.

## PrintJob

Əsas axın:

`Draft → PendingPayment → Paid → Queued → Printing → Completed`

Ödəniş vaxtı keçir:

`PendingPayment → Expired`

Ləğv:

`Queued → Cancelled`, `PendingPayment → Cancelled`

Tətbiqdə əvvəlcədən ödənilib kioskda götürülməyi gözləyən sifariş (plan 9.4,
`HeldForUser`; `docs/DECISIONS.md` T-144) ödənişdən sonra növbəyə düşmür,
`Paid` qalır. İstifadəçi QR-ını həmin kioskda göstərəndə `Paid → Queued`;
sahibi ləğv edəndə və ya `PreparedPrint.AutoCancelDays` keçəndə
`Paid → Cancelled` (pul balansa qaytarılır).

Printer/kiosk problemi — **nəticə məlumdur** (kiosk serverlə əlaqədədir):

`Printing → Interrupted`
`Interrupted → Queued` (təhlükəsiz olduqda təkrar növbəyə)
`Interrupted → Cancelled`

**`Interrupted → Queued` nə vaxt baş verir (plan 7.4, 9.6; T-209).** Kioskun printeri yenidən hazır olanda
(`ClearKioskFaultCommand`) həmin kioskda `Interrupted` qalan və **sahibi olan** sifarişlər növbəyə qayıdır
(`PrintJob.Enqueue`, səbəb `printJobs.reason.printerRecovered`). Qonağın bank ödənişi A ssenarisi ilə artıq
qaytarılıb və sifarişi `Cancelled`-dir — o qayıtmır. Sifariş avtomatik olaraq ən çox
`KioskFaultHandlers.MaxAutomaticAttempts` (3) dəfə çapa gedir: üçüncü dayanmadan sonra `Interrupted` qalır,
incident (High) açılır — sahibi ləğv edə bilər, qərar adamındır.

`PrintJob.Runs` — sifarişin neçənci dəfə növbəyə düşdüyü. Hər çap cəhdinin sərf etdiyi kağız ayrıca
sayılır (`supply_events.DedupKey`: `job:{id}`, ikinci dəfədən `job:{id}:{run}`).

**Sahibinin ləğvi (plan 6 `POST print-jobs/{id}/cancel`; 9.6 A.5).** İstifadəçi öz sifarişini yalnız gözləyəndə
ləğv edir: `Queued → Cancelled`, `Interrupted → Cancelled`. `Printing` (kağız artıq gedir) və `Suspected`
(nəticə naməlumdur — qərar administratorundur, plan 2.5 B) ləğv olunmur.

Kiosk ilə əlaqə itib — **nəticə naməlumdur** (Plan Bölmə 2.5, ssenari B):

`Queued → Suspected`
`Printing → Suspected`

`Suspected`-dən çıxış yalnız **yeni məlumatla** olur — vaxt keçməsi ilə yox:

`Suspected → Completed` (kiosk çapın tamamlandığını bildirir, və ya admin
"Qaytarma yoxdur" seçir)
`Suspected → Interrupted` (kiosk çapın olmadığını bildirir → A ssenarisi,
avtomatik refund)
`Suspected → Cancelled` (admin "Tam qaytar" seçir)
`Suspected → Queued` (admin "Yenidən çapa göndər" seçir)

`Suspected` job üçün **avtomatik refund yoxdur** və ödəniş `Succeeded` qalır.
`Job.SuspectedAutoResolveHours` keçməsi heç bir keçid yaratmır — yalnız
xatırlatma bildirişi göndərir. Səbəb `docs/FAILURE_RECOVERY.md`-dədir:
naməlum nəticədə hər iki avtomatik qərar pul itkisidir, ona görə sistem
təxmin etmir.

Refund:

- Job `Cancelled` (və ya hissəvi çapla `Completed`) olduqda **ayrıca `Refund` entity** yaradılır.
- Job özündə `RefundPending` / `Refunded` statusu **saxlanılmır**; refund vəziyyəti `refunds` kolleksiyasındadır (Plan Bölmə 2.5).
- Job-un maliyyə nəticəsi `payments.Status` (`Refunded` / `PartiallyRefunded` / `RefundFailed`) ilə əks olunur.

## Refund

`Pending → Processing → Succeeded`

Failure:

`Processing → Failed`

`Failed → Processing` (admin təkrar cəhd və ya istiqamət dəyişikliyi ilə)

Qaydalar:
- Bir ödənişə bir neçə refund ola bilər, cəmi ödəniş məbləğini keçə bilməz.
- Eyni səbəb + eyni job üçün ikinci avtomatik refund yaradıla bilməz (idempotency).

## Payment

`Pending → Succeeded`

Failure:

`Pending → Failed`

Timeout:

`Pending → Expired`

Gec təsdiq (bank pulu intent-in vaxtı keçəndən sonra təsdiqləyir — gec və ya itmiş callback, uzlaşdırma):

`Expired → Succeeded` — pul tutulub, ona görə uçotda belə yazılır; job artıq ödənilə bilmədiyi üçün ödəniş eyni tranzaksiyada avtomatik qaytarılır (`docs/DECISIONS.md`, 2026-09-30).

Refund nəticəsi:

`Succeeded → PartiallyRefunded | Refunded | RefundFailed`

`PartiallyRefunded → Refunded | RefundFailed`, `RefundFailed → PartiallyRefunded | Refunded` (admin təkrar cəhdi və ya istiqamət dəyişikliyi ilə)

## Kiosk Device

`Pending → Active`

`Active → Revoked`

`Pending → Expired` (aktivləşdirilmədən vaxtı keçir)

`Revoked` device yenidən aktivləşdirilməməlidir; yeni cihaz qeydiyyatı (yeni açar) tələb olunur.

## Kiosk (biznes entity)

`NotActivated → NotConfigured → Online`

`Online ↔ Paused`, `Online ↔ Offline`, `Online → Maintenance`, `* → Disabled`

`Maintenance → Paused`, `Offline → Paused`, `NotConfigured → Paused` — yalnız printerin nasazlığı hələ
davam edəndə (aşağıda).

**Printerin nasazlığı (plan 9.6, ssenari A; T-209).** `Kiosk.DeclareFault(code)` nasazlığı yadda saxlayır
(`FaultSince`, `FaultCode` — mesaj açarı, məs. `printer.outOfPaper`) və `Online → Paused` edir;
`Kiosk.ClearFault` onu götürür və `Paused → Online`. `Paused` nasazlığın **öz** statusudur: administratorun
fasiləsi `Maintenance`-dir, itmiş əlaqə `Offline`.

- Nasazlıq üç yolla elan olunur: (1) heartbeat `printerOnline: false` deyir və bu
  `Kiosk.PrinterFaultAfterSeconds` (30) davam edir — bir pis oxunuş nasazlıq deyil (`PrinterDownSince`
  ilk pis oxunuşu saxlayır, yaxşı oxunuş onu sıfırlayır); (2) sifariş printerin öz xətası ilə
  (`printer.offline / outOfPaper / paperJam / notFound / error`) `Interrupted` olur — dərhal, gözləmədən;
  (3) kiosk `POST incidents` ilə özü deyir, və ya seçilmiş printer onun siyahısından itir.
- Nasazlıq `printerOnline: true` deyən ilk heartbeat-də bitir.
- Daha güclü vəziyyət nasazlıqdan üstündür, amma onu unutdurmur: `Maintenance` və ya `Offline` kiosk
  statusunu saxlayır, nasazlıq isə yadda qalır — fasilə qalxanda (`ResumeByAdmin`), kiosk təhlükəsiz rejimdən
  çıxanda və ya heartbeat qayıdanda printer hələ işləmirsə kiosk `Online` yox, `Paused` olur
  (`Maintenance → Paused`, `Offline → Paused`, `NotConfigured → Paused`).
- `Kiosk.WorkingStatus`: administrator fasiləsi varsa `Maintenance`, yoxdursa nasazlıq varsa `Paused`,
  yoxdursa `Online`.

**Əlaqənin itməsi (plan 9.6, ssenari B; T-209).** `KioskHeartbeatMonitor` (15 saniyədə bir)
`Kiosk.OfflineAfterSeconds` (60) susmuş kiosku `Online | Paused | NotConfigured → Offline` edir və incident
(Critical) açır. `Maintenance` kimsə qərarıdır — status qalır, incident yenə açılır. Heç nə ləğv olunmur,
heç nə qaytarılmır (sifarişlərin taleyi `StuckJobWatchdogJob`-undur). Növbəti heartbeat kiosku qaytarır və
incident-i bağlayır.

**Administratorun fasiləsi (plan 7.8, `Pause` / `Resume`; T-208).** `Kiosk.PauseByAdmin` bayraq qoyur
(`PausedByAdminSince`) və kioskun statusunu `Maintenance` edir: `Online | Paused | Offline → Maintenance`.
`NotConfigured` kiosk statusunu saxlayır (onsuz da sifariş almır), bayraq isə qalır: test çapı keçəndə
`NotConfigured → Maintenance` olur. Bayraq durduqca:

- heartbeat-in qayıtması `Maintenance`-i götürmür (bu, kiminsə qərarıdır);
- kioskun safe mode-dan çıxması da götürmür — fasilə administratorundur;
- yeni sifariş serverdə dərhal rədd olunur (`Status != Online`), kiosk hələ xəbər tutmasa belə.

`Kiosk.ResumeByAdmin`: `Maintenance → Online` (printer seçilməyibsə `Maintenance → NotConfigured`); kiosk
hələ safe mode-dadırsa `Maintenance` qalır və yenidən safe mode-un olur.

## Kiosk əmri (`KioskCommand`)

`Sent → Acknowledged → Succeeded | Failed`

`Sent → Succeeded | Failed` (dərhal cavablanan əmrlər: `RefreshPrinters`, `TestPrint`, `Pause`, `Resume`)

`Sent | Acknowledged → Expired` (24 saat cavabsız)

`Acknowledged` — "aldım, icra edəcəyəm": `RestartApp` və `Reboot`-u ondan sonra qalxan tətbiq cavablayır.
Təkrar təsdiq və təkrar cavab heç nə dəyişmir.

Cihaz ayrıldıqda: `* → NotActivated`.

## Servis tapşırığı (`ServiceTask`, plan 2.15)

`Open → Assigned → InProgress → Done`

`Open → InProgress` (hovuzdan götürən operator onu dərhal başlayır — tapşırıq onun olur)

`Assigned → Open` (administrator hovuza qaytarır); `Assigned → Assigned` başqa operatora verilməsi keçid deyil — yalnız
`AssignedTo` dəyişir

`Open | Assigned | InProgress → Cancelled` (səbəb məcburidir); `Done`-dan çıxış yoxdur.

- `InProgress` tapşırıq yalnız onu başlayan operator tərəfindən tamamlanır və başqasına verilmir.
- `Done` və `Cancelled` son vəziyyətlərdir (`Active = false`); avtomatik açılan tapşırığın açarı (`Key`) yalnız
  aktiv tapşırıqlar arasında unikaldır — bitəndən sonra eyni material üçün yenisi açıla bilər.
- Prioritet yalnız sistemlə **qalxır** (alert `Critical` olanda); administrator istədiyi kimi dəyişə bilər.
- Keçidlər `state_transitions`-a yazılır (aktor: operator / administrator / `System`).

## Sərf alert-i (`SupplyAlert`, plan 2.15)

Status sahəsi yoxdur: `Active = true` (səviyyə `Warning` və ya `Critical`) → `Active = false` (`ClearedAt`). Səviyyə
`Warning ↔ Critical` arasında hər iki istiqamətdə dəyişə bilər; hər dəyişiklik bir bildirişdir, eyni səviyyədə qalan
alert təkrar bildirilmir. Bağlanmış alert yenidən açılmır — növbəti düşüş yeni alert-dir.

## Fayl (`StoredFile`)

`Pending → Converting → Ready`

`Pending | Converting → Failed`

`Failed → Converting` (yenidən cəhd)

`Ready → Converting` (yenidən konvertasiya)

`Ready → Pending` — **redaktə** (`StoredFile.ApplyEdit`): redaktə yeni versiya kimi
saxlanılır, orijinal toxunulmaz qalır; fayl upload kimi yenidən çevrilir və səhifə
sayı (qiymət) redaktəyə uyğunlaşır. Redaktə yalnız `Ready` faylda mümkündür.

## Storage sinxronizasiya işi (`StorageSyncJob`)

`Pending → Running → Verifying → Completed`

`Running ↔ Paused`, `Pending | Running | Paused → Cancelled`, `Running | Verifying → Failed`

`Failed → Running | Verifying` ("Uğursuzları təkrarla"), `Completed → Running`

Mənbənin təmizlənməsi status deyil, tamamlanmış migrasiyanın alt vəziyyətidir:
`RequestSourcePurge` (yalnız `Completed` migrasiya, `Storage.SourceRetentionDays` keçəndən
sonra) → `CompleteSourcePurge`.

## Update (kiosk versiyası)

`Idle → Downloading → Ready → Applying → HealthCheck → Succeeded`

Failure:

`Downloading | Ready | Applying | HealthCheck → Failed`

Rollback:

`HealthCheck | Failed → RolledBack`

## ScheduledPrint

`PendingPayment → Scheduled → AwaitingUser → Sent`

`Scheduled → Sent` ("Çapa ver" vaxtdan əvvəl, `docs/DECISIONS.md` T-145)

`AwaitingUser → Scheduled` ("Vaxtı dəyiş")

`PendingPayment | Scheduled | AwaitingUser → Cancelled`

`AwaitingUser → Expired` (auto-cancel + refund)

`PendingPayment` — planlama yaradılıb, onun job-u hələ ödənilməyib. Ödəniş gəlməsə (intent bitir) job `Expired` olur və planlama `Cancelled` olur, istifadəçinin siyahısında görünmür. Sifarişin özü `Source=Scheduled` job-dur. Bu job ödənişdən sonra `Paid` qalır (`HeldForUser`) və "Çapa ver" ilə `Paid → Queued` keçidini edir. Ləğv və `Expired` zamanı job ləğv olunur (`Paid → Cancelled`), pul balansa `RefundReason.ScheduleCancel` ilə qayıdır. Job başqa yolla ləğv olunsa (admin), planlama da `Cancelled` olur.

## Referral

`Registered → Rewarded`

Dəvət olunan referral kodu ilə qeydiyyatdan keçəndə `Registered` yaranır (bir dəvət olunana bir qeyd, `InviteeId` unikal). Dəvət
olunanın **ilk pulla ödənmiş** (`Parts.Money > 0`) `Completed` job-u onu **bir dəfə həll edir** (`QualifiedAt`, `QualifyingJobId`):
heç nə mane olmursa `Rewarded` (bonuslar həmin an yazılır), mane olursa `Registered` qalır və `Hold` səbəbi yazılır —
`SameDevice`, `MonthlyLimit`, `InviterInactive`. Həll olunmuş qeyd bir daha dəyişmir; job sonradan refund olunsa da bonus geri
alınmır (`DECISIONS.md` 2026-10-05).

## Qaydalar

- İcazəsiz state transition mümkün olmamalıdır.
- Hər transition üçün actor və timestamp saxlanmalıdır.
- Transition idempotent olmalıdır; eyni transition event ikinci dəfə gəldikdə duplicate side effect olmamalıdır.
- `Completed` job yenidən `Printing` vəziyyətinə keçirilə bilməz.
- Tam refund olunmuş ödəniş üçün yeni refund yaradıla bilməz.
- State transition DB-də atomik şəkildə qorunmalıdır (Mongo transaction / optimistic version).

## Transition metadata

Hər keçiddə (`StatusHistory[]` və ya `state_transitions`) saxlanmalıdır:

- FromStatus
- ToStatus
- ActorType (User / Kiosk / Admin / System)
- ActorId
- DeviceId
- Reason
- CorrelationId
- CreatedAt

## Implementation

State dəyişikliklərini sərbəst property assignment ilə etmə:

```csharp
job.Status = PrintJobStatus.Completed;   // YANLIŞ
```

Domain səviyyəsində explicit transition istifadə et:

```csharp
job.Complete(printedSides, printedSheets, actor, correlationId);   // DÜZGÜN
```

İcazəsiz keçid `DomainException` ilə dayandırılmalıdır.
