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
