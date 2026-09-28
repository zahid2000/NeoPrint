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

### Alternatives

- **API konteynerinə `CAP_SYS_ADMIN` verib `unshare -n`** — rədd: bütün API-yə
  kernel səviyyəli imtiyaz vermək izolyasiya məqsədinə ziddir.
- **Ayrıca şəbəkədə HTTP converter servisi** (`internal: true` şəbəkə) — rədd:
  konvertor API-yə və şəbəkədəki digər servisə qoşula bilərdi; spool ilə
  `network_mode: none` mümkün olan ən sərt variantdır.
- **Ayrıca .NET layihəsi (`NeoPrint.Converter`)** — təxirə salındı: plan
  Bölmə 3-dəki repo strukturunda yoxdur; eyni image-in ayrıca rejimi yeni
  layihə, ayrıca publish və image tələb etmir.

### Consequences

- LibreOffice / Ghostscript / GhostPCL / GhostXPS istismarı şəbəkəyə, API-nin
  secret-lərinə, DB-yə çata bilmir; resurs tükəndirmə yalnız converter
  konteynerini (cgroup) və həmin konvertasiyanı (prlimit) vurur.
- **Qalıq risk:** PDFium (səhifə sayı, səhifə şəkilləri, redaktə) və SkiaSharp
  (şəkil → PDF) hələ API prosesinin içindədir. Bunlar da untrusted faylı parse
  edir; onların converter-ə köçürülməsi **Addım 7-də, pilotdan əvvəl** edilir
  (`docs/TASKS.md` T-051, layihə sahibinin 2026-09-28 qərarı). İndiki qoruyucu:
  PDFium yalnız artıq çevrilmiş/yoxlanmış PDF-i açır, Skia piksel limiti
  (100 MP, header-dən) tətbiq edir.
- Windows development maşınında (Docker-siz `dotnet run`) `Local` rejim işləyir:
  prlimit yoxdur, şəbəkə izolyasiyası yoxdur — yalnız Development-də icazəlidir.

### Security Impact

Müsbət: şəbəkəsizlik, imtiyazsızlıq, secret-sizlik, CPU/RAM/PID limiti.
Spool tmpfs-dir və yalnız iki konteynerə mount olunur; job faylında secret yoxdur.
Worker job-dakı proqram adına icazə siyahısı ilə, qovluğa isə yol yoxlaması ilə
baxır.

### Operational Impact

Yeni konteyner: `neoprint-converter` (compose ilə avtomatik qalxır). Log-lar JSON:
`docker compose logs converter`. Converter dayanıbsa konvertasiyalar gözləyir,
sonra "çevrilə bilmədi" ilə bitir; API `/health` `converter: Degraded` göstərir.

### Migration

Məlumat migrasiyası yoxdur. Lokal mühit: `docker compose up -d --build
--remove-orphans` (yeni servis və yeni `conversion-spool` volume-u yaradılır).

### Related Plan Step

Addım 2 (məcburi kross-kəsən qayda, layihə sahibinin şərti); planın Addım 8-dəki
"İzolyasiya olunmuş konvertasiya"nın backend hissəsi indi edilir, PDFium/Skia
köçürülməsi Addım 7-də (pilotdan əvvəl) edilir.

### Date

2026-09-27
