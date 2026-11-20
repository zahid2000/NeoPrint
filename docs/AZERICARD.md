# Azericard — developer portalının analizi (T-452)

Tarix: 2026-11-20. Səbəb: Azericard 2026-11-20-də fintexlər üçün **Developer Portal** və **sandbox** açdı
([fins.az xəbəri](https://fins.az/az/poster/azerikard-fintekler-yeni-imkanlar-teqdim-edib)). Portal:
[developer.azericard.com](https://developer.azericard.com/en) — bir uzun səhifədir (bölmələr anchor-larla).
**Bu sənəd araşdırmadır, kod deyil**; real endpoint ilə hələ yoxlanılmayıb. Tapılmayan məlumat **"tapılmadı"** kimi
yazılıb. Əvvəlki ümumi müqayisə: `docs/PROVIDERS.md` (T-302).

> Repoya **yazılmayanlar:** portaldakı test private/public açarlarının PEM blokları (yalnız portalda, test üçün)
> və əlaqə e-poçtu. Prod açarımız yalnız OpenBao-da saxlanılır (CLAUDE.md, təhlükəsizlik).

## 1. Xəbər nə deyir

- Azericard (prosessinq şirkəti) özünəxidmət ("self-service") modelində **Developer Portal** istifadəyə verib:
  API sənədləri, inteqrasiya bələdçiləri, test məlumatları, autentifikasiya mexanizmləri, **sandbox** (test
  əməliyyatları).
- Əhatə: e-kommersiya, Server-to-Server (S2S), təkrarlanan ödənişlər, Apple Pay və Google Pay.
- Qeydiyyat, qiymət, giriş şərtləri xəbərdə **yoxdur**. QR / A2A / open banking xatırlanmır.

## 2. Portalın bölmələri

Overview və transaction flow, E-Commerce (təsdiq / refund, `P_SIGN`, S2S), Installment, **Test data**, **Key generation**,
Card storage, Recurring, **Transaction Status**, Google Pay, Apple / Google Pay direct, Money transfer, Click to Pay,
**Sandbox**, Submission of Test Results.

- Sandbox üçün qeydiyyat / login **təsvir olunmayıb**; test terminalları və test açarları portalda açıq verilib.
- Prod məlumatı (terminal, Azericard-ın bizə məxsus public açarı) **bank / Azericard ilə müqavilədən** və test
  nəticələrinin təsdiqindən sonra verilir.

## 3. Bizim axına uyğunluq

Bizim axın: kiosk ekranında QR → müştəri telefonda ödəyir → callback / status → çap → lazım olsa refund
(`docs/PAYMENT_FLOWS.md`, `IBankQrProvider` — `backend/src/NeoPrint.Application/Payments/PaymentAbstractions.cs`).

| Bizim addım | Azericard-da | Qeyd |
|---|---|---|
| `CreateQrAsync` | QR-da **bizim** səhifənin linki (məs. `/pay/{paymentId}`); telefon onu açanda backend avtomatik göndərilən HTML forma ilə gateway-ə `POST` edir (`TRTYPE=1`) → Azericard-ın kart səhifəsi (3-D Secure) | Azericard-da **QR / payment link məhsulu tapılmadı** — linki biz yaradırıq. Kart məlumatı bizə gəlmir → hosted səhifə, **PCI DSS tələb olunmur** (portal PCI-ni yalnız S2S və direct üçün deyir) |
| `ReadCallback` | Nəticə `BACKREF` ünvanına `POST`: `TERMINAL, TRTYPE, ORDER, AMOUNT, CURRENCY, ACTION, RC, APPROVAL, RRN, INT_REF, TIMESTAMP, NONCE, P_SIGN` | İmza **başlıqda yox, gövdədə** (`P_SIGN`) — adapter `SignatureHeader` əvəzinə gövdədən oxumalıdır. `BACKREF` brauzerin geri qayıdışı ola bilər (müştəri səhifəni bağlasa gəlməyə bilər) → **həqiqət mənbəyi status sorğusudur** |
| `GetStatusAsync` | `TRTYPE=90` (`TRAN_TRTYPE, ORDER, TERMINAL, TIMESTAMP, NONCE, P_SIGN`); cavab HTML / XML / JSON | **Yalnız 24 saat ərzində** — reconciliation job-u (`ListToReconcileAsync`) bu pəncərəyə sığmalıdır |
| `CancelAsync` (QR vaxtı bitdi) | Ayrıca "ödəniş sessiyasını ləğv et" tapılmadı | Ödənilməyibsə heç nə etmirik; gec gələn uğurlu ödəniş → reversal (aşağı) |
| `RefundAsync` | `TRTYPE=22` (onlayn reversal) və ya `24` (oflayn); `AMOUNT, CURRENCY, ORDER, RRN, INT_REF, TERMINAL, TIMESTAMP, NONCE, P_SIGN` | Callback-dən `RRN` və `INT_REF` **mütləq saxlanmalıdır**. Qismən refund və müddət **tapılmadı** |
| Alternativ: pre-auth | `TRTYPE=0` (bloklama) → çap uğurlu olanda `TRTYPE=21` (checkout); çap alınmasa `22` (bloklamanı açmaq) | Çap xidməti üçün **daha uyğun ola bilər**: pul yalnız çapdan sonra tutulur, refund azalır. Azericard-dan soruşulmalı (bölmə 8) |

## 4. Texniki faktlar

**Endpoint-lər (test):**

| Nə | URL | Metod |
|---|---|---|
| Gateway (TRTYPE 0/1, 21/22/24, 90) | `https://testmpi.3dsecure.az/cgi-bin/cgi_link` | `POST`, `application/x-www-form-urlencoded` |
| Token / recurring | `https://testmpi.3dsecure.az/token/cgi_link` | `POST` |
| Sandbox nümunələri (imza, auth, reversal, status) | `https://testsite.3dsecure.az/sandbox/...` | — |
| **Prod gateway URL** | tapılmadı (müqavilə ilə verilir) | — |

**Avtorizasiya sorğusunun sahələri:** `AMOUNT` (1–12, nöqtəli, "1.00"), `CURRENCY` (`AZN`), `ORDER` (rəqəm, 6–32;
status bölməsində 6–20 — **20-dən uzun etməyək**; son 6 rəqəm terminal üzrə **gün ərzində unikal**), `DESC` (≤ 50),
`MERCH_NAME` (≤ 50), `MERCH_URL` (≤ 250), `TERMINAL` (8), `TRTYPE`, `COUNTRY`, `MERCH_GMT` (`+4`), `BACKREF` (≤ 250),
`TIMESTAMP` (GMT, `YYYYMMDDHHMMSS`, **±1 saat**), `NONCE` (8–32 təsadüfi bayt, hex), `LANG`, `EMAIL` (istəyə görə),
`M_INFO` (Base64 JSON — brauzer ekranı, saat qurşağı), `P_SIGN`.

**TRTYPE:** `0` pre-auth · `1` avtorizasiya · `21` checkout (satışı tamamlamaq) · `22` onlayn reversal · `24` oflayn
reversal · `90` status.

**İmza (`P_SIGN`):** RSA-SHA256 (açar ≥ 2048 bit), nəticə hex. MAC mənbəyi: hər sahənin dəyərindən əvvəl onun **onluq
uzunluğu**, ardıcıl birləşdirilir; boş sahə `-` (uzunluğu sayılmır).

| Mesaj | Sahələrin sırası |
|---|---|
| TRTYPE 0 / 1 | `AMOUNT, CURRENCY, TERMINAL, TRTYPE, TIMESTAMP, NONCE, MERCH_URL` |
| TRTYPE 21 / 22 / 24 | `AMOUNT, CURRENCY, TERMINAL, TRTYPE, ORDER, RRN, INT_REF` |
| TRTYPE 90 | `ORDER, TERMINAL, TRTYPE, TIMESTAMP, NONCE` |
| Callback (yoxlama) | `AMOUNT, TERMINAL, APPROVAL, RRN, INT_REF` — Azericard-ın **public** açarı ilə |

Sorğunu **bizim private açar** imzalayır; Azericard-a public açarımızı göndəririk, o da bizə öz public açarını verir
(prodda hər merchant üçün ayrıca). Açar cütü OpenSSL ilə yaradılır. **.NET-in daxili `RSA` sinfi kifayətdir — yeni paket
lazım deyil.**

**Callback kodları:** `ACTION` — `0` uğurlu, `1` dublikat, `2` rədd edildi, `3` emal xətası (3DS olmayanda əlavə `6`, `7`,
`8` — təkrarlar). `RC` — ISO-8583 cavab kodu (`00` uğur); **tam cədvəl tapılmadı**. Valyuta cavabda rəqəmlə: **`944`**
(AZN). Təkrar göndərmə (retry) siyasəti **tapılmadı**.

**Test məlumatı** (portalın "Test data" bölməsi; hamısı ictimai test məlumatıdır):

| Kart | Bitmə | CVV | 3DS SMS kodu |
|---|---|---|---|
| Visa `4127208104942601` | 05/31 | 796 | 1111 |
| Mastercard `5167513332880780` | 05/31 | 609 | 1111 |
| Visa "Tam Card" (ABB merchant-ları) `4127214121630724` | 04/31 | 536 | 1111 |

Test terminalları: `17205356`; `17205357` (ABB Tam Card); `17205358` (Direct Post); `17205359` (Apple Pay və Google Pay).
Sandbox curl nümunələrində `77777777` də var (siyahıda yoxdur).

**Prod-a keçid:** bank / Azericard ilə müqavilə → public açarımızı göndəririk → testdə uğurlu `TRTYPE=1` və `22`
əməliyyatlarının `RRN`, tarix / saatını e-poçtla təqdim edirik → yoxlamadan sonra prod terminal və açar verilir.
IP whitelist / domen tələbi **tapılmadı**.

**SDK / nümunə:** C# / .NET SDK **yoxdur**, Postman kolleksiyası yoxdur — yalnız curl nümunələri. Rate limit, ödəniş
səhifəsinin sessiya müddəti **tapılmadı**.

**Bizə hələ lazım olmayanlar:** recurring / kart saxlama (`TOKEN`, MIT), Apple Pay / Google Pay (ayrıca terminal,
`gatewayMerchantId` Azericard verir), Click to Pay, Installment, Money transfer. Mobil tətbiqdə Apple / Google Pay
sonradan maraqlı ola bilər — ayrıca qərar.

## 5. Bizim tərəfdə nəyə diqqət etməli (adapter yazılanda — M25)

1. **ORDER** — rəqəm, ≤ 20, son 6 rəqəm gün ərzində unikal: `paymentId` (Guid) ilə `ORDER` arasında saxlanılan xəritə
   (`Payment.ProviderRef`), təkrar cəhddə **eyni** `ORDER` (idempotency, double charge yox).
2. **İmza** — gələn hər callback və status cavabında `P_SIGN` yoxlanır; imzasız / səhv imzalı → rədd və loq (açar,
   kart məlumatı loqa yazılmır). `provider_events` unikal indeksi: `TERMINAL + ORDER + TRTYPE + RRN`.
3. **Status 24 saat** — callback gəlməyən ödəniş reconciliation-da 24 saat bitməmiş `TRTYPE=90` ilə yoxlanır.
4. **Saat** — `TIMESTAMP` GMT və ±1 saat: server saatı NTP ilə; kiosk saatından istifadə edilmir.
5. **Məbləğ** — `Money` → `"0.00"` formatı, mərkəzi yuvarlaqlaşdırma; cavabdakı `AMOUNT` gözlənilənlə müqayisə olunur.
6. **Refund** — `RRN` və `INT_REF` ödənişlə birlikdə saxlanılır; refund idempotent (bir `refundKey` — bir reversal).
7. **Açarlar** — private açar OpenBao-da; test açarları da repoya yazılmır (lokal `.env` / OpenBao dev).

## 6. Azericard-dan soruşulmalı olanlar

1. Hosted səhifədə `TRTYPE=1`-dən sonra `TRTYPE=21` (checkout) **məcburidirmi**? Yoxsa `0 → 21` (pre-auth) bizə tövsiyə olunur?
2. `BACKREF` — server-to-server bildirişdir, yoxsa yalnız brauzerin geri qayıdışı? Ayrıca **server notification URL** varmı?
   Təkrar göndərilirmi, content type nədir?
3. `ORDER` uzunluğu: 6–32 yoxsa 6–20?
4. **Qismən refund** varmı; refund / reversal neçə gün ərzində mümkündür; `22` ilə `24` fərqi.
5. `RC` kodlarının tam cədvəli.
6. Ödəniş səhifəsinin sessiya müddəti (bizdə QR `Payment.TimeoutMinutes`).
7. Dinamik QR / payment link məhsulu varmı (kiosk ekranı üçün)?
8. Prod gateway URL, IP whitelist, callback üçün domen / HTTPS tələbləri.
9. Komissiya və müqavilə: hansı bank vasitəsilə, MCC, hesablaşma müddəti.

## 7. Nəticə

- Azericard sənədi bizim `IBankQrProvider` axınına **uyğundur**: QR → bizim link → hosted kart səhifəsi → imzalı
  nəticə → `TRTYPE=90` status → `TRTYPE=22` reversal. Yeni kitabxana lazım deyil, PCI DSS (hosted səhifədə) lazım deyil.
- **Sandbox-a dərhal başlamaq olar** — test terminalları, kartlar və açarlar portalda açıqdır; prod üçün bank müqaviləsi lazımdır.
- Adapterin yazılması (M25) layihə sahibinin göstərişi ilə başlayır; əvvəl bölmə 6-dakı 1–3-cü suallar cavablansa,
  arxitektura daha dəqiq olar.
