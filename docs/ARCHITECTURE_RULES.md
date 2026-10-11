# NeoPrint --- Architecture Rules

> Bu sənəd NeoPrint-PLAN.md ilə birlikdə əsas texniki qaydadır. Claude
> Code bu qaydaları dəyişdirməməli və gələcək fazaların işlərini cari
> fazaya səbəbsiz gətirməməlidir.

## 1. Əsas arxitektura prinsipləri

-   Backend ilkin olaraq modular monolith kimi qurulur.
-   Domain layer xarici texnologiyalardan asılı olmamalıdır.
-   Application layer use-case orchestration edir; business rule
    Domain-də qalır.
-   Infrastructure MongoDB, Redis, RabbitMQ, storage, email və provider
    implementasiyalarını saxlayır.
-   API/Host layer yalnız transport, authentication/authorization və
    composition işlərini görür.
-   Controller-lərdə business logic yazılmamalıdır.
-   Backend layihələri: `Api`, `Application`, `Domain`, `Infrastructure`,
    `Workers`, `Contracts` və `Converter`. `NeoPrint.Converter` ayrıca
    icra olunan alətdir (istifadəçi faylını açan yeganə proses); API onu
    image-də daşıyır, amma heç bir tipinə istinad etmir (ADR-0005).
-   External provider-lər həmişə interface arxasında olmalıdır.
-   UI birbaşa MongoDB, storage və ya provider-lərə çıxmamalıdır.
-   Bir modul digər modulun DB kolleksiyasını birbaşa dəyişməməlidir.
-   State dəyişiklikləri explicit command/use-case vasitəsilə
    edilməlidir.

## 2. Source of truth

-   MongoDB = biznes məlumatlarının əsas mənbəyidir.
-   Redis = cache, disposable queue/projection və distributed
    coordination üçündür.
-   RabbitMQ = asynchronous delivery üçündür, source of truth deyil.
-   Hangfire = retry/scheduled/background execution üçündür, biznes
    state-in özü deyil.
-   Redis itərsə sistem MongoDB-dən lazımi queue/projection state-i
    bərpa edə bilməlidir.

## 3. Financial operations

Payment, refund, wallet, fiscalization və digər maliyyə əməliyyatları
idempotent olmalıdır.

Eyni request/event ikinci dəfə gəldikdə: - ikinci ödəniş
yaranmamalıdır; - ikinci refund edilməməlidir; - wallet iki dəfə
dəyişməməlidir; - fiscal əməliyyat duplicate olmamalıdır.

## 4. Kiosk təhlükəsizlik modeli

Kiosk untrusted edge device kimi qəbul edilir.

-   Secret backend-dən kiosk-a göndərilmir.
-   Device private key cihazdan çıxmamalıdır.
-   Backend yalnız public key saxlayır.
-   Kiosk lokal spool encrypted saxlanır.
-   User faylları untrusted input sayılır.
-   Converter-lər təhlükəsiz olmayan fayllarla işləyir və sandbox/limit
    tətbiq etməlidir.
-   İstifadəçi faylını parse edən kitabxanalar (PDFium, SkiaSharp,
    PDFsharp, LibreOffice, Ghostscript) yalnız şəbəkəsiz `converter`
    konteynerində, hər fayl üçün ayrıca prosesdə işləyir. `Api`,
    `Application` və `Infrastructure` bu kitabxanalara istinad etmir —
    `ParserIsolationTests` bunu yoxlayır (ADR-0005).
-   Converter-in gücü sərhədlidir: eyni anda işləyən proseslərin sayı və
    onların arxasındakı növbə sabitdir. Yer olmayanda sorğu limitsiz
    gözlədilmir — `503` + `Retry-After` alır.
-   Açılmış fayl məzmunu (məs. səhifə şəkli) yalnız proses yaddaşında və
    qısa müddət keşlənə bilər; Redis-ə və ya diskə şifrəsiz yazılmır.
-   Log-larda token, secret, file content və lazımsız PII olmamalıdır.

## 5. Error/retry

Background operation retry-safe olmalıdır.

Retry zamanı: - duplicate business action yaranmamalıdır; - state
transition yenidən tətbiq ediləndə təhlükəsiz olmalıdır; - external
provider timeout ilə business failure qarışdırılmamalıdır.

## 6. Observability

Əsas əməliyyatlar CorrelationId/TraceId daşımalıdır:

`API → Payment → RabbitMQ → PrintJob → Kiosk → Printer → Refund → Fiscal/Receipt`

Log-larda ən azı: - CorrelationId - ActorId/ActorType -
DeviceId/KioskId - Entity/Job Id - operation - duration - result/status

olmalıdır.

## 7. Claude Code qaydası

-   Mövcud NeoPrint-PLAN.md-dəki qərarları özbaşına dəyişmə.
-   Gələcək addımları cari addıma gətirmə.
-   Refactor yalnız konkret task üçün lazım olduqda et.
-   Testlər keçmədən növbəti substep-ə keçmə.
-   Package əlavə etməzdən əvvəl mövcud dependency siyahısını yoxla.
-   Secret və real production credential commit etmə.
-   Hər dəyişiklikdən sonra build + test + git diff yoxla.
