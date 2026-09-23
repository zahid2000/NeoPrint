# NeoPrint --- Failure & Recovery

NeoPrint printer və kiosk səviyyəsində hardware failure-ları normal
sistem vəziyyəti kimi qəbul edir.

## Əsas failure ssenariləri

-   internet itir
-   backend unavailable
-   Redis unavailable
-   RabbitMQ unavailable
-   printer offline
-   printer paper out
-   printer jam/error
-   kiosk reboot
-   electricity loss
-   application crash
-   payment timeout
-   provider timeout
-   fiscal provider unavailable
-   converter crash
-   storage unavailable

## Source of truth

Recovery MongoDB-dəki biznes state-dən başlamalıdır.

Redis/RabbitMQ müvəqqəti itərsə biznes state itmiş hesab olunmamalıdır.

## Power loss

Kiosk yenidən qalxdıqda:

1.  device identity bərpa olunur;
2.  local encrypted journal/spool oxunur;
3.  backend ilə state reconciliation edilir;
4.  `Printing` job üçün printer execution evidence yoxlanılır;
5.  eyni job ikinci dəfə çap edilməməlidir;
6.  təhlükəsiz olduğu halda `Queued` job davam edir.

## Print execution

PrintJob ilə real printer əməliyyatı arasında ayrıca execution/attempt
məlumatı saxlanmalıdır.

Tövsiyə:

-   JobId
-   AttemptNo
-   StartedAt
-   FinishedAt
-   PrinterName
-   PrinterCounterBefore
-   PrinterCounterAfter
-   ReportedSheets
-   ReportedSides
-   Status
-   ErrorCode

## Recovery rule

Sistem "çap olubmu?" sualını yalnız application memory/state ilə
cavablandırmamalıdır.

Mümkün printer counter/status/evidence istifadə edilməlidir.

## Converter failure

Converter: - timeout ilə dayandırılmalıdır; - resource limit tətbiq
edilməlidir; - temp files təmizlənməlidir; - failed conversion
retry-safe olmalıdır; - istifadəçiyə təhlükəsiz error göstərilməlidir.

## Recovery test

Ən azı bunlar avtomatlaşdırılmış test/simulyasiya kimi yoxlanmalıdır:

-   payment-dən sonra process crash
-   Printing zamanı reboot
-   Redis loss
-   RabbitMQ temporary loss
-   printer offline
-   duplicate payment callback
-   duplicate refund event
-   failed update + rollback
