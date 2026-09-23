# NeoPrint --- Idempotency Rules

Idempotency Addım 7-yə saxlanılmır. Sistem daxilində ilk real business
operation-lardan etibarən tətbiq edilməlidir.

## Əhatə

Minimum: - payment create - payment callback/webhook - refund - print
job creation - print job cancellation - wallet transaction -
fiscalization - voucher redemption - referral reward -
provisioning/activation - background consumers

## Idempotency Key

Request-based operation üçün:

`Idempotency-Key`

saxlanmalıdır.

Record ən azı:

-   Key
-   UserId/ActorId
-   Endpoint/Operation
-   RequestHash
-   Status
-   Response/status məlumatı
-   CreatedAt
-   ExpiresAt

Eyni key + fərqli request body gəlirsə request conflict sayılmalıdır.

## Provider event

External provider callback üçün provider-in event/transaction ID-si
unique constraint ilə qorunmalıdır.

Məsələn:

`ProviderName + ProviderEventId`

unique olmalıdır.

## Consumer

RabbitMQ message duplicate delivery edə bilər.

Consumer: 1. message identifier-i yoxlayır; 2. artıq işlənibsə side
effect yaratmır; 3. DB state-i yoxlayır; 4. əməliyyatı atomik edir; 5.
yalnız bundan sonra success hesab edir.

## Financial invariant

Heç bir retry aşağıdakı vəziyyətləri yarada bilməz:

-   double charge
-   double refund
-   double wallet credit
-   double fiscal receipt
-   double referral reward

## Concurrency

Optimistic concurrency/version istifadə olunan entity-lərdə version
yoxlanmalıdır.

Race condition zamanı operation təhlükəsiz şəkildə retry edilə
bilməlidir.
