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

**Pilot hesabatının günləri (`analytics_days`, T-214)** — bir kiosk və bir Bakı günü üçün
yalnız say və növlər (format, səbəb açarı, ekran adı, kağız ölçüsü, ciddilik): fayl adı,
hesab, sessiya identifikatoru yoxdur. TTL yoxdur — pilotdan sonra da qərar üçün qalır.
Gün mənbə qeydlərindən hər dəfə yenidən hesablanır; mənbə hadisələri artıq saxlanılmayan
günü (`Analytics.RetentionDays`-dən köhnə) yenidən hesablamaq rədd olunur, printer
telemetriyası artıq olmayan gündə (`Kiosk.TelemetryRetentionDays`) əvvəl yazılmış toner
rəqəmləri saxlanılır — düzgün rəqəm sıfırla əvəz olunmur.

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

`printer_events` — adi kolleksiya, TTL indeksi ilə (`Kiosk.PrinterEventRetentionDays`, default
365; ayar dəyişəndə növbəti başlanğıcda `collMod` ilə tətbiq olunur). Printerə nə baş verdiyi:
toner dəyişdi, kağız dolduruldu, sıxılma başladı / bitdi, vəziyyət dəyişdi. Xam oxunuşlar
(dəqiqədə bir sətir) 30 gündən sonra silinir; hadisələr isə kiçikdir (printer üçün gündə bir neçə
sətir) və sərf proqnozu, servis tapşırıqları üçün bir il qalır. İstifadəçi və ya fayl haqqında heç
nə yoxdur.

## Incident bildirişlərinin qeydi

`incident_notifications` — incident-in kimə və hansı kanalla (`email` / `telegram`) göndərildiyi: sətirdə
yalnız incident-in `id`-si, kanal və admin hesabının `id`-si var — **ünvan və chat ID yoxdur**. Məqsədi
təkrar çatdırılan hadisənin eyni adama iki dəfə məktub yazmamasıdır, ona görə TTL indeksi ilə 30 gündən
sonra silinir (hadisənin təkrar çatdırılması günlərlə ölçülür, aylarla yox). Incident-in özü `incidents`-də
qalır.

## Sərf, servis tapşırıqları və anbar (plan 2.15)

`supply_alert_rules`, `supply_alerts`, `supply_events`, `service_tasks`, `inventory_items`, `inventory_movements` —
əməliyyat tarixçəsidir: hansı kioskda nə azaldı, kim doldurdu, anbardan nə çıxdı. **İstifadəçi və fayl haqqında heç nə
yoxdur**; şəxsə aid yeganə məlumat admin / operator hesabının `id`-sidir (`AssignedTo`, `By`, `CreatedBy`) və servis
tapşırığına yazılan qeyddir (ən çox 500 simvol — qeydə müştəri məlumatı yazılmır). TTL yoxdur: sərf hesabatı, proqnoz
və anbarın uçotu bu sətirlərə söykənir. Bağlanmış alert-lər və bitmiş tapşırıqlar silinmir (`Active = false`); pilotdan
sonra həcmə görə arxivləşdirmə qərarı verilə bilər.

## Səhifə şəkillərinin keşi

Önizləmə üçün çəkilmiş səhifə şəkli istifadəçi faylının məzmunudur. O, yalnız API prosesinin
yaddaşında saxlanılır: ən çox 64 MB, son istəkdən 10 dəqiqə sonra unudulur, proses dayananda
yox olur. Redis-ə və diskə yazılmır — orada şifrəli saxlanan faylın yanında şifrəsiz qalardı.
Fayl silinəndə keşdəki şəkil ən çox 10 dəqiqə yaddaşda qalır, amma əlçatmazdır: sorğu əvvəlcə
faylı və icazəni yoxlayır, silinmiş fayl üçün `404` qaytarır.

## Financial data

Do not apply generic user-file retention rules to payments/fiscal
records.

Financial records need their own retention policy.

## Implementation

Do not hard-delete business data from arbitrary controllers.

Use explicit application commands/jobs.

All cleanup jobs must be retry-safe and observable.
