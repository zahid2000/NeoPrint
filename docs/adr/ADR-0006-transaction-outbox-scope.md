## ADR-0006 — Tranzaksiyanın outbox-u öz scope-undan götürülür

### Status

Accepted (2026-10-01, Addım 4, T-119)

### Context

Hadisələr (`RefundCompleted`, `PaymentSucceeded`, `PrintJobQueued` …) state dəyişikliyi ilə **eyni MongoDB
tranzaksiyasında** MassTransit-in bus outbox-una yazılmalıdır (plan Bölmə 10, ADR-0004). `MassTransitEventPublisher`
tranzaksiya daxilində DI scope-un `IPublishEndpoint`-ini istifadə edirdi.

HTTP sorğusunda bu, bus outbox-dur. Amma **MassTransit consumer-inin içində** scope-da emal olunan mesajın
`ConsumeContext`-i var və `IPublishEndpoint` (eləcə də `IScopedBusContextProvider<IBus>`) məhz o kontekstə bağlanır:
hadisə outbox-a yox, **birbaşa broker-ə** gedir. Nəticə:

- hadisə commit-dən **əvvəl** çatdırılır — consumer elan olunan state-i hələ görmür;
- tranzaksiya transient konflikt ilə ləğv olunsa da, ilk cəhdin hadisəsi artıq göndərilib.

Sübut (T-119): `RefundCompleted` 37.410-da dərc olundu, fiskal consumer 37.442-də refund-u `Processing` gördü və
qaytarma çekini yaratmadı, commit 37.459-da oldu — `FiscalTests.A_refund_receipt_names_the_sale_and_waits_for_it`
deterministik düşdü. Refund, fiskal çek, qəbz və s. consumer-lərdə işləyən bütün tranzaksiyalar eyni riskdə idi.

### Decision

`MongoTransactionRunner` **hər cəhd üçün yeni DI scope** açır (onda emal olunan mesaj yoxdur):

1. həmin scope-un `MongoDbContext`-i ilə tranzaksiya başlayır;
2. `MongoContext` onun sessiyasına qoşulur (ADR-0004 — ambient sessiya);
3. həmin scope-un `IPublishEndpoint`-i — həmişə bus outbox, bu tranzaksiyanın sessiyasında — ambient
   `TransactionOutbox.Current` kimi qoyulur;
4. commit — state və hadisə birlikdə yazılır; abort — cəhdin hadisələri scope ilə birlikdə atılır.

`MassTransitEventPublisher` tranzaksiya daxilində yalnız `TransactionOutbox.Current`-dən istifadə edir; o yoxdursa
(tranzaksiya runner-dən kənarda açılıbsa) xəta atır — səssiz itki yoxdur. Tranzaksiyadan kənarda əvvəlki kimi `IBus`.

### Alternatives

- **MassTransit consumer outbox** (`UseMongoDbOutbox` endpoint-lərdə): bütün consumer-i bir tranzaksiyaya salır —
  bank/kart/e-kassa çağırışı tranzaksiya daxilində qalardı (orkestrator nümunəsi buna görə iki addımlıdır) və bizim
  runner-in tranzaksiyası ilə toqquşardı. Rədd edildi.
- **Commit-dən sonra birbaşa dərc**: commit ilə dərc arasında proses çökərsə hadisə itir. Plan bunu qadağan edir.
- **Öz outbox kolleksiyamız + dispatcher**: düzgündür, amma MassTransit-in hazır outbox-unu təkrarlayır. Ehtiyac yoxdur.

### Consequences

- Hadisə yalnız commit olunmuş state üçün çatdırılır — HTTP-də də, consumer-də də.
- Hər tranzaksiya cəhdi üçün kiçik scope yaradılır (bir neçə scoped obyekt) — xərci cüzidir.
- Transient retry-də ləğv olunmuş cəhdin hadisələri artıq göndərilmir.

### Security Impact

Yoxdur (eyni broker, eyni məlumat). Dolayı müsbət: maliyyə hadisələri (refund, fiskal) yalnız həqiqi state üçün.

### Operational Impact

Yoxdur. Outbox gecikməsi əvvəlki kimi `QueryDelay` (1 s).

### Migration

Lazım deyil — kod dəyişikliyi. Mövcud outbox mesajları eyni formatdadır.
