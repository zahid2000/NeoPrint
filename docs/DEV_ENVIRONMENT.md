# NeoPrint — Development mühiti (Dev Environment)

Bu fayl **sizin öz əlinizlə icra edəcəyiniz hər şeyi** bir yerə toplayır: işə
salma əmrləri, lokal ünvanlar, bağlantı sətirləri, test hesabları və kiosk
aktivləşdirmə məlumatları. Hər yeni servis, port, rol hesabı və ya əmr
yarandıqca bu fayl yenilənir.

> **Buradakı bütün credential-lar yalnız lokal development mühitinə aiddir.**
> Onlar `.env.openbao-seed.example` faylında onsuz da açıqdır. Produksiya
> dəyərləri heç vaxt bu fayla yazılmır — onlar OpenBao-ya `bao kv put` ilə
> yazılır (`docs/OPENBAO.md`).

> **Layihədə PostgreSQL yoxdur.** Source of truth MongoDB-dir, Redis keş/queue/lock,
> RabbitMQ mesaj brokeri, RustFS obyekt saxlamasıdır (plan Bölmə 4).

---

## 1. Sürətli başlanğıc (sıfırdan)

```bash
# 1. Seed faylını hazırla — yalnız bir dəfə
cp .env.openbao-seed.example .env.openbao-seed

# 2. Bütün infrastrukturu və API-ni qaldır
docker compose up --remove-orphans -d

# 3. Hər şeyin qalxdığını yoxla
docker compose ps
curl http://localhost:5080/health
```

`docker compose up` ardıcıllığı: `openbao` → `openbao-init` (init + unseal + KV v2
mount + AppRole + policy + audit + 29 secret) → `mongo` → `mongo-init` (replica
set `rs0`) → `redis`, `rabbitmq`, `rustfs` + `rustfs-secondary` → `rustfs-init` (hər ikisində bucket-lər) → `api` →
`admin`.

**Backend kodu dəyişəndən sonra** konteyner köhnə image ilə işləməyə davam edir,
ona görə yenidən qurmaq lazımdır:

```bash
docker compose up --remove-orphans -d --build api
```

**Dayandırma:**

```bash
docker compose stop            # datanı saxlayır
docker compose down --remove-orphans            # konteynerləri silir, volume-lar qalır
docker compose down --remove-orphans -v         # HƏR ŞEYİ silir (Mongo, OpenBao, RustFS datası)
```

### Problem olarsa — əvvəlcə bunu işlədin

```bash
bash tools/dev-doctor/dev-doctor.sh                                    # Git Bash, Linux, macOS
powershell -ExecutionPolicy Bypass -File tools\dev-doctor\dev-doctor.ps1   # Windows PowerShell
```

Heç nəyi dəyişmir, yalnız yoxlayır və nə etmək lazım olduğunu yazır: Docker işləyirmi;
seed faylı nümunə ilə uyğundurmu (versiya, çatışmayan / köhnə açarlar); compose-un
portlarını (5080, 8080, 9000/9001, 9010, 27017, 6379, 5672/15672, 8200, 8025/1025)
başqa konteyner və ya proqram tuturmu; compose faylında artıq olmayan `neoprint-*`
konteyner və `neoprint_*` volume qalıbmı. Docker Desktop-da bash versiyası ~40 san,
PowerShell versiyası ~10 san çəkir.

### OpenBao restart-dan sonra möhürlənir (sealed)

`docker compose restart openbao` və ya maşını söndürdükdən sonra OpenBao
**sealed** vəziyyətdə qalxır və API secret oxuya bilmir. Açmaq üçün:

```bash
# Unseal açarı openbao-init tərəfindən yazılıb (bu fayl .gitignore-dadır)
docker compose up --remove-orphans -d openbao-init      # init.sh mövcud vəziyyəti görüb yalnız unseal edir
docker compose restart api
```

### Seed faylı (`.env.openbao-seed`) — yoxlama və yeniləmə

`openbao-init` hər `docker compose up`-da sizin `.env.openbao-seed` faylınızı
`.env.openbao-seed.example` ilə müqayisə edir və bu hallarda **dayanır** (API
başlamır):

- nümunədə olan açar sizin faylda yoxdur (**missing keys**);
- sizin faylda nümunədə olmayan açar var — adətən silinmiş servisdən qalıb, məs.
  `MINIO_ROOT_USER`, `SCALITY_*` (**unknown keys**);
- məcburi dəyər boşdur (**empty but needed**).

`SEED_SCHEMA_VERSION` nümunədəkindən fərqlidirsə, xəbərdarlıq verilir. Səbəbi görmək:

```bash
docker compose logs openbao-init
```

Düzəltmək:

- Faylda **öz dəyəriniz yoxdursa** (hər şey nümunədəki kimidir):
  `cp .env.openbao-seed.example .env.openbao-seed`, sonra `docker compose up -d --remove-orphans`.
- **Öz dəyərləriniz varsa**: çatışmayan sətirləri nümunədən köçürün, tanınmayanları silin.

OpenBao-da artıq olan dəyər heç vaxt dəyişdirilmir; seed faylına sonradan əlavə
olunan açar isə növbəti `up`-da OpenBao-ya əlavə edilir.

| Versiya | Dəyişiklik |
|---|---|
| (yoxdur) | Addım 2-yə qədərki fayllar |
| 2 | `SEED_SCHEMA_VERSION` və `NEOPRINT_DEV_TEST_PASSWORD` əlavə olundu; `MINIO_*`, `SCALITY_*` çıxarıldı, `RUSTFS_*` əlavə olundu |

Problem olarsa əvvəlcə `tools/dev-doctor` işlədin (bölmə 9).

---

## 2. Ünvanlar və portlar

| Servis | Ünvan | Qeyd |
|---|---|---|
| **API** | http://localhost:5080 | Swagger: http://localhost:5080/swagger (yalnız Development) |
| **API health** | http://localhost:5080/health | `/health/live`, `/health/ready` də var |
| **Admin panel (Docker)** | http://localhost:8080 | nginx `/api` və `/hubs`-u backend-ə proxy edir |
| **Admin panel (dev server)** | http://localhost:4200 | `ng serve`; `proxy.conf.json` `/api`-ni `localhost:8080`-ə yönləndirir, yəni admin konteyneri də qalxmış olmalıdır |
| **OpenBao UI / API** | http://localhost:8200 | Token: `.openbao/init.json` içindəki root token |
| **MongoDB** | `localhost:27017` | replica set `rs0` |
| **Redis** | `localhost:6379` | |
| **RabbitMQ (AMQP)** | `localhost:5672` | |
| **RabbitMQ management** | http://localhost:15672 | |
| **RustFS — primary** | http://localhost:9000 | S3 API |
| **RustFS — veb konsolu** | http://localhost:9001/rustfs/console/ | `neoprintrustfs` / `devRustfsPassword` |
| **RustFS — secondary** | http://localhost:9010 | migrasiya / mirror hədəfi (plan 9.8) |
| **Mailpit (SMTP tutucu)** | http://localhost:8025 | bütün email-lər buraya gəlir; SMTP: `localhost:1025` |
| **Hangfire dashboard** | http://localhost:5080/hangfire | yalnız Development |

---

## 3. Bağlantı sətirləri və credential-lar (development)

Hamısı `.env.openbao-seed` faylındandır. Konteyner daxilindəki host adları
(`mongo`, `redis`, …) yalnız Docker şəbəkəsində işləyir; öz maşınınızdan
qoşulanda `localhost` istifadə edin.

### MongoDB

| | |
|---|---|
| İstifadəçi | `neoprint` |
| Şifrə | `devMongoPassword` |
| Replica set | `rs0` |
| Baza | `neoprint` |

```bash
# Öz maşınından (Compass, mongosh)
mongodb://neoprint:devMongoPassword@localhost:27017/neoprint?authSource=admin&directConnection=true

# Konteyner daxilindən (API bunu OpenBao-dan alır)
mongodb://neoprint:devMongoPassword@mongo:27017/neoprint?authSource=admin&replicaSet=rs0&directConnection=true
```

```bash
# Sürətli mongosh
docker exec -it neoprint-mongo mongosh -u neoprint -p devMongoPassword --authenticationDatabase admin neoprint
```

### Redis

| | |
|---|---|
| Şifrə | `devRedisPassword` |

```bash
localhost:6379,password=devRedisPassword,abortConnect=false

docker exec -it neoprint-redis redis-cli -a devRedisPassword
```

### RabbitMQ

| | |
|---|---|
| İstifadəçi | `neoprint` |
| Şifrə | `devRabbitPassword` |
| Management | http://localhost:15672 |

```bash
amqp://neoprint:devRabbitPassword@localhost:5672/
```

### Obyekt saxlaması — RustFS

Hər iki instansiya (`rustfs`, `rustfs-secondary`) eyni açarlarla işləyir.

| | |
|---|---|
| Access key | `neoprintrustfs` |
| Secret key | `devRustfsPassword` |
| Primary endpoint | http://localhost:9000 (konteyner daxilindən `http://rustfs:9000`) |
| Primary veb konsolu | http://localhost:9001/rustfs/console/ (yuxarıdakı açarlarla daxil olun) |
| Secondary endpoint | http://localhost:9010 (konteyner daxilindən `http://rustfs-secondary:9000`) — konsolu yoxdur |
| Region | `us-east-1` |
| Bucket-lər | `neoprint-files`, `neoprint-receipts`, `neoprint-temp` (hər ikisində, `rustfs-init` yaradır) |
| Health | http://localhost:9000/health/ready (açarsız) |
| Image | `rustfs/rustfs:1.0.0` (digest ilə pinlənib) |

Konsoldan və ya istənilən S3 klienti ilə baxmaq olar — məsələn AWS CLI:

```bash
aws --endpoint-url http://localhost:9000 s3 ls s3://neoprint-files --recursive
# (AWS_ACCESS_KEY_ID=neoprintrustfs AWS_SECRET_ACCESS_KEY=devRustfsPassword)
```

Oradakı obyektlər **şifrəlidir** — endirsəniz açılmaz, bu gözlənilən haldır.

Admin panelində ikinci provayderi (migrasiya / mirror üçün) əlavə edərkən:
endpoint `http://rustfs-secondary:9000`, tip **RustFS**, yuxarıdakı açarlar.

**Konteyner non-root işləyir (UID/GID 10001).** Compose data üçün named volume
(`rustfs-data`, `rustfs-secondary-data`) istifadə edir — Docker onları image-dəki
sahiblə yaradır, heç nə etmək lazım deyil. Named volume əvəzinə host qovluğu
bind-mount etsəniz (məs. pilot Linux serverində), əvvəl sahibini dəyişin, yoxsa
RustFS `Permission denied` ilə başlamır:

```bash
sudo mkdir -p /srv/neoprint/rustfs && sudo chown -R 10001:10001 /srv/neoprint/rustfs
```

Log-lar `docker compose logs rustfs` ilə görünür (stdout-a yazılır).

#### Köhnə storage-dan (MinIO / Zenko) RustFS-ə keçid (mövcud lokal mühit üçün)

Sizin lokal `.env.openbao-seed` faylınız hələ köhnə (MinIO) dəyərlərdədir və
`openbao-init` artıq dəyəri olan secret-ə **toxunmur**. Köhnə konteynerlər
(`neoprint-minio`, `neoprint-scality`, `neoprint-zenko*`) 9000/9001/8000
portlarını tutur. Əlamət — `docker compose up` bu xəta ilə dayanır:

```
Bind for 0.0.0.0:9001 failed: port is already allocated
```

Köhnə servislər yeni compose faylında olmadığı üçün `docker compose up` onları
özü dayandırmır. İki yol:

**A. Sıfırdan (tövsiyə olunur, bütün lokal data silinir):**

```bash
docker compose down -v --remove-orphans
docker volume rm neoprint_minio-data 2>/dev/null  # köhnə servisin volume-u compose-dan kənarda qalır
cp .env.openbao-seed .env.openbao-seed.bak        # köhnəsini saxlayın
cp .env.openbao-seed.example .env.openbao-seed    # RustFS dəyərləri
docker compose up --remove-orphans -d --build
```

**B. Mongo / OpenBao datasını saxlayaraq** (köhnə storage-dakı test faylları
itir — obyektlər köhnə konteynerin volume-undadır):

1. `.env.openbao-seed`-də `MINIO_ROOT_*`, `SCALITY_*` sətirlərini silin, nümunə
   fayldakı `RUSTFS_ACCESS_KEY` / `RUSTFS_SECRET_KEY` və
   `NEOPRINT_STORAGE_SEED_*` sətirlərini köçürün.
2. Köhnə konteynerləri silin və seed yolunu yeniləyin:

```bash
docker rm -f neoprint-minio neoprint-minio-init neoprint-scality neoprint-zenko neoprint-zenko-secondary neoprint-zenko-init 2>/dev/null
docker exec -e BAO_ADDR=http://127.0.0.1:8200 -e BAO_TOKEN=<root-token> neoprint-openbao bao kv patch -mount=neoprint backend/seed NEOPRINT_STORAGE_SEED_TYPE=RustFS NEOPRINT_STORAGE_SEED_NAME="Local RustFS" NEOPRINT_STORAGE_SEED_ENDPOINT=http://rustfs:9000 NEOPRINT_STORAGE_SEED_ACCESS_KEY=neoprintrustfs NEOPRINT_STORAGE_SEED_SECRET_KEY=devRustfsPassword
docker exec neoprint-mongo mongosh -u neoprint -p devMongoPassword --authenticationDatabase admin neoprint --eval "db.storage_providers.deleteMany({}); db.storage_sync_jobs.deleteMany({}); db.storage_sync_failures.deleteMany({}); db.files.deleteMany({})"
docker compose up -d --remove-orphans --build
```

Storage provayderi yalnız `storage_providers` boş olanda seed edilir, ona görə
köhnə provayder sənədi silinir; köhnə storage-a istinad edən fayl qeydləri də
silinir, çünki obyektlərin özü artıq yoxdur. `<root-token>` —
`.openbao/init.json`-dakı `root_token`.

### OpenBao

| | |
|---|---|
| Ünvan | http://localhost:8200 |
| KV mount | `neoprint` (KV v2) |
| Root token | `.openbao/init.json` → `root_token` (gitignore-dadır) |
| Unseal açarı | `.openbao/init.json` → `unseal_keys_b64[0]` |
| API credential-ları | `.openbao/creds/neoprint-api.role_id` və `.secret_id` |

```bash
# Secret yollarını görmək (dəyərləri oxumadan)
docker exec -e BAO_ADDR=http://127.0.0.1:8200 -e BAO_TOKEN=<root-token> \
  neoprint-openbao bao kv list neoprint/backend
```

KV yolları: `neoprint/backend/core`, `/crypto`, `/providers`, `/seed`.

---

## 4. Test hesabları

### Hazır test hesabları (Development-də avtomatik yaradılır)

API `Development` rejimində işə düşəndə bu hesablar **yoxdursa** yaradılır
(mövcud hesaba — dəyişdirdiyiniz şifrəyə də — toxunulmur). Hamısının şifrəsi
seed faylındakı `NEOPRINT_DEV_TEST_PASSWORD`-dur və **ilk girişdə şifrə dəyişmək
tələb olunmur**. Production-da bu hesablar heç vaxt yaradılmır.

| Hesab | Rol / tətbiq | Email | Şifrə |
|---|---|---|---|
| SuperAdmin (test) | Admin paneli — hər şey | `dev-superadmin@neoprint.local` | `DevTest!2026` |
| Admin | Admin paneli — storage, adminlər, rollar, audit xaric | `dev-admin@neoprint.local` | `DevTest!2026` |
| Operator | Admin paneli — kiosk xidməti + `/operator` | `dev-operator@neoprint.local` | `DevTest!2026` |
| Maliyyə | Admin paneli — ödəniş, qaytarma, qəbz, fiskal | `dev-finance@neoprint.local` | `DevTest!2026` |
| Mobil istifadəçi | Mobil tətbiq — email təsdiqlənib, razılıqlar qəbul olunub | `dev-user@neoprint.local` | `DevTest!2026` |

Admin paneli: http://localhost:8080 · Mobil tətbiq: bölmə 6.

### Admin paneli — əsas SuperAdmin (hər mühitdə seed edilir)

| | |
|---|---|
| Ünvan | http://localhost:8080 |
| Email | `admin@neoprint.local` |
| İlk şifrə | `ChangeMe!2026` |

**İlk girişdə şifrə dəyişmək məcburidir** (plan Bölmə 2.8). Dəyişdiyiniz şifrəni
buraya yazın ki, itməsin:

| | |
|---|---|
| Yeni şifrə (siz təyin edirsiniz) | `…` |

Rolların nə gördüyü:

| Rol | Nə görür |
|---|---|
| `SuperAdmin` | Hər şey. Sistem rolu — redaktə/silinə bilməz, ən azı 1 aktiv qalmalıdır |
| `Admin` | Storage, admin istifadəçiləri, rollar və audit **xaric** hamısı |
| `Operator` | Kiosk xidməti: kiosklar, printerlər, sərf materialları, servis tapşırıqları, incident-lər + `/operator` paneli |
| `Maliyyə` | Ödəniş, qaytarma, qəbz, fiskal çeklər, wallet düzəlişi |

### Mobil tətbiq — yeni hesab

Hazır hesab yuxarıdadır (`dev-user@neoprint.local`). Yeni hesab yaratmaq üçün
tətbiqdən qeydiyyatdan keçin — təsdiq kodu email ilə gəlir və **Mailpit**-də
görünür: http://localhost:8025

Kodu terminaldan götürmək (ünvanı öz emailinizlə əvəz edin):

```bash
EMAIL=siz@neoprint.test
ID=$(curl -s "http://localhost:8025/api/v1/search?query=to:$EMAIL" | grep -o '"ID":"[^"]*"' | head -1 | sed 's/.*://;s/"//g')
curl -s "http://localhost:8025/api/v1/message/$ID" | grep -o '[0-9]\{6\}' | head -1
```

> `docker compose down -v --remove-orphans` bütün hesabları silir (Mongo volume-u ilə);
> növbəti `up`-da hazır test hesabları yenidən yaradılır.

---

## 5. Kiosk tətbiqini işə salmaq

### Test kioskları (Development-də seed edilir)

| Kod | Ad | Admin PIN |
|---|---|---|
| `KSK-9001` | Test kiosk — Windows | `135790` |
| `KSK-9002` | Test kiosk — Linux | `135790` |

Hər ikisi üçün **enrollment token** yaradılır və API log-una yazılır:

```bash
docker logs neoprint-api 2>&1 | grep -A 8 "Development enrollment tokens"
```

### Windows-da işə salmaq

Cihaz açarı **machine-scoped CNG** açarıdır, ona görə **Administrator**
sessiyası tələb olunur. Adi istifadəçi sessiyasında açar yaradılmır.

PowerShell-i **"Run as administrator"** ilə açın və skriptdən istifadə edin
(`tools/dev-kiosk/`):

```powershell
cd C:\Users\EFSO\source\zahid\Ai\NeoPrint

# Build + işə salmaq (köhnə kiosk açıqdırsa əvvəl onu bağlayır)
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1

# Kod ekranı olmadan, token ilə dərhal aktivləşdirmək
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1 -EnrollmentToken <TOKEN>

# Başqa API-yə qarşı / Release build / build-siz
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1 -ApiUrl http://192.168.1.50:5080
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1 -NoBuild

# Dayandırmaq
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\stop-kiosk.ps1

# Aktivləşdirmə ekranına qaytarmaq / yalnız təhlükəsiz rejim sayğacını silmək
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\reset-kiosk.ps1
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\reset-kiosk.ps1 -SafeModeOnly
```

| Skript | Nə edir |
|---|---|
| `start-kiosk.ps1` | Administrator yoxlayır → işləyən kiosku bağlayır → `dotnet build` → `NEOPRINT_KIOSK_ENVIRONMENT=Development` → seçilmiş konfiqurasiyanın ən son `NeoPrint.Kiosk.exe`-sini işə salır. Parametrlər: `-EnrollmentToken`, `-ApiUrl`, `-Configuration Debug\|Release`, `-NoBuild` |
| `stop-kiosk.ps1` | İşləyən kiosku bağlayır; 5 saniyədə bağlanmasa dayandırır (çöküş kimi sayılır) |
| `reset-kiosk.ps1` | Kiosku dayandırır, `%ProgramData%\NeoPrint`-i silir (cihaz kimliyi, keş, spool, jurnal). `-SafeModeOnly` — yalnız çöküş sayğacı; `-DeleteDeviceKey` — CNG cihaz açarı da |

**Hansı fayllar açılır.** PDF, şəkil (JPG/PNG/…) və TXT hər maşında açılır:
PDFium kioskun build-inə özü düşür (ilk build onu internetdən götürür və
SHA-256 ilə yoxlayır). Word / Excel / PowerPoint üçün maşında
[LibreOffice](https://www.libreoffice.org/) (`C:\Program Files\LibreOffice`),
PS / PCL / XPS üçün Ghostscript (`gswin64c.exe` PATH-da) lazımdır — yoxdursa
kiosk "bu fayl növünü açan proqram işləmir" deyir, `start-kiosk.ps1` də
başlayanda xəbərdarlıq edir.

**Build `MSB3027 ... The file is locked by: "NeoPrint.Kiosk (…)"` ilə qırılırsa**,
əvvəlki kiosk hələ açıqdır və DLL-ləri tutur: `stop-kiosk.ps1` işlədin (və ya
`Stop-Process -Name NeoPrint.Kiosk`). `start-kiosk.ps1` bunu özü edir.

Skriptsiz (eyni şey):

```powershell
$env:NEOPRINT_KIOSK_ENVIRONMENT = "Development"
dotnet run --project desktop\src\NeoPrint.Kiosk
```

`Development` mühiti `appsettings.Development.json`-u yükləyir:
`ApiBaseUrl = http://localhost:5080`, `UseFakePrinter = true`,
`ActivationPollSeconds = 5`.

Aktivləşdirmə kodunu ekranda gözləmək istəmirsinizsə, token ilə birbaşa
aktivləşdirin:

```powershell
dotnet run --project desktop\src\NeoPrint.Kiosk -- --enrollment-token <TOKEN>
```

Başqa API ünvanına qarşı:

```powershell
dotnet run --project desktop\src\NeoPrint.Kiosk -- --api-url http://192.168.1.50:5080
```

### Cihaz kimliyini sıfırlamaq (yenidən Activation ekranı)

```powershell
# Windows (administrator): tools\dev-kiosk\reset-kiosk.ps1 və ya əl ilə
Remove-Item -Recurse -Force "$env:ProgramData\NeoPrint" -ErrorAction SilentlyContinue
```

```bash
# Linux
sudo rm -rf /var/lib/neoprint
```

Alternativ (daha düzgün yol): admin paneldə **Cihazlar → Cihazı ayır**. Kiosk
növbəti sorğuda 403 alır və özü Activation ekranına qayıdır.

### Kioska kağız qoymaq (sayğac)

Yeni aktivləşdirilmiş kioskun lotoku **boşdur** (`Current = 0`) və
`Paper.BlockWhenInsufficient` açıqdırsa heç bir sifariş qəbul olunmur
("Bu kioskda hazırda kifayət qədər kağız yoxdur").

Admin paneldə: **Kiosklar → kiosk → Sərf** tabı → "Kağız doldur" → üsul
(**Əlavə et** / **Tam doldur** / **Dəqiq say**) → **Yadda saxla**. Eyni tabda
lotokun səviyyəsi və son hərəkətlər (çap olunan sifarişlər, test səhifəsi,
doldurma) görünür. Kağız qurtaran və ya azalan kiosk dashboard-dakı
"Kioskların vəziyyəti" kartında "Diqqət tələb edir" siyahısına düşür.

Eyni əməliyyat API ilə (SuperAdmin tokeni ilə):

```powershell
$token = "<admin accessToken>"
$kiosk = "<kioskId>"
Invoke-RestMethod -Method Post "http://localhost:5080/api/admin/kiosks/$kiosk/paper/refill" `
  -Headers @{ Authorization = "Bearer $token"; "Idempotency-Key" = [guid]::NewGuid().ToString("N") } `
  -ContentType "application/json" -Body '{ "paper": "A4", "mode": "Full", "sheets": 0 }'
```

`mode`: `Full` — tutuma qədər, `Add` — `sheets` qədər əlavə, `Exact` — sayılmış dəqiq say.

### Təhlükəsiz rejim ("Texniki xidmət" ekranı)

Kiosk tətbiqi 10 dəqiqədə 3 dəfə təmiz bağlanmadan dayanarsa (çöküş, prosesin
öldürülməsi), növbəti açılışda **Texniki xidmət** ekranı göstərilir, sifariş
qəbul olunmur və admin paneldə kiosk `Maintenance` olur. Reboot və elektrik
kəsilməsi çöküş sayılmır. Development-də debugger-i 3 dəfə dayandırmaq da
bu rejimi aça bilər.

Söndürmək: admin menyusu (aşağıda) → **Təhlükəsiz rejimdən çıx və yenidən
başlat**. Kiosk aktivləşdirilməyibsə (PIN yoxdur) sayğac faylını silib
tətbiqi yenidən başladın:

```powershell
# Windows
Remove-Item "$env:ProgramData\NeoPrint\state\runs.json" -ErrorAction SilentlyContinue
```

```bash
# Linux
sudo rm -f /var/lib/neoprint/state/runs.json
```

### Gizli admin menyusu

Başlıqdakı **NeoPrint loqosuna 3 saniyə ərzində 5 dəfə** klikləyin → PIN
klaviaturası → kioskun admin PIN-i (test kiosklarında `135790`). Menyu:
təhlükəsiz rejimdən çıx, yenidən başlat, texniki giriş. Development-də
`Kiosk:Lockdown` söndürülüb — Win düyməsi və Alt+Tab developer maşınında
işləyir, menyunun hər əməliyyatı isə sadəcə tətbiqi bağlayır (kiosk
maşınında "Texniki giriş" Windows hesabından çıxış edir — developer maşınında
bu olmur).

### Linux-da işə salmaq

```bash
export NEOPRINT_KIOSK_ENVIRONMENT=Development
dotnet run --project desktop/src/NeoPrint.Kiosk
```

Cihaz açarı `/var/lib/neoprint/device/key.pem` (`chmod 600`) faylındadır, ona
görə həmin qovluğa yazma icazəsi lazımdır.

---

## 6. Mobil tətbiqi işə salmaq (Flutter)

Əvvəlcə stack qalxmış olmalıdır (`docker compose up --remove-orphans -d`). Giriş üçün
hazır hesab: `dev-user@neoprint.local` / `DevTest!2026` (bölmə 4). Yeni hesabın
təsdiq kodu Mailpit-dədir: http://localhost:8025

API ünvanı build zamanı seçilir; `--dart-define=NEOPRINT_API_URL=...` verilməyibsə
debug build bu default-ları götürür:

| Hədəf | Default API ünvanı |
|---|---|
| Chrome (Flutter web) | `http://localhost:5080/` |
| Android emulyator | `http://10.0.2.2:5080/` (emulyatorun içindən kompüterin `localhost`-u) |
| iOS simulyator | `http://localhost:5080/` |
| Real telefon | default işləmir — kompüterin şəbəkə IP-si verilməlidir (aşağıda) |
| Release build | `https://api.neoprint.az/` |

```bash
cd mobile
flutter pub get
```

### Chrome (Flutter web)

```bash
flutter run -d chrome
```

API CORS-u Development-də `localhost`-un istənilən portunu qəbul edir; Flutter-in
seçdiyi port fərq etmir. Başqa ünvan lazımdırsa: `--dart-define=NEOPRINT_API_URL=http://localhost:5080`.

### Android emulyator

```bash
flutter emulators                          # mövcud emulyatorlar
flutter emulators --launch <emulator_id>   # birini açın
flutter run -d emulator-5554               # və ya sadəcə: flutter run
```

Ünvan avtomatik `10.0.2.2:5080`-dir, heç nə vermək lazım deyil.

### Real telefon (Android, USB və ya eyni Wi-Fi)

1. Kompüterin yerli şəbəkə IP-sini tapın: Windows `ipconfig` → "IPv4 Address"
   (məs. `192.168.1.50`); Linux/macOS `ip addr` / `ifconfig`.
2. Telefon və kompüter **eyni Wi-Fi**-da olmalıdır; Windows firewall 5080 portuna
   daxil olan bağlantını buraxmalıdır (ilk dəfə soruşanda "Allow").
3. Telefonda "Developer options" → "USB debugging" açıq, USB ilə qoşun, sonra:

```bash
flutter devices                            # telefonun ID-si
flutter run -d <telefon_id> --dart-define=NEOPRINT_API_URL=http://192.168.1.50:5080
```

Telefonun brauzerində `http://192.168.1.50:5080/health/live` açılırsa, tətbiq də
qoşulacaq. Açılmırsa problem şəbəkə/firewall-dadır, tətbiqdə yox.

### Şəbəkə xətası görsəniz

Tətbiq "Serverə qoşulmaq mümkün olmadı" deyirsə: API işləyir? (`curl http://localhost:5080/health/live`);
ünvan düzgündür? (debug konsolunda `Request to ... failed without a response: ...`
sətri əsl səbəbi göstərir); real telefondursa — yuxarıdakı IP/Wi-Fi/firewall addımları.

---

## 7. Admin panelini işə salmaq (Angular)

```bash
cd admin
npm ci
npm start            # http://localhost:4200
```

`proxy.conf.json` `/api` və `/hubs`-u `http://localhost:8080`-ə (admin
konteynerinin nginx-inə) yönləndirir, ona görə `docker compose up --remove-orphans -d` işləməlidir.

---

## 7a. Addım 2 funksiyalarını yoxlamaq

Yeni kod üçün image-ləri yenidən qurun: `docker compose up --remove-orphans -d --build api admin`.
Admin paneli: http://localhost:8080 (və ya `npm start` ilə http://localhost:4200),
SuperAdmin: `admin@neoprint.local`.

| Səhifə | Ünvan | Nəyi yoxlamaq |
|---|---|---|
| Kiosklar | `/kiosks` | Siyahı ↔ Xəritə, status filtri, axtarış; sətirə klik → redaktə |
| Yeni kiosk | `/kiosks/new` | Xəritədə nöqtə seç, PIN (4–12 rəqəm), kağız tutumu → "Kiosk yarat" |
| Kiosk redaktə | `/kiosks/{id}` | Tab-lar: Ümumi, Limitlər ("Default istifadə et"), Qiymət və fiskal, Kağız, Admin PIN, Cihaz |
| Qiymət profilləri | `/pricing` | Profil yarat/redaktə, pillələr, "Default et", kiosk bağla, kalkulyator |
| Fayl formatları | `/formats` | Formatı söndür → mobil tətbiqdə həmin fayl seçilmir və yüklənmir |
| Fayl saxlama | `/storage` | Provayderlər, bağlantı testi, sinxronizasiya sehrbazı, canlı proqres |

### Storage migrasiyası (plan 9.8) — addım-addım

1. **Fayl saxlama → Provayder əlavə et**:
   ad `Second RustFS`, tip **RustFS**, endpoint `http://rustfs-secondary:9000`,
   region `us-east-1`, HTTPS **söndürülü**, access key `neoprintrustfs`,
   secret key `devRustfsPassword`, bucket-lər `neoprint-files` / `neoprint-receipts` /
   `neoprint-temp` → **Bağlantını yoxla** → **Yadda saxla**.
2. **Sinxronizasiya → Yeni sinxronizasiya** → Migrasiya → hədəf `Second RustFS` →
   "Bitəndə hədəfi Primary et" → Başlat. Proqres canlı yenilənir; Dayandır / Davam et işləyir.
3. Bitəndə `Second RustFS` Primary olur. Faylların oxunduğunu mobil tətbiqdə yoxlayın.
4. **Mənbəni təmizlə**: `Storage.SourceRetentionDays` (default 30 gün) keçəndən sonra
   görünür. Lokalda yoxlamaq üçün **Tənzimləmələr**-də onu `0` edin.
5. Mirror: köhnə provayderi "Mirror kimi qoş" ilə Secondary edin; sonra
   `docker stop neoprint-rustfs-secondary` (hansı Primary-dirsə onu dayandırın) —
   oxumalar digərindən davam edir, provayderin vəziyyəti ~1 dəqiqədə "Cavab vermir" olur.

RustFS konsolunda obyektlərə baxmaq: http://localhost:9001/rustfs/console/ — orada
görünən fayllar **şifrəlidir**, endirsəniz açılmaz (gözlənilən haldır).

### Konvertasiya — izolyasiya olunmuş `converter` konteyneri

Word/Excel/PowerPoint/PS/PCL/XPS faylları API-də yox, `neoprint-converter`
konteynerində çevrilir (şəbəkəsiz, read-only, CPU/RAM limitli —
`docs/adr/ADR-0005-isolated-converter.md`). Portu yoxdur.

```bash
docker compose logs -f converter                       # hər iş üçün bir sətir: proqram, nəticə, müddət
curl -s http://localhost:5080/health | grep converter  # "Healthy" — heartbeat gəlir

# İzolyasiyanı öz gözünüzlə yoxlamaq
docker exec neoprint-converter sh -c 'tail -n +3 /proc/net/dev | cut -d: -f1'   # yalnız lo
docker exec neoprint-converter curl -m 3 http://api:8080/health/live             # uğursuz olmalıdır
docker inspect neoprint-converter --format '{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}} {{.HostConfig.PidsLimit}} {{.HostConfig.NetworkMode}}'
```

Converter dayanıbsa fayllar "Hazırlanır" statusunda gözləyir və converter qalxanda
çevrilir (təxminən 90 saniyədən çox gözlədisə "Açılmadı" olur — faylı yenidən yükləyin).

Docker-siz `dotnet run` ilə API-ni işlədəndə konvertasiya lokal child prosesdə gedir
(`Conversion:Mode=Local`, yalnız Development-də icazəlidir).

### Şifrələməni storage-dan birbaşa yoxlamaq

Backend-i keçərək obyekti RustFS-dən birbaşa oxuyun — faylın özü (`%PDF`, `%!PS`)
yox, `NPENC` ilə başlayan şifrəli zərf gəlməlidir; açarsız sorğu `403` alır:

```bash
# Obyekt açarlarını siyahıla
curl -s --aws-sigv4 "aws:amz:us-east-1:s3" -u "neoprintrustfs:devRustfsPassword"   "http://localhost:9000/neoprint-files?list-type=2&max-keys=20" | grep -o "<Key>[^<]*</Key>"

# Birini oxu: ilk baytlar NPENC olmalıdır
curl -s --aws-sigv4 "aws:amz:us-east-1:s3" -u "neoprintrustfs:devRustfsPassword"   "http://localhost:9000/neoprint-files/<açar>" | head -c 16 | od -c

# Açarsız: 403
curl -s -o /dev/null -w "%{http_code}
" "http://localhost:9000/neoprint-files/<açar>"
```

### Fayl yüklədim, amma "Növbədədir" statusundan çıxmır

Köhnə API image-idir: `v0.2.0`-dan əvvəlki build-də hadisələr outbox-a yazılmırdı və
konvertasiya başlamırdı (`docs/DECISIONS.md`). `docker compose up --remove-orphans -d --build api` ilə
yenidən qurun; əvvəl yüklənmiş fayllar üçün onları silib yenidən yükləyin.

### Mobil — Fayllarım

| Nə | Necə |
|---|---|
| Fayl yüklə | "Fayl yüklə" → "Fayl seç". Seçici yalnız aktiv formatları göstərir (+ HEIC) |
| Şəkil çək | "Fayl yüklə" → "Şəkil çək" (brauzerdə yoxdur; emulyatorda virtual kamera) |
| HEIC → JPEG | iPhone şəkli (`.heic`) seçin: telefonda JPEG-ə çevrilib yüklənir. Android 9+ lazımdır; web-də işləmir (izah göstərilir) |
| Baxış | Hazır fayla toxunun → səhifələr serverdə render olunur (PDFium API image-ində var) |
| Sil | Faylın menyusu (⋮) → Sil → təsdiq |

Word / Excel / PowerPoint faylları LibreOffice ilə API image-ində çevrilir; lokal
`dotnet run` ilə işlədəndə LibreOffice yoxdursa fayl "Açılmadı" statusu alır — bu gözlənilir.
Emulyatora test faylı atmaq: faylı emulyator pəncərəsinə sürükləyin (Downloads-a düşür).

---

## 7b. Addım 3 funksiyalarını yoxlamaq

Yeni kod üçün: `docker compose up --remove-orphans -d --build api admin`, kiosk üçün bölmə 5.

**Kiosku işə salıb xidmətə açmaq (bir dəfə):**

1. Admin paneli → **Cihazlar → Token yarat** (kiosk seçin) və ya bölmə 5-dəki `docker logs` tokenini götürün.
2. Kiosku `--enrollment-token <TOKEN>` ilə başladın — kod ekranı olmadan aktivləşir.
3. **Kiosklar → kiosk → Printerlər** → printeri **Əsas et** → **Test çap**. Əmr "Uğurlu" olanda kiosk `Online` olur, ekranda "Xoş gəlmisiniz" görünür.
4. **Sərf** tabında A4 lotokunu doldurun (tutumu **Kağız** tabında təyin olunur).

**Kioskda:**

| Nəyi | Necə |
|---|---|
| Flaşdan sifariş | Flaş taxın (və ya "Flash kartdan çap et") → fayl → **Davam et** → parametrlər → **Sifariş et**. Kod və növbədəki yer göstərilir |
| Səhifə aralığı | Parametrlərdə **Aralıq** → ekran klaviaturası. Yarımçıq yazı ("1-") xəta deyil; səhv aralıq sahənin altında yazılır |
| TXT redaktəsi | `.txt` → **Redaktə et** → ekran klaviaturası (az hərfləri) → **Yadda saxla** |
| Kodla ləğv | Ana ekran → **Sifariş kodu ilə ləğv et** → 6 rəqəm → **Tap**. Növbədəki sifariş ləğv olunur, çap olunmuş yox |
| Admin menyusu | Loqoya 3 san-də 5 toxunuş → PIN (test kiosklarında `135790`) |
| Təhlükəsiz rejim | Bölmə 5, "Təhlükəsiz rejim" |

**Admin panelində:**

| Səhifə | Ünvan | Nəyi yoxlamaq |
|---|---|---|
| İdarə paneli | `/` | "Kioskların vəziyyəti": status sayları, "Diqqət tələb edir" (təhlükəsiz rejim, əlaqə yoxdur, printer seçilməyib, kağız qurtarıb/azdır) |
| Sifarişlər | `/print-jobs` | Filtr: kod, status, kiosk, tarix aralığı (`gg.aa.iiii` yazın və ya təqvimdən seçin); sətrə klik → detal |
| Sifariş detalı | `/print-jobs/:id` | Parametrlər, qiymət snapshot-u (profil dəyişəndə dəyişmir), çap cəhdləri, status tarixçəsi; növbədəki sifariş üçün **Sifarişi ləğv et** (səbəb məcburidir) |
| Kiosk → Printerlər | `/kiosks/:id` | Printer seçimi, imkanları söndürmək, **Printerləri yenilə**, **Test çap** — nəticə canlı gəlir |
| Kiosk → Növbə / Sifarişlər | `/kiosks/:id` | Kioskun canlı növbəsi və yalnız həmin kioskun sifarişləri |
| Kiosk → Sərf | `/kiosks/:id` | Lotok səviyyəsi, doldurma (Əlavə et / Tam doldur / Dəqiq say), hərəkətlər |

Operator hesabı (`dev-operator@neoprint.local`) printerləri, sifarişləri və sərfi görür, kağız doldura bilir.

Real printer və ya real cihaz tələb edən yoxlamalar (məs. çapın ortasında elektrik kəsilməsi): `docs/MANUAL_TESTS.md`.

---

## 7c. Addım 4 — ödəniş, refund, fiskal (dev əmrləri)

Yeni kod üçün: `docker compose up --remove-orphans -d --build api admin`. Aşağıdakı
`/api/dev/*` endpoint-ləri **yalnız Development-də** var (başqa mühitdə `404`),
token tələb etmir. Bütün ID-ləri admin panelindən və ya Swagger-dən götürün.

**Fiskal terminallar.** Kiosk satışının fiskal çeki yalnız həmin kioskun terminalı ilə
verilir; terminal yoxdursa çek "gözləyir" (`fiscal.noTerminal`) və 15 dəqiqədən sonra
incident açılır. Development seed-i test kiosklarına `TERM-KSK-9001` / `TERM-KSK-9002`,
onlayn terminala (tətbiq satışları və balans artırılması) `ONLINE-DEV-01` yazır — API
yenidən başlayanda boş olanları doldurur. Başqa kiosk yaratsanız: Admin paneli →
**Kiosklar → kiosk → Redaktə et → Fiskal terminal ID**. Onlayn terminalı dəyişmək:

```bash
# admin token-i ilə (Swagger-də "Authorize" və ya panelin Network tabından)
curl -X PUT http://localhost:5080/api/admin/fiscal/settings \
  -H "Authorization: Bearer <ADMIN_TOKEN>" -H "Content-Type: application/json" \
  -d '{"values":{"Fiscal.OnlineTerminalId":"ONLINE-DEV-01"}}'
```

**Bank QR ödənişi (kiosk "Bank QR" ekranında gözləyir):**

```bash
P=<paymentId>   # kiosk ekranındakı ödəniş və ya admin → Ödənişlər
curl -X POST "http://localhost:5080/api/dev/payments/$P/simulate-success"                    # ödənildi
curl -X POST "http://localhost:5080/api/dev/payments/$P/simulate-success?delaySeconds=90"    # gec gələn callback
curl -X POST "http://localhost:5080/api/dev/payments/$P/simulate-success?duplicate=true"     # dublikat callback
curl -X POST "http://localhost:5080/api/dev/payments/$P/simulate-success?dropCallback=true"  # callback itir → reconciliation (5 dəq) tapır
curl -X POST "http://localhost:5080/api/dev/payments/$P/simulate-fail"                       # bank rədd etdi
curl -X POST "http://localhost:5080/api/dev/mock-bank/create-fails?fails=true"               # bank QR yaratmır (timeout); false ilə qaytarın
curl -X POST "http://localhost:5080/api/dev/mock-card/outcome?value=Decline"                 # tətbiqdə kart: Success | Decline | Timeout
```

**Refund və fiskal uğursuzluqları:**

```bash
curl -X POST "http://localhost:5080/api/dev/mock-bank/refunds-fail?fails=true"   # bank heç bir refund qəbul etmir (false ilə qaytarın)
curl -X POST "http://localhost:5080/api/dev/refunds/<refundId>/simulate-fail"    # yalnız bu refund
curl -X POST "http://localhost:5080/api/dev/mock-fiscal/mode?mode=Down"          # e-kassa cavab vermir: Ok | Down | Reject
curl -X POST "http://localhost:5080/api/dev/fiscal/<receiptId>/simulate-fail"    # yalnız bu çek (?fails=false ilə qaytarın)
```

**"Kiosk susdu" (`Suspected`) ssenarisini tez görmək:** kiosk bir sifarişi çap edərkən
kiosku dayandırın (`tools/dev-kiosk/stop-kiosk.ps1`) və ya şəbəkəni kəsin. 15 dəqiqə
gözləməmək üçün admin → **Tənzimləmələr → Sifarişlər → `Job.SuspectedAfterMinutes` = 1**
(yoxlamadan sonra 15-ə qaytarın). Bir-iki dəqiqədən sonra sifariş `Suspected` olur,
avtomatik refund olmur, incident açılır. Kiosku yenidən başladanda o, nəticəni bildirir
və sifariş özü həll olunur.

**Müştəri müraciəti (anonim, qəbzdəki kodlarla):**

```bash
curl -X POST http://localhost:5080/api/support/claims -H "Content-Type: application/json" \
  -d '{"jobCode":"123456","kioskCode":"KSK-0001","text":"Pul çıxdı, kağız çıxmadı","contact":"+994 50 000 00 00"}'
```

PowerShell-də `curl` əvəzinə `curl.exe` yazın (və ya `Invoke-RestMethod -Method Post <URL>`).

---

## 8. Build, test, lint

```bash
# Backend (integration testlər Docker tələb edir; Docker yoxdursa skip olunur)
dotnet build backend/NeoPrint.sln
dotnet test  backend/NeoPrint.sln

# Kiosk
dotnet build desktop/NeoPrint.Kiosk.sln
dotnet test  desktop/NeoPrint.Kiosk.sln

# Mobil
cd mobile && flutter analyze && flutter test && dart format --set-exit-if-changed .
flutter build web          # lokalda; Android build CI-da (Android SDK 36 lazımdır)

# Admin
cd admin && npm run lint && npm test && npm run build
node scripts/icon-subset.mjs --check   # istifadə olunan ikonlar şriftdədirmi
node scripts/icon-subset.mjs           # yeni ikon əlavə edəndən sonra şrifti yenidən qur

# Lisenziya siyasəti (bütün ekosistemlər)
node tools/license-check --strict

# Lisenziya yoxlayıcısının öz testləri
cd tools/license-check && npm test

# Dizayn qaydaları: hardcoded rəng / şrift ölçüsü / boşluq (admin, mobil, kiosk)
node tools/design-lint --list
cd tools/design-lint && npm test
```

### İnteqrasiya testi yalnız tam dəstdə düşəndə

Test konteynerdəki API-nin loqları default olaraq atılır. `NEOPRINT_IT_LOG`
verilsə, API-nin xəbərdarlıq və xətaları həmin fayla yazılır (consumer xətaları,
MassTransit retry-ları və s.):

```bash
# bash
NEOPRINT_IT_LOG="$PWD/it-app.log" dotnet test backend/tests/NeoPrint.Api.IntegrationTests
# yalnız bir sinif
NEOPRINT_IT_LOG="$PWD/it-app.log" dotnet test backend/tests/NeoPrint.Api.IntegrationTests --filter "FullyQualifiedName~RefundTests"
```

```powershell
# PowerShell
$env:NEOPRINT_IT_LOG = "$PWD\it-app.log"; dotnet test backend/tests/NeoPrint.Api.IntegrationTests; Remove-Item Env:NEOPRINT_IT_LOG
```

Testdə ayrı ayarlarla ikinci API lazımdırsa `WithWebHostBuilder` yox,
`_fixture.WithHttpOnlyHost(...)` istifadə edin — əks halda ikinci host-un mesaj
bus-ı fixture-un mesajlarını götürüb itirir (`docs/DECISIONS.md`).

### Konvertasiya testləri (PDFium)

Səhifə sayı və preview PDFium native kitabxanasını tələb edir. Backend Docker
image-i və CI onu daşıyır; Windows maşınında yoxdursa həmin testlər **skip**
olunur (`PdfiumFact` səbəbini yazır). Lokalda da işlətmək istəsəniz:

```bash
# Linux / WSL
curl -fsSL -o /tmp/pdfium.tgz \
  "https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8066/pdfium-linux-x64.tgz"
echo "0b43f405477cf2cfc4dbff06905093c3309756c6bca1fb9da99234a2ca97fed2  /tmp/pdfium.tgz" | sha256sum -c -
tar -xzf /tmp/pdfium.tgz -C /tmp
sudo cp /tmp/lib/libpdfium.so /usr/local/lib/ && sudo ldconfig
```

Windows üçün (Git Bash):

```bash
curl -fsSL -o pdfium.tgz \
  "https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8066/pdfium-win-x64.tgz"
echo "739a57d597d864297909cc40a2411eba728490c76a0fa25e3ea299c7f6b07020  pdfium.tgz" | sha256sum -c -
mkdir -p pdfium && tar -xzf pdfium.tgz -C pdfium
cp pdfium/bin/pdfium.dll backend/tests/NeoPrint.Api.IntegrationTests/bin/Debug/net8.0/
```

`dotnet clean`-dən sonra DLL-i yenidən köçürün (bin qovluğu silinir).

LibreOffice və Ghostscript marşrutları yalnız backend image-ində işləyir; onlar
olmayanda konvertasiya faylı `Failed` edir və səbəbini yazır — sorğu xəta
atmır.

> Kiosk build-i "file is locked" xətası verirsə, işləyən kiosk tətbiqi DLL-ləri
> tutub saxlayır: `Stop-Process -Name NeoPrint.Kiosk -Force`.

---

## 9. Tez-tez lazım olan yoxlamalar

Mühit qalxmırsa əvvəlcə `tools/dev-doctor` (bölmə 1, "Problem olarsa").

```bash
# API log-u (canlı)
docker logs -f neoprint-api

# API-nin heç bir secret-i env-dən almadığını yoxla (boş çıxmalıdır)
docker inspect neoprint-api --format '{{range .Config.Env}}{{println .}}{{end}}' | grep NEOPRINT_

# Bütün servislərin sağlamlığı
docker compose ps

# Mongo-da kolleksiyalar
docker exec -it neoprint-mongo mongosh -u neoprint -p devMongoPassword \
  --authenticationDatabase admin neoprint --quiet --eval 'db.getCollectionNames()'
```
