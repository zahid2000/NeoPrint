# ADR-0002 — Explicit state transition mexanizmi

### Status

Accepted

### Context

`docs/STATE_MACHINES.md` yeddi state maşını təsvir edir (PrintJob, Payment,
Refund, Device, Kiosk, Update, ScheduledPrint) və tələb edir ki:

- icazəsiz keçid mümkün olmasın;
- hər keçiddə actor, səbəb, CorrelationId və vaxt saxlanılsın;
- keçid idempotent olsun;
- keçid DB-də atomik qorunsun (transaction və ya optimistic version);
- `job.Status = ...` kimi raw property assignment istifadə olunmasın.

Mövcud vəziyyət (audit hesabatı, A4): `KioskDevice.Activate()` / `Revoke()` və
`Kiosk.BindDevice()` / `UnbindDevice()` metodları var idi, lakin:

1. `Status` sahələri public setter-li idi və Application layer-də bir yerdə
   (`DeviceHandlers.cs:135`) birbaşa dəyişdirilirdi;
2. heç bir keçid metadata-sı yazılmırdı — nə actor, nə səbəb, nə CorrelationId;
3. icazəli keçidlərin siyahısı yox idi (`Revoke()` istənilən statusdan işləyirdi,
   `Expire()` metodu heç yox idi);
4. `ReplaceOneAsync` filtri yalnız `Id` idi — last-write-wins.

İndi yalnız Device və Kiosk mövcuddur, digər beş maşın sonrakı addımlarda gəlir.
Mexanizm hamısına yarayacaq şəkildə qurulmalıdır, lakin gələcək entity-lər indi
yaradılmamalıdır (`CLAUDE_CODE_EXECUTION_RULES.md` §2, §5).

### Decision

**1. `StateMachine<TStatus>` — icazə cədvəli.**
Hər aggregate öz cədvəlini `static readonly` sahə kimi özü ilə yanaşı elan edir
(`KioskDevice.Transitions`, `Kiosk.Transitions`). `Allow(from, to...)` və sink-lər
üçün `AllowFromAny(to)`. **Whitelist** yanaşması: elan olunmayan hər şey rədd
olunur, ona görə enum-a yeni status əlavə olunanda o, kimsə icazəni yazana qədər
heç yerə keçə bilmir (fail closed).

**2. `ActorContext` — kim və nə üçün.**
`ActorType`, `ActorId`, `DeviceId`, `Reason`, `CorrelationId`. Transition
metodları onu **parametr kimi tələb edir** — actor-suz status dəyişikliyi
mümkün olmasın deyə. Fabrik metodları: `ActorContext.Admin(...)`,
`.Kiosk(...)`, `.User(...)`, `.System(...)`; `Because(reason)` və
`WithCorrelationId(...)` ilə zəncirlənir.

**3. `state_transitions` — ayrıca kolleksiya.**
Aggregate daxilində massiv deyil: kiosk və print job ömrü boyu keçid toplayır,
sənəd daxilindəki qeyri-məhdud massiv nəticədə 16 MB limitinə dəyir. Plan
(Bölmə 5) da ayrıca kolleksiya deyir. Statuslar **ad kimi** saxlanılır (rəqəm
kimi yox) — enum-a üzv əlavə olunanda köhnə qeydlərin mənası dəyişməsin.

**4. Keçidlər entity-də toplanır, repository yazır.**
Domain metodu `Record(...)` ilə `Entity.PendingTransitions`-ə əlavə edir;
`AggregateWriter` saxlayarkən `DrainTransitions()` edib `state_transitions`-a
yazır. Beləliklə "hər keçid qeyd olunur" **konstruksiya ilə** doğrudur — handler
unuda bilməz. Drain (kopyalama yox) ikinci save-in eyni qeydi təkrar yazmasının
qarşısını alır.

**5. `Entity.RowVersion` — optimistic concurrency.**
`ReplaceOneAsync` filtri `Id == x && RowVersion == expected`; `MatchedCount == 0`
olarsa `ConcurrencyException`. Uğursuz yazıdan sonra entity-nin versiyası geri
qaytarılır ki, retry heç vaxt yazılmamış versiya ilə işləməsin.

`Version` yox, **`RowVersion`**: `LegalDocument.Version` və `Consent.Version`
artıq var və tamam başqa şey (sənədin dərc versiyası) deməkdir.

**6. Status setter-ləri `private set`.**
Kompilyator qaydanı məcbur edir. Bu, dərhal Application layer-dəki mövcud raw
assignment-ləri aşkarladı.

### Alternatives

- **Hər keçid üçün ayrıca metod, cədvəl olmadan (`Pause()`, `Resume()`, ...).**
  Qaydalar metodların içinə səpələnir; "bu statusdan hara keçmək olar?" sualını
  cavablandırmaq üçün bütün metodları oxumaq lazım gəlir. Cədvəl bir yerdədir.
- **`StatusHistory[]` aggregate daxilində (planın ilkin variantı).**
  Sadədir, lakin sənəd ölçüsü artır və "bütün kiosklarda son 24 saatda nə oldu?"
  sorğusu bütün sənədləri oxumağı tələb edir. Plan özü böyük entity-lər üçün
  ayrıca kolleksiya deyir.
- **Domain event-lər + event handler-lər keçidi yazsın.**
  Daha çox mexanizm; MassTransit outbox artıq var, lakin keçid qeydi audit
  məlumatıdır və onu asinxron yazmaq o deməkdir ki, DB-də status dəyişib, qeyd
  isə hələ yoxdur. Sinxron yazı daha doğrudur.
- **Yalnız Mongo transaction, RowVersion olmadan.**
  Transaction eyni anda iki yazını ardıcıllaşdırır, lakin "oxu → düşün → yaz"
  pəncərəsini bağlamır: iki sorğu eyni sənədi oxuyub ikisi də yaza bilər.
  RowVersion məhz bu pəncərəni bağlayır.

### Consequences

Müsbət:
- İcazəsiz keçid kompilyasiya + runtime səviyyəsində mümkün deyil.
- Hər keçid kim, nə üçün, hansı sorğuda — cavabı ilə yazılır.
- Qalan beş state maşını (PrintJob, Payment, Refund, Update, ScheduledPrint)
  yalnız öz cədvəlini elan etməlidir; mexanizm hazırdır.
- Eyni anda iki yazı səssizcə itmir.

Mənfi:
- Transition metodlarının imzaları dəyişdi (`actor`, `utcNow` parametrləri) —
  mövcud domain testləri yeniləndi. Davranış iddiaları dəyişmədi.
- Hər status dəyişikliyi bir əlavə yazı (`state_transitions`) deməkdir.
- Keçid qeydi ilə aggregate iki ayrı yazıdır. Transaction daxilində (cihaz
  aktivləşdirmə) birlikdə commit olur; transaction xaricində aradakı çöküş
  qeydi itirir, state-i yox — təhlükəsiz istiqamət budur.

### Security Impact

- `Revoked` sink olduğu üçün ləğv edilmiş cihazın "geri qayıtması" artıq
  kompilyasiya səviyyəsində deyil, cədvəl səviyyəsində qadağandır.
- Hər keçiddə actor saxlanıldığı üçün admin sui-istifadəsi izlənə bilir
  (`SECURITY_THREAT_MODEL.md` → "Admin abuse").
- `state_transitions` heç bir secret, token və ya PII saxlamır — yalnız
  identifikatorlar, status adları və sərbəst mətn `Reason`.

### Operational Impact

- Yeni kolleksiya + 3 indeks (entity üzrə tarixçə, CorrelationId, tarix).
- Kolleksiya sonsuz böyüyür; saxlama müddəti `DATA_RETENTION.md` çərçivəsində
  sonrakı addımda təyin olunmalıdır (hələlik silinmir — **məlum boşluq**).
- `ConcurrencyException` 409 kimi görünür; panel və mobil bunu "yenidən cəhd
  edin" kimi göstərməlidir.

### Migration

Mövcud `kiosks` və `kiosk_devices` sənədlərində `RowVersion` yoxdur; Mongo
onu 0 kimi oxuyur, ona görə ilk yazı `RowVersion == 0` filtri ilə uğurlu olur.
Data migration lazım deyil. Keçmiş status dəyişiklikləri üçün geriyə dönük
`state_transitions` qeydi yaradılmır — tarixçə bu dəyişiklikdən başlayır.

### Related Plan Step

Addım 1 — "Kross-kəsən infrastruktur (ilk gündən)": explicit state transition
bazası.

### Date

2026-09-24
