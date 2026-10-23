# Bank və e-kassa provayderləri — araşdırma (T-302)

Tarix: 2026-10-24. Məqsəd: 7B-də real adapterlərə (M25 — bank, M26 — e-kassa) başlamazdan əvvəl kimlə və necə
inteqrasiya ediləcəyini aydınlaşdırmaq. **Bu sənəd araşdırmadır, kod deyil.** Mənbələr ictimai internetdəndir;
hər iddianın yanında mənbə var. Təsdiqlənməyən və ya tapılmayan məlumat **"bilinmir"** kimi yazılıb — uydurulmayıb.
Komissiyalar və müqavilə şərtləri ictimai deyil; onlar provayderdən sorğu ilə alınır (bölmə 5).

## 1. Bizim interfeyslər (nəyə uyğun gəlməlidir)

| İnterfeys | Nə edir | Fayl |
|---|---|---|
| `IBankQrProvider` | Kioskun ekranında QR yaradır (`CreateQrAsync`), statusu soruşur, ləğv edir, provayderin callback-ini **imza ilə** oxuyur (`ReadCallback(body, signature)`, `SignatureHeader`), refund (`IRefundingProvider`) | `backend/src/NeoPrint.Application/Payments/PaymentAbstractions.cs` |
| `ICardPaymentProvider` | Kart ödənişi (`ChargeAsync`), status, refund | eyni fayl |
| `IFiscalProvider` | Satış və qaytarma çeki (`IssueSaleAsync`, `IssueRefundAsync`), status, sağlamlıq yoxlaması; hər kioskun öz `TerminalId`-si | `backend/src/NeoPrint.Application/Fiscal/FiscalAbstractions.cs` |

## 2. Ödəniş — variantlar

| Variant | Nədir | API sənədi | Sandbox | Callback imzası | Refund | Komissiya | Qoşulma | Mənbə |
|---|---|---|---|---|---|---|---|---|
| **Azericard** (bankların kart prosessinqi, e-Gateway) | Kart ödənişi bank-ekvayer üzərindən; Visa / Mastercard / Amex / UnionPay / JCB | **Var**, ictimai developer saytı | **Var**: test kartları, test terminal ID-ləri, OTP `1111` | **Var**: `P_SIGN` — RSA (≥ 2048 bit) + SHA-256; callback MPI public açarla yoxlanılır | **Var**: `TRTYPE=22` (onlayn reversal), `24` (oflayn); status `TRTYPE=90` (24 saat ərzində) | bilinmir (bankla müqavilə) | Terminal / Merchant ID **bank verir**; S2S (direct) inteqrasiya üçün **PCI DSS** sertifikatı tələb olunur; prod-da öz açar cütü | [developer.azericard.com](https://developer.azericard.com/en) |
| **Payriff** (ödəniş xidməti) | Host olunan ödəniş səhifəsi (kart), invoice-lar, payout | Rəsmi sayt [docs.payriff.com](https://docs.payriff.com/) (məzmunu oxuna bilmədi); **rəsmi SDK-lar** GitHub-da: [.NET](https://github.com/PayRiff/payriff-dotnet), [PHP](https://github.com/PayRiff/payriff-php) | Var — merchant panelində sandbox açarları (üçüncü tərəf bələdçisi: [1](https://ecommercestoreguide.com/e-commerce-academy/setting-up-payriff-gateway/)) | **Yoxdur** — SDK README-sinə görə callback imzalanmır; sifarişin statusu ayrıca sorğu ilə təsdiqlənməlidir, callback təkrar gələ bilər | Var (SDK: sifariş, məbləğ, səbəb) | bilinmir | Müqavilə; AZN / USD / EUR (üçüncü tərəf) | [payriff-php](https://github.com/PayRiff/payriff-php), [layihə qeydləri](https://github.com/ismetcahangirov/masapay/pull/111) |
| **Epoint** (ödəniş xidməti) | Host olunan ödəniş səhifəsi (form post), split, pre-auth, payout, wallet | Rəsmi PDF (SDK README-də istinad; oxunmadı); bir çox **icma** SDK-sı | **Rəsmi sandbox bilinmir**; icmanın lokal sandbox-u var ([epoint-sandbox](https://github.com/onemax-az/epoint-sandbox)) | **Var**: `data` + `signature` (base64(SHA-1(private_key + data + private_key))) — icma SDK-larına görə | Var: `reverse` və `refund-request` (icma SDK-ları; refund-request saxlanılmış kart tələb edir) | bilinmir | Müqavilə; Kapital Bank tərəfdaş bank kimi göstərilir (köhnə səhifə) | [epoint.az](https://epoint.az/en), [epoint-python](https://github.com/martian56/epoint-python) |
| **Kapital Bank e-commerce** | Bankın öz kart ödəniş səhifəsi | **Rəsmi developer portalı tapılmadı** — yalnız icma kitabxanaları | bilinmir | bilinmir | pre-auth var (icma) | bilinmir | Bankla müqavilə | [icma modulu](https://github.com/arzuhuseyn/Kapital-Bank-Payment-Gateway) |
| **Ani Ödənişlər Sistemi (Mərkəzi Bank, IPS) — QR** | Bank tətbiqi ilə QR ödənişi; 2021-dən işləyir, ilk pilot e-kassa operatorları və bir supermarket şəbəkəsi ilə | Mərkəzi Bankın **"merchant-presented" QR spesifikasiyası** (ISO 18004 + EMV QR) var; merchant API-si bankdan asılıdır — **bilinmir** | bilinmir | bilinmir | bilinmir | Mərkəzi Banka görə kartdan **ucuz** (məbləğ bilinmir) | IPS-ə qoşulmuş bank vasitəsilə | [CBAR press-reliz](https://www.cbar.az/press-release-2885/ani-?language=en), [QR spesifikasiyası (PDF)](https://uploads.cbar.az/assets/63cc0920918b7858f85091b00.pdf), [Azernews](https://www.azernews.az/business/189694.html) |
| **Kioskda fiziki POS terminal** (kartı terminala toxundurmaq) | Bankın POS terminalı + onun SDK / protokolu | bilinmir | bilinmir | — | bilinmir | bilinmir | Bank | — |

**Ümumi mənzərə (bazar icmalı):** [payatlas.com — Azərbaycan](https://payatlas.com/countries/azerbaijan-az) (üçüncü tərəf; PSP-lər adətən sənəd və sandbox verir).

## 3. e-kassa (fiskal çek) — variantlar

| Nə | Faktlar | Mənbə |
|---|---|---|
| Hüquqi əsas | e-kassa — proqram-aparat kompleksi; əməliyyatlar **real vaxtda** Vergi Xidmətinin məlumat bazasına ötürülür; Nazirlər Kabinetinin 14.02.2019 tarixli 52 nömrəli qərarı | [taxes.gov.az — E-cash register (PDF)](https://www.taxes.gov.az/uploads/2024/eng/Bifold-eCASHREGISTER.pdf), [2019 tələblər (PDF)](https://tedris.taxes.gov.az/assets/upload/files/E-kassa%2009.01.2019%20-%20EN.pdf) |
| Nə göndərilir | Satış, nağd giriş / çıxış, **qaytarma**, ləğv, gündəlik **Z-hesabat** | eyni 2019 sənədi |
| Kim satır və qoşur | Yalnız Vergi Xidmətində **e-kassa operatoru** kimi qeydiyyatda olan rezident hüquqi şəxs; e-kassa modeli idarələrarası komissiyada texniki sınaqdan keçir | [e-kassa.gov.az — operator tələbləri](https://e-kassa.gov.az/az/operatarlar-ucun?catID=1), [operator qeydiyyatı](https://e-kassa.gov.az/az/operatarlar-ucun?catID=2) |
| Operatorların siyahısı | e-kassa.gov.az portalında: "Reyestrə salınmış e-kassaların siyahısı", "E-kassa operatorları və kassaların stoku" — **siyahının özü oxunmadı** | [e-kassa.gov.az](https://e-kassa.gov.az/), [taxes.gov.az — qeydiyyat](https://www.taxes.gov.az/az/post/2865) |
| API | Vergi Xidmətinin **ictimai developer API-si tapılmadı**; praktik yol — operatorun cihazı / proqramı və onun API-si. Nümunə: Omnitech öz cihazları ilə inteqrasiyadan danışır | [Omnitech e-kassa](https://www.akinsoft.com/other-solutions/omnitech-e-kassa) |
| Hər kiosk | Hər satış nöqtəsi (kiosk) üçün qeydiyyatda olan e-kassa lazımdır — bizim `TerminalId` buna uyğundur; qeydiyyat operator vasitəsilə, vergi ödəyicisinin e-kabinetində təsdiqlə | [taxes.gov.az — qeydiyyat](https://www.taxes.gov.az/az/post/2865) |
| Komissiya / abunə | bilinmir | — |
| Sandbox | bilinmir | — |

## 4. Bizim interfeyslərlə uyğunluq riski

| Mövzu | Risk | Nə etmək olar (M25–M26-da) |
|---|---|---|
| **İmzasız callback** (Payriff) | `ReadCallback(body, signature)` imzalı callback gözləyir; Payriff imzalamır | Adapter callback-i yalnız "status dəyişib" siqnalı sayır və `GetStatusAsync` ilə provayderdən təsdiq alır (artıq mövcud reconciliation yolu); callback URL-inə öz HMAC-ımızı əlavə etmək |
| **"Bank QR" = nə?** | Bizim axında kiosk QR göstərir, müştəri telefonla ödəyir. Gateway-lərdə bu QR **ödəniş səhifəsinin linki** olur (kartla telefonda); IPS QR (bank tətbiqi ilə) üçün merchant API-si bilinmir | Provayder seçimindən asılıdır: gateway linki QR-a çevirmək asandır; IPS QR üçün bankın merchant API sənədi lazımdır |
| **Kart kioskda** | `ICardPaymentProvider.ChargeAsync` fiziki POS terminalını nəzərdə tutursa — bankın POS SDK-sı / protokolu lazımdır, ictimai məlumat yoxdur | Pilotda yalnız QR (telefonla) qalırsa, `ICardPaymentProvider` mock qala bilər — layihə sahibinin qərarı |
| **PCI DSS** | Azericard-da S2S (kart məlumatı bizim serverdən) PCI DSS tələb edir | Host olunan səhifə (kart məlumatı bizə gəlmir) — PCI yükü minimal |
| **İmza alqoritmləri** | Azericard RSA + SHA-256 (öz kodumuzla, .NET-in daxili kriptoqrafiyası); Epoint SHA-1 (zəif, amma provayderin tələbidir) | Yeni paket lazım deyil |
| **e-kassa** | Ümumi API yoxdur, hər operatorun öz API-si; cihaz kioskun yanında ola bilər (lokal API) və ya bulud | Operator seçiləndən sonra `IFiscalProvider` adapteri; lokal cihazdırsa çağırış kioskdan getməli ola bilər (arxitektura sualı — ADR) |
| **Refund-un fiskal çeki** | `IssueRefundAsync` — e-kassa qaytarmanı dəstəkləyir (2019 sənədi), formatı operatordan | Operatorun sənədi |
| **Valyuta** | Bəzi Epoint əməliyyatları yalnız AZN (icma) | Bizdə yalnız AZN — problem yoxdur |

## 5. Layihə sahibinin göndərməli olduğu sorğular

**Bankdan / ödəniş xidmətindən (Azericard üzərindən bank, Payriff, Epoint və ya digər):**
1. Kioskda QR ilə ödəniş üçün hansı məhsulu təklif edirsiniz: ödəniş səhifəsinin linki (kart), yoxsa Ani Ödənişlər Sisteminin (IPS) QR-ı (bank tətbiqi)? Dinamik QR (hər sifarişə ayrıca məbləğ) varmı?
2. API sənədi (son versiya) və **sandbox** girişi — test açarları, test kartları.
3. Callback / webhook: imzalanırmı, hansı alqoritmlə? Təkrar göndərilirmi? Status sorğusu endpoint-i?
4. Refund: tam və qismən, onlayn reversal və gecikmiş refund; refund callback-i; neçə gün ərzində mümkündür?
5. QR / sessiyanın müddəti (bizdə `Payment.TimeoutMinutes`) və ödəniş gəlməsə ləğv əməliyyatı.
6. Komissiya: kart, QR (IPS), refund; aylıq ödəniş, minimum.
7. Müqavilə şərtləri: hüquqi şəxs, MCC, hesablaşma müddəti, PCI DSS tələbi (host olunan səhifədə də?).
8. Kioskda fiziki kart terminalı (lazım olsa): model, SDK / protokol, Linux və Windows dəstəyi.
9. Test serverimizin (internetdən əlçatan) callback ünvanı üçün IP whitelist / domen tələbləri.

**e-kassa operatorundan:**
1. Avtomatlaşdırılmış satış nöqtəsi (özünəxidmət kiosku) üçün e-kassa həlliniz varmı — cihaz, yoxsa bulud?
2. API sənədi və test mühiti: satış çeki, qaytarma çeki, Z-hesabat, status.
3. Hər kiosk üçün ayrıca e-kassa lazımdırmı; qeydiyyat addımları və müddəti.
4. Çekin müştəriyə verilməsi: QR / link (bizim qəbz ekranı və PDF-də göstəririk) — formatı.
5. Şəbəkə kəsiləndə davranış (oflayn növbə) və təkrar göndərmə; idempotentlik açarı.
6. Qiymət: cihaz / abunə / əməliyyat başına.

**Hazır saxlanmalı sənədlər:** şirkətin qeydiyyat sənədləri və VÖEN; bank hesabı; kiosk ünvanları (hər satış nöqtəsi); veb-sayt / domen və məxfilik siyasəti ilə istifadə şərtləri (T-162); refund siyasəti (`docs/legal/REFUND_POLICY.az.md`); texniki əlaqə şəxsi; (Azericard S2S olarsa) PCI DSS.

## 6. Nəticə və təklif

- **Ən çox sənədləşən və sandbox-u açıq olan:** Azericard (bank vasitəsilə). Payriff-in rəsmi .NET SDK-sı var, amma callback imzalanmır.
- **IPS QR** (Mərkəzi Bank) ən ucuz yol kimi təqdim olunur, amma merchant API-si yalnız bankdan alınar.
- **e-kassa** üçün ilk addım — e-kassa.gov.az-dakı operator siyahısından kiosk üçün həll təklif edən 2–3 operatorla əlaqə.
- Seçim **layihə sahibinindir**; adapterlər seçilmiş provayderin sandbox sənədi gələndən sonra yazılır (M25, M26).
