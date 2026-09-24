# ADR-0003 — Idempotency: `Idempotency-Key` və `provider_events`

### Status

Accepted

### Context

`docs/IDEMPOTENCY.md` birinci sətrində deyir: "Idempotency Addım 7-yə
saxlanılmır. Sistem daxilində ilk real business operation-lardan etibarən tətbiq
edilməlidir." Plan Addım 1-in iş siyahısına `Idempotency-Key` middleware +
`idempotency_keys` və `provider_events` unikal indeksini əlavə etdi.

Səbəb texniki deyil, fizikidir: cavab almayan çağıran itmiş cavabı itmiş
sorğudan ayıra bilmir, ona görə təkrar göndərir. Qoruma olmasa təkrar sorğu
kartdan ikinci dəfə pul çıxarır. Provayder webhook-ları da 2xx alana qədər (və
bəzən sonra da) təkrar göndərilir.

Audit (A1) göstərdi ki, `grep -rn "Idempotency" backend/src` → 0 nəticə. Eyni
zamanda `IDEMPOTENCY.md` əhatəsinə düşən endpoint-lər artıq mövcuddur:
aktivləşdirmə/provisioning (`devices/register`, `devices/{id}/activate`,
`enrollment-tokens`) və razılıqlar.

### Decision

**1. İki ayrı mexanizm, çünki iki ayrı problem var.**

- `idempotency_keys` — **çağıranın** təkrar sorğusu. Açar çağıran tərəfindən
  gəlir.
- `provider_events` — **provayderin** təkrar callback-i. Id provayderdən gəlir.

**2. `Idempotency-Key` header-i, `[Idempotent]` atributu ilə opt-in.**
Hər yazı endpoint-inə tətbiq edilmir: açar yalnız təkrarın real zərər verdiyi
yerdə məna daşıyır (ödəniş, refund, aktivləşdirmə). Qalanlarında mərasim olardı.

**3. Unikallıq `(Key, ActorKey, Operation)` üzrədir.**
Yalnız `Key` olsaydı, bir çağıranın açarı başqasının cavabını replay edə
bilərdi. `ActorKey` — `user:`, `admin:`, `device:` və ya `anonymous`. Anonim
kiosk müştərisinin öz kimliyi yoxdur, ona görə kioskun kimliyi işlənir: bu, iki
kiosku bir-birindən ayırmaq üçün kifayətdir.

**4. Claim atomik insert-dir, check-then-act yox.**
`InsertOneAsync` unikal indeksə qarşı; `DuplicateKey` xətası "kimsə məndən əvvəl
tutdu" deməkdir. Eyni anda gələn iki sorğu ikisi də "mən birinciyəm" nəticəsinə
gələ bilməz.

**5. Dörd nəticə:**
`Claimed` (davam et), `Replay` (saxlanılmış cavabı qaytar), `InProgress` (409 —
birinci sorğu hələ cavab verməyib, replay edəcək heç nə yoxdur), `Conflict`
(409 — eyni açar, fərqli məzmun).

**6. Sorğu gövdəsi saxlanılmır, yalnız SHA-256 hash-i.**
Gövdə ödəniş detalları, fayl adları və PII daşıyır (Bölmə 2.13). Hash "eyni
sorğu yenə" ilə "fərqli sorğu, açar təkrar istifadə olunub" arasını ayırmaq üçün
kifayətdir.

**7. Uğursuz sorğu açarı azad edir.**
Uğursuz sorğu məhz təkrar cəhd edilməli olandır; uğursuz cəhdin qeydi çağıranı
təkrar cəhddən məhrum etməməlidir. 2xx olmayan cavab və ya tutulmamış
exception → `ReleaseAsync`.

**8. Cavab MVC-nin öz `JsonSerializerOptions`-u ilə serializasiya olunur.**
Default variantla replay PascalCase qaytarırdı, birinci cavab isə camelCase idi
— testin tapdığı real baq.

**9. TTL indeksi, təmizləmə job-u yox.**
24 saat (plan Bölmə 5). Mongo TTL indeksi bunu özü edir, ona görə planda
sadalanan `IdempotencyCleanupJob` **lazım deyil** — bu, plandan sadə tərəfə
sapmadır və burada qeyd olunur.

**10. `provider_events`: `(Provider, ProviderEventId)` unikal.**
Provayderlər event-lərini müstəqil nömrələyir, ona görə cüt unikal olmalıdır.
`TryRecordAsync` `true`/`false` qaytarır — kod "yoxla, sonra yaz" etmir, qərarı
verilənlər bazası verir.

### Alternatives

- **Middleware, filter yerinə.** Middleware model binding-dən əvvəl işləyir, ona
  görə gövdəni özü oxumalı, buffer etməli və geri çevirməli olardı. Filter isə
  bind olunmuş arqumentləri görür — hash mənalı olur və açar operation üzrə
  scope-lanır.
- **Yalnız `Key` üzrə unikallıq.** Bir çağıranın açarı başqasının cavabını
  replay edə bilərdi.
- **Gövdəni tam saxlamaq (replay dəqiqliyi üçün).** PII və ödəniş detalları
  saxlanılardı; Bölmə 2.13 bunu qadağan edir.
- **`InProgress`-də gözləmək (polling).** Sorğu müddətini uzadır və deadlock
  riski yaradır; 409 ilə çağıranın özünə qərar vermək daha sadədir.
- **Redis-də idempotency açarları.** Redis həqiqət mənbəyi deyil
  (`ARCHITECTURE_RULES.md` §2); Redis boşalsa ikiqat ödəniş mümkün olardı.

### Consequences

Müsbət:
- Təkrar sorğu ikinci dəfə iş görmür; birinci cavab qaytarılır.
- Təkrar webhook ikinci dəfə iş görmür.
- Açarın təkrar istifadəsi səssizcə yanlış cavab vermir, 409 verir.
- Ödəniş, refund və wallet gələndə (Addım 4) mexanizm hazır olacaq — yalnız
  `[Idempotent]` əlavə etmək lazım gələcək.

Mənfi:
- Hər qorunan sorğu bir əlavə insert və (uğurda) bir update deməkdir.
- `InProgress` halında çağıran 409 alır və özü təkrar yoxlamalıdır.
- Hash arqumentlərin serializasiyasına əsaslanır; serializasiya oluna bilməyən
  arqument tipi (məs. `CancellationToken`) istisna edilməlidir — bu, testin
  tapdığı ikinci baq idi.

### Security Impact

- `ActorKey` scope-u bir istifadəçinin başqasının cavabını oxumasının qarşısını
  alır — bu, əsas təhlükəsizlik xüsusiyyətidir, test ilə qorunur.
- Nə sorğu gövdəsi, nə provayder payload-u saxlanılmır — yalnız hash.
- `SECURITY_THREAT_MODEL.md` → "Replay" nəzarətləri: idempotency + unikal
  provider event id-ləri. Hər ikisi indi mövcuddur.
- Açar 128 simvolla məhdudlaşdırılır (log və indeks şişməsinə qarşı).

### Operational Impact

- İki yeni kolleksiya; `idempotency_keys` TTL ilə özünü təmizləyir,
  `provider_events` isə **böyüməyə davam edir** — saxlama müddəti sonrakı
  addımda təyin olunmalıdır (`state_transitions` ilə birlikdə, **məlum boşluq**).
- Mobil və kiosk klientləri hələ `Idempotency-Key` göndərmir; backend tərəfi
  hazırdır, klient tərəfi ödəniş axını ilə (Addım 4) gələcək.

### Migration

Yeni kolleksiyalar, mövcud data dəyişmir.

### Related Plan Step

Addım 1 — "Kross-kəsən infrastruktur (ilk gündən)".

### Date

2026-09-24
