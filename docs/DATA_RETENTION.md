# NeoPrint --- Data Retention

NeoPrint user files and financial records have different retention
requirements.

## User files

File metadata and binary content must be treated separately.

Supported auto-delete modes from the plan:

-   Never
-   AfterPrint
-   AfterDays

`DeleteAt` should be derived and processed by a reliable background job.

Deletion must be idempotent.

### Telefonda skan səhifələri (Addım 6, T-196)

Skan səhifələri telefonda yalnız skan sessiyası boyu, tətbiqin öz keşində (`cache/scan-*.jpg`) yaşayır:
skan saxlananda və ya ekrandan çıxılanda silinir. Android-də Google Play xidmətlərinin skaneri hər səhifənin
öz nüsxəsini də həmin keşdə (`cache/mlkit_docscan_ui_client/`) yaradır — kanal onları səhifələr tətbiqə
verilən kimi, skaner saxlamadan bağlananda və növbəti skan başlamazdan əvvəl silir. Qalereyaya heç nə
yazılmır. Yoxlama: `bash tools/dev-mobile/emu.sh cache` (debug build).

## Delete process

1.  Mark file as deleting/deleted according to domain rules.
2.  Remove storage object.
3.  Remove encrypted versions/previews when applicable.
4.  Remove related temporary artifacts.
5.  Record audit/technical result.
6.  Retry failed storage deletion safely.

## Account deletion

Account deletion must follow the plan: - personal data
removed/anonymized according to policy; - financial records preserved
only where legally/business-required; - wallet balance follows the
documented rule; - files are removed according to retention rules; -
audit history must not become misleading.

### NeoPrint-də necə işləyir (Addım 5, T-147)

- **Fayllar (avtomatik silinmə):** `FileAutoDeleteJob` (hər 5 dəq + `PrintJobCompleted`). `AfterPrint` — sifariş çap
  olunan kimi, faylın açıq sifarişi / aktiv planlanmış çapı varsa sonuncudan sonra; `AfterOneDay/SevenDays/ThirtyDays` —
  yükləmədən N gün sonra. Bütün obyektlər (orijinal, PDF, versiyalar) bütün provayderlərdən silinir (`FileEraser`,
  istifadəçinin öz silməsi ilə eyni yol), sonra `DeletedAt`; uğursuz silmə növbəti dövrdə təkrarlanır.
- **"Hesabımı sil":** şərtlər — aktiv çap, aktiv planlanmış çap, kioskda götürülməmiş sifariş, bitməmiş refund yoxdur.
  Dərhal: `Status = Deleted`, email → SHA-256 hash (+ hesab id-si), ad / şifrə / push cihazları silinir, sessiyalar ləğv
  olunur. `Privacy.AccountDeletionGraceHours` (24) keçəndən sonra `AccountDeletionJob` (10 dəq): fayllar bütün
  provayderlərdən, ixrac arxivləri, bildirişlər, hər iki istiqamətdəki fayl paylaşımları (`file_shares`) silinir; qəbzlərdən email, razılıqlardan IP / cihaz, dəstək
  müraciətlərindən mətn və cavab silinir (`—`), sonra `ErasedAt`.
- **Qalır (anonim hesaba bağlı):** ödənişlər, refund-lar, fiskal çeklər (dəyişmir), qəbzlər, balans ledger-i (balans
  itir, geri qaytarılmır — plan 2.14), razılıqların versiya və tarixi (sübut), müraciətin statusu / məbləği / qərarı,
  cihaz id-ləri (referral yoxlaması üçün, şəxsə işarə etmir).

## Export

"Məlumatlarımı yüklə" (T-147): ZIP — profil, ödənişlər, refund-lar, balans hərəkətləri, sifarişlər, qəbzlər (PDF), fayllar
(orijinal + versiyalar; `Privacy.ExportMaxMegabytes`-dan böyükdürsə yalnız siyahı). Temp bucket-də şifrəli, 24 saat
keçərli imzalı link; vaxtı keçəndə saatlıq iş arxivi silir. Şifrə hash-i, token-lər, provayder istinadları daxil deyil.


User export should include allowed personal data and metadata without
exposing secrets.

## Legal documents

Consent records should preserve: - document type - version - accepted
timestamp - IP where specified - device ID where specified

Published legal document versions must remain identifiable.

## Analitika hadisələri

`analytics_events` — MongoDB time-series kolleksiyası, `Analytics.RetentionDays`
(default 180) keçəndən sonra MongoDB özü silir (`expireAfterSeconds`; ayar
dəyişəndə növbəti başlanğıcda `collMod` ilə tətbiq olunur). Hadisələrdə fayl
adı və şəxsi məlumat yoxdur — yalnız kataloqdakı sahələr saxlanılır.

## Kiosk telemetriyası

`kiosk_telemetry` — MongoDB time-series kolleksiyası (hər kioskdan 30 saniyədə bir
sətir), `Kiosk.TelemetryRetentionDays` (default 30) keçəndən sonra MongoDB özü silir
(`expireAfterSeconds`; ayar dəyişəndə növbəti başlanğıcda `collMod` ilə tətbiq olunur).
Son hesabat Redis-də (`kiosk:status:{id}`) 20 interval (default 10 dəqiqə) saxlanır.
Telemetriyada istifadəçi və ya fayl haqqında heç nə yoxdur: ekranın adı, sessiyanın
növü (kimin olduğu yox), növbənin uzunluğu və çap olunan sifarişin kodu.

`printer_telemetry` — eyni qayda ilə (time-series, `Kiosk.TelemetryRetentionDays`): printerin
modeli, seriya nömrəsi, vəziyyəti, sərf səviyyələri, sayğacları və sistem növbəsindəki işlərin
nömrəsi / ölçüsü. İşin adı və ya məzmunu yoxdur.

## Financial data

Do not apply generic user-file retention rules to payments/fiscal
records.

Financial records need their own retention policy.

## Implementation

Do not hard-delete business data from arbitrary controllers.

Use explicit application commands/jobs.

All cleanup jobs must be retry-safe and observable.
