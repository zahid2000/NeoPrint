# NeoPrint --- Observability

Observability must make a single print transaction traceable from API to
physical printer and refund/fiscal outcome.

## Correlation

Propagate:

`CorrelationId / TraceId`

through:

API → Application → RabbitMQ → Worker → Kiosk → PrintJob → Printer →
Payment/Refund/Fiscal/Receipt

Where supported, use OpenTelemetry-compatible tracing.

## Structured logs

Minimum useful fields:

-   Timestamp
-   Level
-   Service
-   Environment
-   CorrelationId
-   TraceId
-   UserId/ActorId where appropriate
-   KioskId
-   DeviceId
-   JobId
-   PaymentId
-   RefundId
-   Operation
-   Status
-   DurationMs
-   ErrorCode

## Log səviyyələri (API)

| Nə baş verib | Səviyyə | Harada |
| --- | --- | --- |
| Sorğu uğurla bitib, yoxlama (validation) rədd edib, sorğu ləğv olunub | `Information` | `LoggingBehavior`, `ProblemDetailsExceptionHandler` |
| Qayda rədd edib: `DomainException` (409), `NotFoundException` (404), `AuthenticationException` (401) — məs. ləğv olunmuş cihazın qeydiyyatı, artıq verilmiş çekin təkrarı | `Warning`, mesaj açarı ilə (`ErrorCode`), stack trace-siz | `LoggingBehavior` |
| Gözlənilməz xəta (5xx) | `Error`, exception ilə | `LoggingBehavior`, `ProblemDetailsExceptionHandler` |

Alert yalnız `Error`-a qurulur, ona görə qayda rəddi `Error` yazılmır. ASP.NET Core-un
`ExceptionHandlerMiddleware`-i hər exception-ı handler-dən əvvəl `Error` kimi yazır.
Bu kateqoriya `Program.cs`-də söndürülüb, çünki səviyyəni handler seçir (T-132,
`RefusalLoggingTests`).

## Do not log

-   passwords
-   access/refresh tokens
-   private keys
-   encryption keys
-   complete documents
-   sensitive payment data
-   unnecessary personal information

## Metrics

**Hazırda (T-215, T-282):** backend sayğacları .NET-in öz `System.Diagnostics.Metrics` API-si ilə
`NeoPrint` meter-indədir (paket yoxdur; OpenTelemetry ixracatçısı təsdiqlənəndə eyni adlarla çıxacaq):
`neoprint.analytics.events_rejected` (`reason`), `neoprint.files.cleanup.deleted`,
`neoprint.files.cleanup.deleted_bytes`, `neoprint.messaging.dead_letter_messages` (`queue`),
`neoprint.reconciliation.findings` (`check`, `kind`). Bu gün oxumaq:
`dotnet-counters monitor -n NeoPrint.Api --counters NeoPrint`. Eyni rəqəmlər admin panelinin
"Arxa plan işləri" səhifəsindədir (`job_runs`, `analytics_rejections`).

Recommended business/technical metrics:

### Kiosk

-   online/offline
-   heartbeat age
-   app version
-   printer status
-   paper stock
-   active jobs

### Printing

-   jobs created
-   jobs completed
-   jobs interrupted
-   average print duration
-   printer errors
-   refund rate
-   duplicate/retry count

### Payment

-   payment success/failure
-   provider latency
-   timeout count
-   callback duplicate count
-   refund success/failure

### Storage

-   upload failures
-   conversion failures
-   storage latency
-   cleanup failures

### Infrastructure

-   CPU
-   memory
-   disk
-   RabbitMQ backlog
-   Redis availability
-   Mongo latency

## Alerts

Alerts should focus on actionable failures:

-   kiosk offline too long
-   printer offline
-   paper low
-   repeated print failures
-   payment provider failure spike
-   refund backlog
-   fiscalization backlog
-   storage failure
-   update failure
-   abnormal resource consumption

## Incident correlation

An incident should be traceable to affected: - kiosk - device - print
jobs - payments - refunds - provider events

without exposing user file content.
