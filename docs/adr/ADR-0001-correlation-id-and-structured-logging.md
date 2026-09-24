# ADR-0001 — CorrelationId və structured JSON logging

### Status

Accepted

### Context

`docs/OBSERVABILITY.md` bir çap əməliyyatının API-dən fiziki printerə, oradan
refund və fiskal nəticəyə qədər izlənə bilməsini tələb edir. Plan Addım 1-də
bunu "ilk gündən" işi kimi sadalayır.

Mövcud vəziyyət: CorrelationId heç yerdə yox idi, log-lar .NET-in default mətn
formatında idi və `LoggingBehavior` yalnız request adı ilə müddəti yazırdı —
`OBSERVABILITY.md`-dəki 14 sahədən heç biri.

Zəncir bir prosesdən ibarət deyil: mobil tətbiq və kiosk ayrı proseslərdir,
sonra RabbitMQ consumer-ləri və Hangfire job-ları gələcək. Ona görə id-nin
proses sərhədini keçməsi lazımdır.

### Decision

1. **Nəqliyyat:** `X-Correlation-Id` HTTP header-i. Gələn dəyər qəbul edilir,
   yoxdursa yaradılır, hər iki halda cavab header-ində qaytarılır.
2. **Format:** 32 onaltılıq simvol (`Guid("n")`). Mobil, kiosk və backend eyni
   formatı yaradır, ona görə log-larda fərq görünmür.
3. **Saxlanma:** `ICorrelationContext` (Application abstraction) + scoped
   `CorrelationContext` (Infrastructure). Bir HTTP sorğusu, bir consumer
   mesajı və bir job icrası — hərəsi öz id-si ilə.
4. **Log-a çatdırılma:** middleware `ILogger.BeginScope` ilə id-ni scope-a
   qoyur. .NET-də scope provider bütün logger-lər arasında paylaşıldığı üçün
   sorğunun yaratdığı hər sətir id-ni daşıyır — hər sinifə `ICorrelationContext`
   inject etməyə ehtiyac yoxdur.
5. **Format:** Production-da `AddJsonConsole` (`IncludeScopes = true`),
   Development-də `AddSimpleConsole` (işləyərkən oxunaqlıdır). Əlavə paket
   yoxdur — .NET-in daxili provayderləri kifayətdir.
6. **Sanitizasiya:** gələn dəyər caller-in nəzarətindədir və həm log sətrinə,
   həm cavab header-inə düşür. Yalnız `[A-Za-z0-9._-]` saxlanılır, 128 simvola
   kəsilir; heç nə qalmasa yeni id yaradılır.
7. **Audit:** `audit_logs.CorrelationId` + `ActorType`. Id-ni `MongoAuditWriter`
   ambient kontekstdən özü götürür, ona görə heç bir çağırış yeri ötürməyi
   unuda bilməz.
8. **Client-lər:** kioskda `CorrelationIdHandler` (DelegatingHandler), mobildə
   Dio `onRequest`. Hər ikisi cəhd başına bir id yaradır, mövcud dəyəri əvəz
   etmir.

### Alternatives

- **W3C `traceparent` / OpenTelemetry indi.** Plan tam OTel yığımını Addım 8-ə
  saxlayır; indi gətirmək "gələcək addımı cari addıma gətirmə" qaydasını
  pozardı. `X-Correlation-Id` sonra `traceparent` ilə yanaşı yaşaya bilər.
- **Serilog və ya başqa structured logging paketi.** Bölmə 2.0 qaydası 1:
  əvvəlcə platformanın daxili imkanları. `AddJsonConsole` tələb olunan sahələri
  verir; Serilog həm də banned paketlər siyahısındadır.
- **Id-ni hər metod imzasına parametr kimi ötürmək.** Hər çağırış yerində
  unudula bilər və mövcud imzaların hamısını dəyişməyi tələb edərdi.
- **`AsyncLocal` ilə statik ambient kontekst.** DI scope-u eyni işi daha açıq
  şəkildə görür və testdə asanlıqla əvəz olunur.

### Consequences

Müsbət:
- Bir sorğunun yaratdığı bütün log sətirləri bir id ilə tapılır.
- Uğursuz sorğunun cavabında `correlationId` olur — istifadəçi "xəta verdi"
  deyəndə dəqiq sorğu tapılır.
- Audit qeydi onu yaradan sorğu ilə bağlıdır.
- Əlavə asılılıq yoxdur.

Mənfi:
- Production log-ları JSON-dur; birbaşa `docker logs` ilə oxumaq daha çətindir
  (Development-də mətn formatı qalır).
- `IExceptionHandler` singleton olduğu üçün scoped id-ni konstruktorda ala
  bilmir; `HttpContext.RequestServices`-dən oxunur. Bu, DI lifetime
  validasiyası ilə aşkarlandı və testlə qorunur.

### Security Impact

- Gələn id sanitizasiya olunmasaydı, newline ilə saxta log sətri yazmaq və ya
  cavab header-ini bölmək mümkün olardı. Testlə qorunur.
- Id secret deyil, heç nəyə icazə vermir — ona görə təsadüfi mənbə kifayətdir.
- Log-a əlavə olunan sahələr yalnız identifikatorlardır (UserId, KioskId,
  DeviceId). Request məzmunu, token və PII log-a düşmür.

### Operational Impact

- Production-da log toplayıcı JSON gözləməlidir.
- `ix_audit_logs_correlation` indeksi "bu sorğu daha nə etdi?" sualını cavablayır.
- Kiosk və mobil öz id-lərini göndərdiyi üçün incident zamanı istifadəçinin
  ekranındakı id ilə server log-u birləşdirilə bilər.

### Migration

Mövcud `audit_logs` sənədlərində `CorrelationId` yoxdur; C# tərəfdə sahə
nullable-dır, ona görə köhnə qeydlər oxunmağa davam edir. Data migration
lazım deyil.

### Related Plan Step

Addım 1 — "Kross-kəsən infrastruktur (ilk gündən)".

### Date

2026-09-24
