# NeoPrint --- Payment Flows

## Payment flow

1.  User print options seçir.
2.  Backend qiyməti hesablayır.
3.  PriceBreakdown snapshot yaradılır.
4.  PaymentIntent yaradılır.
5.  Provider-ə payment request göndərilir.
6.  Callback/polling ilə nəticə alınır.
7.  Payment idempotent şəkildə Paid edilir.
8.  PrintJob queue-ya düşür.
9.  Çap tamamlandıqda Completed olur.
10. Receipt/fiscalization işləyir.

## Price snapshot

Payment sonradan yenidən pricing engine ilə hesablanmamalıdır.

Payment/PrintJob özündə hesablanmış snapshot saxlamalıdır.

Snapshot-a daxil ola bilər:

-   pages
-   sides
-   sheets
-   copies
-   color
-   paper
-   basePrice
-   volumeDiscount
-   promoDiscount
-   bonus
-   minimumApplied
-   finalPrice
-   currency

## Payment timeout

Timeout olduqda: - payment Pending-dən Expired vəziyyətinə keçir; -
provider callback sonradan gəlsə duplicate payment yaranmamalıdır; -
provider-in real statusu ilə local status reconcile edilməlidir.

## Webhook

Webhook: - authentication/signature yoxlamalıdır; - ProviderEventId
unique olmalıdır; - duplicate event təhlükəsiz ignore edilməlidir; -
callback processing idempotent olmalıdır.

## Kiosk error

Əgər payment uğurlu olub, amma çap icra olunmayıbsa:

`Paid → RefundPending`

Refund yalnız çap nəticəsi və biznes qaydaları əsasında verilməlidir.

## Refund

Refund direction ayrıca modelləşdirilməlidir:

-   Card
-   Bank
-   Wallet
-   Voucher fallback

Refund üçün: - reason - source payment - requested amount - processed
amount - provider reference - status - timestamps

saxlanmalıdır.

## Wallet

Wallet prepaid service credit-dir.

Wallet əməliyyatları ledger prinsipi ilə saxlanmalıdır. Sadəcə
`Balance += amount` yanaşmasına güvənmə.

Ledger type nümunələri:

-   TopUp
-   Payment
-   Refund
-   Bonus
-   VoucherRedeem
-   AdminAdjust

Admin adjustment mütləq reason + audit tələb edir.

## Mock provider

Mock provider real provider kimi failure/timeout/duplicate callback
ssenarilərini simulyasiya edə bilməlidir.
