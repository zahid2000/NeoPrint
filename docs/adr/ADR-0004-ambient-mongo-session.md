# ADR-0004 — Ambient MongoDB sessiyası: `MongoSet<T>` və `MongoContext.Enlist`

### Status

Accepted

### Context

Addım 1-in Definition of Done bəndlərini canlı yoxlayarkən aktivləşdirmə
əməliyyatı **yarım tətbiq olunmuş** vəziyyət buraxdı: cihaz sənədi yazıldı,
kiosk sənədinin yazısı `409` ilə uğursuz oldu, çağıran xəta aldı, lakin cihaz
bazada `Active` qaldı — heç bir kioska bağlı olmayan, lakin özünü aktivləşdirilmiş
sayan cihaz.

Səbəb kodda açıq idi. Pipeline-da `TransactionBehavior` mövcuddur,
`ActivateDeviceCommand` `ITransactionalRequest` ilə işarələnib və
`MongoTransactionRunner` həqiqətən sessiya açıb `StartTransaction()` çağırır.
Lakin **sessiya heç kimə verilmirdi**: bütün repository-lər və `AggregateWriter`
sürücü kolleksiyasını birbaşa çağırırdı —

```csharp
await collection.ReplaceOneAsync(filter, entity, cancellationToken: ct);
```

MongoDB sürücüsü əməliyyatı tranzaksiyaya **yalnız sessiya ötürüldükdə** daxil
edir. Ötürülmədiyi üçün hər yazı tranzaksiyadan kənarda gedirdi; `commit` və
`abort` heç nəyə təsir etmirdi. Yəni mexanizm var idi, effekti yox idi — və bu,
plan Bölmə 10 və `docs/IDEMPOTENCY.md`-nin atomiklik tələbini pozurdu.

Addım 2-də ödəniş, refund və wallex ledger yazıları bu mexanizmin üstündə
qurulacaq, ona görə problem kod baqından pul itkisinə çevrilə bilərdi.

### Decision

**1. Sessiya ambient saxlanılır: `MongoContext.Session` (`AsyncLocal`).**

`MongoTransactionRunner` tranzaksiya açandan sonra `_context.Enlist(session)`
çağırır; qaytarılan scope dispose olunanda ambient sessiya təmizlənir. Sessiya
`static AsyncLocal<IClientSessionHandle?>`-dadır, çünki bir sorğu ərzində
`MongoContext` bir necə dəfə həll oluna bilər, tranzaksiya isə birdir.

**2. Kolleksiyalara yalnız `MongoSet<T>` vasitəsilə çıxış var.**

`MongoContext`-in bütün kolleksiya property-ləri artıq
`IMongoCollection<T>` yox, `MongoSet<T>` qaytarır. `MongoSet<T>` oxu və yazı
metodlarını eyni adlarla açır və ambient sessiya varsa onu sürücüyə özü ötürür:

```csharp
public Task InsertOneAsync(TDocument document, CancellationToken cancellationToken = default)
    => Session is { } session
        ? _collection.InsertOneAsync(session, document, cancellationToken: cancellationToken)
        : _collection.InsertOneAsync(document, cancellationToken: cancellationToken);
```

Metod adları və imzaları eyni saxlanıldığı üçün **90 çağırış yerinin heç biri
dəyişmədi** — repository kodu və mövcud testlər olduğu kimi qaldı.

**3. Oxular da sessiyaya qoşulur.**

Sənədi oxuyub qərar verib yazan əməliyyat öz əvvəlki yazılarını görməlidir və
başqalarından təcrid olunmalıdır.

**4. Yan keçmək mümkün deyil, təsadüfən isə heç mümkün deyil.**

- Sürücü kolleksiyası `MongoSet<T>.WithoutSession` ilə yalnız assembly daxilində
  əlçatandır və **yalnız indeks işi** üçündür (MongoDB tranzaksiya içində indeks
  əmrlərini qəbul etmir, indekslər isə startup-da yaradılır).
- Tranzaksiya işlədiyi halda `WithoutSession` **exception atır**: tranzaksiyadan
  yan keçən yazı `abort`-dan sağ çıxır, ona görə səssiz davranmaqdansa ilk
  sorğuda dayanmaq düzgündür.
- `Enlist` iç-içə tranzaksiyanı rədd edir: daxili tranzaksiya səssizcə xarici
  tranzaksiyaya qoşulsaydı, handler commit etdiyini düşünərkən etməmiş olardı.
- `PersistenceRulesTests` mənbə kodunu yoxlayır: `NeoPrint.Infrastructure`
  daxilində `MongoContext.cs` və `MongoSet.cs`-dən başqa heç bir fayl
  `IMongoCollection<`, `GetCollection<` və ya `WithoutSession` istifadə edə
  bilməz; `MongoSet<T>`-in hər yazı metodu ambient sessiyanı istifadə etməlidir;
  `MongoTransactionRunner` `Enlist` çağırmalıdır.

### Alternatives

**Sessiyanı parametr kimi ötürmək** (`repository.UpdateAsync(session, entity, ct)`).
Rədd edildi: 90 çağırış yeri və hər yeni repository metodu üçün bir daha
unudulma şansı. Bir dəfə unudulsa nəticə səssizdir — yazı gedir, abort onu geri
qaytarmır. Baqın ilk növbədə yaranma səbəbi elə budur.

**`IMongoCollection<T>` üçün tam decorator.** Rədd edildi: interfeys onlarla
metod və overload daşıyır; istifadə olunmayan metodları da yazmaq lazım gələrdi
və hər biri yeni səhv imkanı olardı. `MongoSet<T>` yalnız layihənin həqiqətən
istifadə etdiyi metodları açır; yeni metod lazım olanda kompilyator dərhal
göstərir.

**Extension metodlar (`collection.InsertOneAsync(context, doc, ct)`).** Rədd
edildi: `context` arqumentini unutmaq yenə mümkündür və kompilyator köhnə
(sessiyasız) overload-u seçib heç nə demir.

**`CommandStartedEvent` ilə runtime nəzarəti.** Nəzərdən keçirildi: sürücü
event-lərinə abunə olub `lsid`-siz yazı əmrini tutmaq. Rədd edildi, çünki event
handler-i əmr göndərildikdən sonra çağırılır — qarşısını almır, yalnız sonradan
xəbər verir; üstəlik paralel tranzaksiyalarda yalançı pozitiv riski var. Tip
sistemi ilə qorumaq daha erkən və daha dəqiqdir.

### Consequences

Müsbət:

- tranzaksiya işarəsi olan əməliyyat həqiqətən atomikdir; `409` və ya hər hansı
  xəta heç bir yazı buraxmır;
- repository kodu sessiya barədə heç nə bilmir və bilməli deyil;
- Addım 2-nin ödəniş/wallet yazıları hazır və işlək mexanizmin üstündə qurulur;
- qayda testlərlə qorunur, yəni gələcəkdə səssizcə pozula bilməz.

Mənfi:

- `MongoSet<T>` layihənin istifadə etdiyi metodları saxlamalıdır: yeni sürücü
  metodu lazım olanda əvvəlcə sarğıya əlavə edilir (kompilyator xəbər verir);
- `AsyncLocal` axına bağlıdır: tranzaksiya daxilində `Task.Run` ilə ayrılan iş
  sessiyanı görmür. Hazırda belə kod yoxdur; belə ehtiyac yaranarsa sessiya
  açıq şəkildə ötürülməlidir;
- tranzaksiya içində indeks yaratmaq artıq exception verir — bu, qəsdəndir.

### Security Impact

Birbaşa təhlükəsizlik nəticəsi: yarım tətbiq olunmuş əməliyyat cihazın
vəziyyətini yanlış qoyurdu (aktivləşdirmə uğursuz olsa da cihaz `Active`).
Aktivləşdirmə və ləğvetmə təhlükəsizlik sərhədidir, ona görə onların atomikliyi
təhlükəsizlik tələbidir, sadəcə məlumat gigiyenası deyil.

### Operational Impact

Konfiqurasiya dəyişikliyi yoxdur. MongoDB replica set rejimində işləməlidir —
artıq belədir (`docker-compose.yml`, `rs0`). Tranzaksiya konflikti olduqda
`MongoTransactionRunner` 3 cəhdə qədər təkrar edir; bu davranış dəyişməyib.

### Migration

Məlumat migrasiyası lazım deyil. Kod migrasiyası: `MongoContext`-in kolleksiya
property-lərinin tipi dəyişdi, lakin metod adları eyni qaldığı üçün heç bir
çağırış yeri redaktə edilmədi (328 test dəyişmədən keçir).

### Related Plan Step

Addım 1 (kross-kəsən infrastruktur: MediatR pipeline behavior-ları, Bölmə 10
tranzaksiya tələbi). Addım 2 və 4-ün ödəniş/wallet yazıları bunun üstündədir.

### Date

2026-09-25

### Yeniləmə — 2026-09-27 (Addım 2): sessiyanın sahibi MassTransit outbox-udur

Qərar dəyişmir (ambient sessiya + `MongoSet<T>`), dəyişən sessiyanın **kim
tərəfindən açıldığıdır**. `MongoTransactionRunner` artıq öz sessiyasını açmır:
tranzaksiyanı MassTransit-in `MongoDbContext`-i (`BeginTransaction` /
`CommitTransaction` / `AbortTransaction`) idarə edir və runner onun
`Session`-ını `_context.Enlist(...)` ilə ambient edir.

Səbəb: `UseBusOutbox` publish olunan hadisəni yaddaşda saxlayır və onu yalnız
öz kontekstinin commit-i outbox kolleksiyasına yazır. Ayrıca sessiya ilə
biznes dəyişikliyi commit olunurdu, hadisə isə heç vaxt yazılmırdı — Addım 2-nin
DoD yoxlamasında bütün fayllar `Pending` qaldı (`docs/DECISIONS.md`). İndi
state dəyişikliyi və onun hadisəsi eyni tranzaksiyada saxlanılır.

Tranzaksiyadan kənarda (upload, background job) `IEventPublisher` hadisəni
birbaşa broker-ə göndərir, çünki orada buferi commit edəcək kontekst yoxdur.
`PersistenceRulesTests.The_transaction_runner_enlists_the_session` hər iki
şərti (outbox sessiyasının enlist olunması, commit-in outbox vasitəsilə getməsi)
yoxlayır; `FilesApiTests.An_upload_is_converted_by_the_bus_on_its_own` isə
hadisənin consumer-ə real olaraq çatdığını.
