# NeoPrint --- Domain Rules

## Money

Money üçün raw decimal istifadəsini mümkün qədər məhdudlaşdır.

Money value object: - Amount - Currency

Currency hazırda plan üzrə AZN-dir.

Rounding qaydası mərkəzləşdirilmiş olmalıdır.

## Pricing

Pricing nəticəsi sonradan dəyişməməlidir.

Print order yaradılarkən PriceBreakdown snapshot saxlanır.

Pricing engine yalnız yeni quote/order üçün işləyir.

## PrintJob

PrintJob fiziki çap əməliyyatının biznes representation-ıdır.

Printer-in texniki API statusu birbaşa PrintJob statusuna
çevrilməməlidir; mapping/application layer vasitəsilə idarə edilməlidir.

## Wallet

Wallet balance tək source kimi istifadə edilməməlidir.

Ledger transaction-lar audit edilə bilən financial history-dir.

Balance cache/materialized value ola bilər, lakin ledger ilə consistency
qorunmalıdır.

## Kiosk

Kiosk biznes entity, KioskDevice isə konkret device identity-dir.

Bir kiosk-un device dəyişməsi ilə kiosk biznes identity-si
itirilməməlidir.

Revoked device yenidən etibarlı device kimi qəbul edilməməlidir.

## File

File metadata ilə binary storage ayrıdır.

StorageKey daxili texniki məlumatdır və UI-yə birbaşa expose
edilməməlidir.

## Audit

Sensitive action-lar audit olunmalıdır:

-   admin adjust
-   refund
-   user block
-   device revoke
-   pricing change
-   feature flag change
-   supply adjustment
-   manual job cancellation

Audit immutable olmalıdır.

## Notifications

Notification delivery business transaction-dan ayrılmalıdır.

Email/FCM failure əsas business transaction-ı avtomatik rollback
etməməlidir, əgər plan başqa cür tələb etmirsə.

## Background jobs

Background job: - retry-safe - observable - idempotent -
cancellation-aware

olmalıdır.
