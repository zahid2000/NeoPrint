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

## Do not log

-   passwords
-   access/refresh tokens
-   private keys
-   encryption keys
-   complete documents
-   sensitive payment data
-   unnecessary personal information

## Metrics

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
