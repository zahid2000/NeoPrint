# ADR-0005 — İzolyasiya olunmuş konvertor: şəbəkəsiz `converter` konteyneri və spool

### Status

Accepted

### Context

`CLAUDE.md` ilk addımdan tələb edir: "İstifadəçi faylları untrusted input:
konvertasiya ayrıca prosesdə, şəbəkəsiz, timeout və resurs limiti ilə."
Layihə sahibi Addım 2-nin şərtlərində bunu ayrıca təkrarladı (konvertasiyanın
şəbəkəsiz işləməsi və CPU/RAM limiti — yalnız timeout yox — test ilə).

Addım 2-nin ilk versiyasında LibreOffice və Ghostscript ailəsi API konteynerinin
içində child proses kimi işləyirdi: ayrıca proses, vaxt limiti, proses ağacının
öldürülməsi, təmizlənmiş environment və Linux-da `prlimit --as` var idi. Amma
proses API ilə **eyni şəbəkəni və eyni istifadəçini** bölüşürdü və CPU limiti
yox idi. Bu boşluq `DECISIONS.md`-də Addım 8-ə keçirilmişdi — plan Bölmə 12-də
("İzolyasiya olunmuş konvertasiya: backend-də ayrıca converter servisi
(şəbəkəsiz konteyner, read-only root FS, CPU/RAM limiti, vaxt limiti)") bu işi
Addım 8-də göstərir. Audit zamanı bunun məcburi qaydanı pozduğu qəbul edildi.

Şəbəkəni prosesin içindən almaq mümkün deyil: Linux-da ayrıca network namespace
`CAP_SYS_ADMIN` tələb edir (API konteynerinə bu imtiyazı vermək izolyasiyanın
özündən daha təhlükəlidir), Docker-in default seccomp profili isə imtiyazsız
`unshare`-i bağlayır.

### Decision

**1. Konvertor ayrıca konteynerdə işləyir.** Eyni API image-i
`--converter-worker` açarı ilə başladılır; `Program.cs` bu açarı ən əvvəldə
ayırır — heç bir konfiqurasiya, secret store, DB və ya web host yüklənmir.
Konteyner (`docker-compose.yml`, `converter`):

- `network_mode: none` — yalnız `lo`, heç bir yerə qoşulmaq mümkün deyil;
- `read_only: true` root FS, `/tmp` tmpfs;
- `cap_drop: ALL`, `no-new-privileges`;
- `cpus: 2`, `mem_limit: 2g`, `pids_limit: 256` (cgroup);
- OpenBao credential-ı, seed faylı, portu yoxdur.

**2. API ilə yeganə əlaqə — spool.** `conversion-spool` tmpfs volume-dur
(sənəd diskə yazılmır, uid/gid 2000, `0700`), yalnız `api` və `converter`
konteynerlərinə mount olunur. Hər konvertasiyanın öz qovluğu var:

```
/spool/neoprint-convert-<guid>/
  input.<ext>        API yazır
  job.json           API yazır (atomik: .tmp → rename)
  job.claimed        worker götürəndə (rename — iki worker eyni işi götürə bilməz)
  cancel             API sorğunu ləğv edəndə
  result.json        worker yazır (atomik)
  converted.pdf      proqramın çıxışı
/spool/.converter-alive   worker-in heartbeat-i (5 san), API health check-i oxuyur
```

**3. Worker job-a etibar etmir.** Proqram icazə siyahısındadır
(`/usr/bin/soffice`, `soffice`, `gs`, `gpcl6`, `gxps`); iş qovluğu spool-un
birbaşa alt-qovluğudur; limitlər aralıqdadır (timeout ≤ 600 san, CPU ≤ 1200 san).
Limitləri worker özü yenidən tətbiq edir: `SandboxedProcess` hər proqramı
`prlimit --as=<yaddaş> --cpu=<CPU saniyə>` ilə başladır — konteynerin cgroup-u
üstündə bir qat da.

**4. Rejim konfiqurasiyadadır, production-da məcburidir.**
`Conversion:Mode` = `Isolated` (compose) və ya `Local` (Docker-siz development
maşını). `ConversionOptions.EnsureAllowedIn`: Development xaricində `Local`
rejimi ilə API **başlamır**; `Isolated` rejimdə `Conversion:WorkRoot` (spool)
məcburidir. API tərəfində `IProcessSandbox` abstraksiyası: `SandboxedProcess`
(Local) və `IsolatedProcessSandbox` (spool). Konvertorlar (`LibreOfficeConverter`,
`GhostscriptConverter`) hansının işlədiyini bilmir.

**5. Cavab gəlməsə:** API konvertasiyanın öz timeout-u + `IsolatedGraceSeconds`
(30) gözləyir, sonra nəticə `NotStarted` olur — fayl "çevrilə bilmədi" statusu
alır, consumer asılı qalmır. Converter-in heartbeat-i yoxdursa, API health-i
`Degraded` göstərir.

**6. PDFium, SkiaSharp və PDFsharp da yalnız converter-də işləyir (Addım 7A,
T-051, 2026-10-11).** İstifadəçi faylını parse edən bütün qalan əməliyyatlar —
səhifə sayı, səhifə şəkli (preview), şəkil → PDF, səhifə redaktəsi, şəkil
redaktəsi, tərtibatın (skan / layout) PDF-ə çəkilməsi — API prosesindən
çıxarılıb. Onları **converter aləti** edir: ayrıca .NET proqramı
(`backend/src/NeoPrint.Converter`, `NeoPrint.Converter.dll`), hər fayl üçün bir
qısa ömürlü proses.

- **Kim işə salır:** converter konteynerindəki worker — eyni spool protokolu
  ilə, icazə siyahısındakı yeni "proqram" `neoprint-tool` kimi; worker onu
  `dotnet NeoPrint.Converter.dll <əməliyyat>` kimi başladır. Docker-siz
  development maşınında (`Conversion:Mode=Local`) eyni aləti API child proses
  kimi işə salır — yəni heç bir rejimdə parser API prosesinin içində deyil.
- **Protokol:** əməliyyatın öz spool qovluğunda `tool-request.json` (API yazır)
  və `tool-response.json` (alət yazır) + adı sorğuda çəkilən fayllar. Alət
  yalnız öz iş qovluğundakı **sadə fayl adlarını** qəbul edir (yol, `..`,
  disk hərfi rədd olunur); worker alətdən yalnız altı məlum əməliyyatı və tək
  arqumenti qəbul edir. Cavabda istifadəçi üçün mesaj açarı (`pdf.damaged`,
  `conversion.imageTooLarge` …) gəlir; stderr-ə yalnız xətanın növü yazılır.
- **Limitlər:** alət .NET prosesidir və runtime bir neçə GB ünvan sahəsi
  rezerv edir, ona görə `prlimit --as` altında başlamır (yoxlanıb: 1–3 GB
  ilə "Failed to create CoreCLR"). Onun üçün `prlimit --data` (yazıla bilən
  yaddaş, 1280 MB) + `--cpu` tətbiq olunur — bu, native parser-lərin
  `malloc`-unu da bağlayır (yoxlanıb: 64 MB ilə proses "Out of memory" ilə
  dayanır). GC-yə limit `DOTNET_GCHeapHardLimit` ilə bildirilir. Alət öz
  `oom_score_adj`-unu 1000 edir ki, konteynerin yaddaşı bitəndə kernel
  worker-i yox, faylı tutan prosesi öldürsün.
- **Növbə:** worker-də alət işlərinin öz 3 yeri var — preview və səhifə sayı
  iki uzun Office konvertasiyasının arxasında gözləmir.
- **Yük həddi (T-222):** API tərəfində də eyni say var
  (`Conversion:InteractiveParallel` = 3): kiminsə gözlədiyi sorğu yalnız yer
  tutandan sonra faylı açıb spool-a yazır. Yer gözləyənlərin sayı
  (`InteractiveQueueLength` = 48) və gözləmə müddəti
  (`InteractiveQueueSeconds` = 10) sərhədlidir; sığmayan sorğu `503`
  (`code: files.processingBusy`) + `Retry-After: 3` alır. Ölçülüb (Docker
  Desktop, converter 2 CPU): eyni anda 100 müxtəlif səhifə istənəndə 52-si
  çəkildi, 48-i rədd olundu, ən gec cavab 9,9 saniyədə gəldi, konteynerdə
  eyni anda 3-dən çox alət prosesi görünmədi.
- **Səhifə şəkillərinin keşi (T-222):** çəkilmiş şəkil API-nin yaddaşında
  qalır (PDF hash-i + səhifə + en; 64 MB, 10 dəqiqə). Ölçülüb: ilk sorğu
  326 ms, təkrar sorğular ≤ 8 ms və converter-də yeni proses yoxdur. Keş
  yalnız yaddaşdadır — fayl məzmununun şifrəsiz nüsxəsi Redis-ə və diskə
  düşmür. `crossgen2` (ReadyToRun) əlavə olunmadı (layihə sahibi, 2026-10-11).
- **Converter dayananda:** kiminsə gözlədiyi sorğular (preview, redaktə,
  tərtibat) heartbeat 20 saniyədən köhnədirsə **dərhal** `503`
  (`code: files.processingUnavailable`, "Sənəd emalı xidməti hazırda
  işləmir… bir az sonra yenidən cəhd edin") alır — fayl "zədəli" kimi
  göstərilmir və sorğu timeout-u gözləmir. Yüklənən faylın konvertasiyası
  əvvəlki kimidir (bənd 5).
- **Sübut:** `NeoPrint.Infrastructure` artıq PDFsharp, SkiaSharp və
  `NeoPrint.Pdfium`-a istinad etmir. `ParserIsolationTests` API prosesini
  təşkil edən assembly-lərin metadata-sını oxuyur: heç biri bu kitabxanaların
  (və `NeoPrint.Layout.LayoutRenderer`-in) heç bir tipinə müraciət etmir;
  alətin assembly-si isə edir. İki tərəfin ortaq yeganə kodu
  `ToolContract.cs`-dir (hər ikisinə mənbə faylı kimi daxil edilir).

### Alternatives

- **API konteynerinə `CAP_SYS_ADMIN` verib `unshare -n`** — rədd: bütün API-yə
  kernel səviyyəli imtiyaz vermək izolyasiya məqsədinə ziddir.
- **Ayrıca şəbəkədə HTTP converter servisi** (`internal: true` şəbəkə) — rədd:
  konvertor API-yə və şəbəkədəki digər servisə qoşula bilərdi; spool ilə
  `network_mode: none` mümkün olan ən sərt variantdır.
- **Ayrıca .NET layihəsi (`NeoPrint.Converter`)** — Addım 2-də təxirə
  salınmışdı (plan Bölmə 3-dəki repo strukturunda yoxdur). **Addım 7A-da
  əlavə olundu (bənd 6):** yalnız alət üçün — worker yenə API image-inin
  `--converter-worker` rejimidir, ayrıca image yoxdur. Səbəb: (a) parser
  kitabxanalarının API assembly-lərindən **kompilyasiya səviyyəsində**
  ayrılması yalnız ayrı assembly ilə test oluna bilir; (b) hər fayl üçün bir
  proses başladığından alətin açılış vaxtı vacibdir — kiçik proqram API-nin
  öz binarından xeyli tez açılır.
- **Parser-ləri worker prosesinin içində çağırmaq** (child proses olmadan) —
  rədd: bir faylın yaratdığı yaddaş pozuntusu worker-i və ondan sonrakı
  bütün istifadəçilərin fayllarını vurardı; fayl başına proses həm çökməni,
  həm də bir faylın vəziyyətinin növbətiyə keçməsini bağlayır.
- **Alətə də `prlimit --as`** — mümkün deyil (bənd 6, "Limitlər").

### Consequences

- LibreOffice / Ghostscript / GhostPCL / GhostXPS istismarı şəbəkəyə, API-nin
  secret-lərinə, DB-yə çata bilmir; resurs tükəndirmə yalnız converter
  konteynerini (cgroup) və həmin konvertasiyanı (prlimit) vurur.
- **Qalıq risk bağlandı (Addım 7A, T-051):** PDFium, SkiaSharp və PDFsharp
  artıq API prosesində deyil (bənd 6). Qalan: (a) layout-un **JSON-u** API-də
  oxunur — bu `System.Text.Json` ilə idarə olunan koddur, native parser
  deyil; (b) qəbz PDF-ləri (QuestPDF) API-də yaradılır — onlar bizim öz
  məlumatımızdandır, istifadəçi faylı deyil; (c) preview indi hər səhifə üçün
  bir proses başladır — in-process render-dən yavaşdır (ölçü
  `docs/DECISIONS.md`-də).
- Windows development maşınında (Docker-siz `dotnet run`) `Local` rejim işləyir:
  prlimit yoxdur, şəbəkə izolyasiyası yoxdur — yalnız Development-də icazəlidir.

### Security Impact

Müsbət: şəbəkəsizlik, imtiyazsızlıq, secret-sizlik, CPU/RAM/PID limiti.
Spool tmpfs-dir və yalnız iki konteynerə mount olunur; job faylında secret yoxdur.
Worker job-dakı proqram adına icazə siyahısı ilə, qovluğa isə yol yoxlaması ilə
baxır.

### Operational Impact

Yeni konteyner: `neoprint-converter` (compose ilə avtomatik qalxır). Log-lar JSON:
`docker compose logs converter` — alət işləri də orada, əməliyyatın adı ilə
(`pdf-render`, `pdf-info` …). Converter dayanıbsa konvertasiyalar gözləyir,
sonra "çevrilə bilmədi" ilə bitir; API `/health` `converter: Degraded` göstərir.

### Migration

Məlumat migrasiyası yoxdur. Lokal mühit: `docker compose up -d --build
--remove-orphans` (yeni servis və yeni `conversion-spool` volume-u yaradılır).

### Related Plan Step

Addım 2 (məcburi kross-kəsən qayda, layihə sahibinin şərti); planın Addım 8-dəki
"İzolyasiya olunmuş konvertasiya"nın backend hissəsi indi edilir, PDFium/Skia
köçürülməsi Addım 7-də (pilotdan əvvəl) edilir.

### Date

2026-09-27; bənd 6 — 2026-10-11 (Addım 7A, T-051)
