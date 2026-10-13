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

> **Dev skriptlərin klaviatura qaydası.** `tools/` altındakı heç bir skript başqa proseslərin
> pəncərəsinə düymə göndərmir. Klaviatura simulyasiyası (skaner, mətn yazma) yalnız
> `NeoPrint.Kiosk` prosesinin pəncərəsinə gedir: pəncərə **prosesə görə** tapılır (başlığa görə yox —
> repo açıq olan redaktorun başlığında da "NeoPrint" var) və düymələrdən dərhal əvvəl ön plandakı
> pəncərənin həmin prosesə aid olduğu yoxlanır. Kiosk prosesi tapılmasa və ya ön planda başqa
> pəncərə olsa, skript **heç nə yazmadan** xəta ilə dayanır. Linux konteyner kioskunda düymələr
> konteynerin öz virtual ekranına (Xvfb) gedir, orada kioskdan başqa pəncərə yoxdur. Yeni skript
> yazılanda bu qayda məcburidir.

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
**sealed** vəziyyətdə qalxır və API secret oxuya bilmir.

Simptomlar:

- nə kiosk, nə admin paneli serverə qoşula bilmir;
- `docker compose ps`-də `neoprint-api` `Restarting` görünür;
- `docker compose logs api`-də AppRole girişi `503` alır, sonra `NEOPRINT_MONGO_URI` tapılmır.

Açmaq üçün:

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
| **Hangfire dashboard** | hələ yoxdur — `/api/admin/hangfire` (yalnız SuperAdmin) Addım 7-də gəlir (T-167) | Background işlər öz cədvəli ilə işləyir |

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

### Firebase (FCM) service account açarı

Push üçün backend `NEOPRINT_FCM_SERVICE_ACCOUNT_JSON`-u OpenBao-dan
(`neoprint/backend/providers`) alır. Boşdursa `LoggingPushSender` işləyir:
bildiriş göndərilmir, yalnız loqa "göndəriləcəkdi" yazılır (plan 2.1).

1. Firebase Console → ⚙ **Project settings** → **Service accounts** →
   **Generate new private key** (JSON yüklənir).
2. Açarı **söhbətə, issue-ya, seed faylına yapışdırmayın** — skript onu stdin ilə
   birbaşa OpenBao-ya yazır, ekranda yalnız layihə adı və hesab görünür:

```powershell
powershell -ExecutionPolicy Bypass -File tools\dev-secrets\set-fcm-key.ps1 -Path C:\Users\<siz>\Downloads\<açar>.json
Remove-Item C:\Users\<siz>\Downloads\<açar>.json
docker compose restart api
```

3. Service account-un mesaj göndərmək hüququ olmalıdır. Push `403 PERMISSION_DENIED —
   cloudmessaging.messages.create` qaytarırsa: https://console.cloud.google.com/iam-admin/iam?project=neoprint-8d6e1 →
   `firebase-adminsdk-…@….iam.gserviceaccount.com` sətrində ✏ → **Add another role** →
   **Firebase Cloud Messaging API Admin** → **Save** (1–2 dəqiqəyə tətbiq olunur, T-163).

Açar təsadüfən başqa yerə düşübsə (söhbət, ekran görüntüsü, commit): Firebase
Console → **Service accounts** → **Manage service account permissions** →
`firebase-adminsdk-…` → **Keys** tabında həmin açarı silin və yenisini yaradın.

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

Kiosklar bölməsində **Yaxınımda** brauzerdən məkan icazəsi istəyir; Chrome `localhost`-da icazə verir
(https tələb olunmur). Xəritə debug rejimində ictimai OpenStreetMap serverindən yüklənir — yalnız
development üçün (T-061).

### Android emulyator

```bash
flutter emulators                          # mövcud emulyatorlar
flutter emulators --launch <emulator_id>   # birini açın
flutter run -d emulator-5554               # və ya sadəcə: flutter run
```

Ünvan avtomatik `10.0.2.2:5080`-dir, heç nə vermək lazım deyil.

**Emulyatorda real axın (skaner, secure storage, paylaşım vərəqi).** Sənəd skaneri Google Play xidmətlərinin
içindədir, ona görə **Google Play** image-i lazımdır (`google_apis_playstore`); Google hesabı lazım deyil:

```bash
SDK="$LOCALAPPDATA/Android/Sdk"
"$SDK/cmdline-tools/latest/bin/sdkmanager.bat" "system-images/android-34/google_apis_playstore/x86_64"   # ~1,5 GB, tərəqqi yazmır
```

`avdmanager.bat` bu maşında paket adındakı `;`-i bölür və AVD yarada bilmir — AVD əl ilə yaradılır: mövcud
AVD-nin `config.ini`-si `%USERPROFILE%\.android\avd\neoprint_api34.avd\`-yə kopyalanır, orada
`image.sysdir.1 = system-images\android-34\google_apis_playstore\x86_64\`, `tag.id = google_apis_playstore`,
`PlayStore.enabled = true`, `hw.ramSize = 4096`, `hw.camera.back = virtualscene` yazılır; yanında
`neoprint_api34.ini` (`path=` həmin qovluq, `target=android-34`). Sonra:

```bash
"$SDK/emulator/emulator.exe" -avd neoprint_api34 -no-snapshot -no-boot-anim -no-audio -gpu swiftshader_indirect &
cd mobile && flutter build apk --debug --target-platform android-x64 && cd ..   # emulyator x86_64-dir; real telefon üçün --target-platform-suz
bash tools/dev-mobile/emu.sh install && bash tools/dev-mobile/emu.sh start
bash tools/dev-mobile/emu.sh shot ekran.png        # ekran görüntüsü; tap / text / key / swipe də var
bash tools/dev-mobile/emu.sh cache                 # skandan sonra burada şəkil qalmamalıdır (T-196)
bash tools/dev-mobile/emu.sh storage               # möhürlənmiş dəyərlərin adları (T-195)
# Tətbiq açılıb dərhal bağlanırsa: adb logcat -d | grep "libflutter.so" — "is for EM_AARCH64 instead of EM_X86_64"
# yazırsa APK-da x86_64 üçün libflutter.so yoxdur (APK yuxarıdakı --target-platform ilə qurulmayıb). Yenidən qurun,
# quraşdırın və köhnə çıxarılmış kitabxananı silin: adb shell run-as az.neoprint.neoprint rm -rf app_lib
adb shell wm density 480                           # ekranı 360 dp enə gətirir (1080 px / 3) — ən dar telefon; UI yoxlaması belə aparılır
adb shell wm density reset                         # əvvəlki sıxlığa (440) qaytarır
```

Bilməli olduqlarınız: (1) təzə image-də Play xidmətləri köhnədir (23.18) və skaner modulunu endirə bilmir —
tətbiq "Bu telefonda sənəd skaneri işləmir" göstərir; emulyator internetdə ~1 saat açıq qalanda Play xidmətləri
özü yenilənir və növbəti "Sənəd skanı" modulu endirir ("Downloading updates to Google Play services…").
(2) Debug build proqram GPU-su ilə çox yavaşdır: "isn't responding" dialoqunda **Wait** seçin, uzun mətni
`text` ilə hissə-hissə yazın. (3) Virtual kamera otaqdakı şahmat lövhəsini "sənəd" kimi tapır — kənar tapma və
real ölçü üçün bu kifayətdir, real kartın oxunaqlılığı üçün yox (MT-08, S24). (4) Kamera qara qalsa emulyatoru
yenidən başladın.

**Çap olunan vərəqi ölçmək** (Linux konteyner kioskunda Ghostscript və ImageMagick var):

```bash
docker exec neoprint-kshot sh -c 'F=$(ls -t /var/lib/neoprint/spool/printed/*.pdf | head -1)
  gs -q -dNOPAUSE -dBATCH -sDEVICE=pnggray -r300 -sOutputFile=/tmp/p-%d.png "$F"     # xəbərdarlıq yazmamalıdır (T-198)
  convert /tmp/p-1.png -threshold 97% -negate -define connected-components:verbose=true \
    -define connected-components:area-threshold=20000 -connected-components 8 null:'
# hər blok "EN x HÜNDÜRLÜK+x+y" pikseldir; mm = piksel × 25,4 / 300 (vəsiqə: 1011 × 638 → 85,6 × 54,0 mm)
```

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

Real telefonda `localhost` telefonun özüdür, ona görə `NEOPRINT_API_URL=http://localhost:5080/`
işləmir — kompüterin IP-si yazılır.

**Kompüterin IP-si sabit deyil.** Başqa Wi-Fi-a qoşulanda (və ya router yeni ünvan verəndə) dünən
işləyən əmr `connectionTimeout` verir, çünki köhnə ünvanda artıq heç kim yoxdur. Əvvəlcə cari ünvana
baxın:

```powershell
Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp | Select-Object IPAddress, InterfaceAlias
```

**IP-dən asılı olmayan yol (USB):** portu telefona yönləndirin və ünvan kimi `127.0.0.1` verin —
Wi-Fi və firewall heç bir rol oynamır:

```powershell
adb -s <telefon_id> reverse tcp:5080 tcp:5080
adb -s <telefon_id> reverse --list          # "tcp:5080 tcp:5080" görünməlidir
flutter run -d <telefon_id> --dart-define=NEOPRINT_API_URL=http://127.0.0.1:5080
```

`adb reverse` yalnız `127.0.0.1` ünvanına təsir edir: `--dart-define`-də LAN IP-si qalırsa, yönləndirmə
istifadə olunmur. Kabel çıxarılanda və ya `adb` yenidən başlayanda yönləndirmə silinir — əmri təkrar edin.

**"Android sdkmanager did not install NDK 28.2.13676358"** ilə build dayanırsa:
cmdline-tools 23-ün köhnəlmiş `sdkmanager`-i paket adını (`ndk;…`) `;`-də bölür və
tapmır. NDK-nı yeni `android` CLI ilə bir dəfə quraşdırın (~2 GB):

```powershell
& "$env:LOCALAPPDATA\Android\sdk\cmdline-tools\latest\bin\android.exe" sdk install ndk/28.2.13676358
```

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

### Kiosk telemetriyası

Kiosk hər 30 saniyədə öz vəziyyətini göndərir (plan 7.9, T-206). Admin panelinin oxuduğu
ilə eyni məlumata terminaldan baxmaq:

```bash
node tools/dev-kiosk/telemetry.mjs             # KSK-9002 — Linux konteyner kioskunun son hesabatı + son 1 saatın nöqtələri
node tools/dev-kiosk/telemetry.mjs KSK-0001    # başqa kiosk, kodu ilə

# Uzaqdan əmrlər (plan 7.8) — panelin düymələrinin etdiyi; dev-admin kimi göndərir və son əmrləri göstərir
node tools/dev-kiosk/command.mjs pause         # KSK-9002: yeni sifariş yox, ekranda "Texniki xidmət"
node tools/dev-kiosk/command.mjs resume
node tools/dev-kiosk/command.mjs restart-app   # konteynerdə kiosk prosesi PID 1-dir: konteyner dayanır → docker start neoprint-kshot
node tools/dev-kiosk/command.mjs reboot        # konteyner kompüteri yenidən yükləyə bilmir: əmr "Failed — kompüter yenidən yüklənmədi" olur
node tools/dev-kiosk/command.mjs list          # son 8 əmr və nəticələri

docker exec neoprint-redis redis-cli -a devRedisPassword --no-auth-warning --scan --pattern 'kiosk:status:*'                                   # Redis-dəki son snapshot-lar
docker exec neoprint-mongo mongosh -u neoprint -p devMongoPassword --authenticationDatabase admin neoprint --quiet --eval "db.kiosk_telemetry.countDocuments()"   # tarixçədəki sətirlər
```

Eyni skript printerləri də göstərir (plan 7.10, T-207): kimlik, vəziyyət və səbəblər, sərf
materialları, lotoklar, sayğac, imkanlar, sistem növbəsi və son 5 dəyişiklik. Konteyner kioskunda
yalnız saxta printer var (CUPS quraşdırılmayıb) — o özü haqqında cavab verir, real printerin
sahələri "the printer does not report this" kimi çıxır.

**Həqiqi IPP cavabı almaq** (parser-i yoxlamaq üçün; real printer olmadan): CUPS-un
`ippeveprinter` simulyatoru müvəqqəti konteynerdə —

```bash
MSYS_NO_PATHCONV=1 docker run --rm debian:bookworm-slim sh -c 'apt-get update -qq && apt-get install -y -qq --no-install-recommends cups-ipp-utils avahi-daemon dbus >/dev/null
  mkdir -p /run/dbus /tmp/spool; dbus-daemon --system --fork; avahi-daemon -D --no-drop-root --no-chroot; sleep 2
  (ippeveprinter -p 8631 -d /tmp/spool -m "NeoPrint Test Laser 4000" -f application/pdf,image/jpeg -2 -s 30,25 "Sim Printer" &) ; sleep 5
  ipptool -T 10 -t -v ipp://localhost:8631/ipp/print get-printer-attributes.test'
```

(`ippeveprinter` DNS-SD-siz başlamır, ona görə `dbus` və `avahi-daemon` da qaldırılır.) Bu cavab
`PrinterTelemetryParserTests.A_real_ipp_everywhere_printer_answer_is_read_whole`-da saxlanılıb.

Intervalı dəyişmək: admin panel → Tənzimləmələr → `Kiosk.TelemetrySeconds` (kiosk yeni dəyəri
konfiqurasiyası ilə alır). Hədd incident-ləri (disk < 2 GB, yaddaş > 90% 5 dəq, backend > 2000 ms
5 dəq, SignalR 2 dəq qopuq) `GET /api/admin/incidents?type=DiskLow` ilə görünür.

### Konvertasiya — izolyasiya olunmuş `converter` konteyneri

İstifadəçi faylını açan **hər şey** API-də yox, `neoprint-converter`
konteynerində işləyir (şəbəkəsiz, read-only, CPU/RAM limitli —
`docs/adr/ADR-0005-isolated-converter.md`): Word/Excel/PowerPoint/PS/PCL/XPS
konvertasiyası və Addım 7A-dan (T-051) bəri PDF-in səhifə sayı, önizləmə
şəkilləri, şəkil → PDF, səhifə / şəkil redaktəsi, skan və tərtibatın PDF-ə
çəkilməsi. Sonuncuları converter aləti (`NeoPrint.Converter.dll`) edir — hər
fayl üçün bir qısa proses. Portu yoxdur.

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
Önizləmə, redaktə və tərtibat isə gözləmir: dərhal `503` və "Sənəd emalı xidməti
hazırda işləmir… bir az sonra yenidən cəhd edin" cavabı gəlir.

**Hamısını bir əmrlə yoxlamaq (T-051):** skript `dev-user` kimi hər format
qrupundan bir fayl yükləyir (PDF, TXT, RTF, CSV, ODP, PNG, BMP, PS — özü yaradır),
`Ready` olmasını və səhifə şəklini gözləyir, səhifə və şəkil redaktəsi, tərtibat və
skan edir, zədəli PDF-in izahla `Failed` olduğuna baxır, sonra API prosesinin
yaddaş xəritəsində (`/proc/1/maps`) PDFium / SkiaSharp / PDFsharp olmadığını və altı
alət əməliyyatının converter-in log-unda olduğunu yoxlayır. Yaratdığı faylları silir.

```bash
node tools/dev-converter/check.mjs            # 25 yoxlama, ~1 dəqiqə (sonuncusu: bir səhifəyə 6 sorğu → converter-də 1 proses)
node tools/dev-converter/check.mjs --down     # + converter-i dayandırır: 503, health Degraded, sonra yenidən qaldırır (+6 yoxlama)
node tools/dev-converter/check.mjs --load     # + eyni anda 100 önizləmə: kim çəkilir, kim 503 "məşğul" alır (+6 yoxlama, ~1,5 dəqiqə artıq)
```

Əl ilə:

```bash
docker exec neoprint-api sh -c 'grep -c -i -E "libpdfium|libSkiaSharp|PdfSharp|NeoPrint.Converter" /proc/1/maps'   # 0 olmalıdır
docker exec neoprint-converter sh -c 'ls /app/NeoPrint.Converter.dll'                                              # alət image-dədir
docker logs --since 5m neoprint-converter | grep -o 'with [a-z-]*: [A-Za-z]* in [0-9.]* s'                         # hər iş: əməliyyat, nəticə, müddət

docker stop neoprint-converter      # 20 saniyədən sonra: önizləmə / redaktə → 503 files.processingUnavailable
docker start neoprint-converter     # eyni sorğu yenidən işləyir
```

`--load` bir dəqiqə gözləyir (bir çağıranın dəqiqədə 120 sorğu həddi təzələnsin), sonra 50
səhifəlik PDF-in 100 müxtəlif şəklini eyni anda istəyir və yoxlayır: hər cavab ya şəkildir, ya
`503 files.processingBusy` + `Retry-After: 3`; heç kim asılı qalmır; konteynerdə eyni anda
3-dən çox alət prosesi yoxdur; rədd olunan sorğu təkrar göndəriləndə işləyir.

Hədlər konfiqurasiyadan dəyişir (compose-da `api` servisinin mühit dəyişəni kimi, məs.
`Conversion__InteractiveQueueLength=16`): `Conversion:InteractiveParallel` (3),
`InteractiveQueueLength` (48), `InteractiveQueueSeconds` (10), `PreviewCacheMegabytes`
(64; 0 keşi söndürür), `PreviewCacheMinutes` (10).

**M3-dən keçid (T-222 … T-224):** `docker compose up -d --build api converter` və
`bash tools/dev-kiosk/update-linux-kiosk.sh`. Yeni `printer_events` kolleksiyası və onun TTL
indeksi API başlayanda özü yaranır; compose faylında və seed-də dəyişiklik yoxdur.

**Addım 6-dan keçid (T-051):** kod yeniləndikdən sonra image-i yenidən qurun —
`docker compose up -d --build api converter`. Hər iki konteyner eyni image-dəndir;
biri köhnə qalsa önizləmə `503` verər (köhnə converter `neoprint-tool` işini rədd edir).
Compose faylında, seed-də və konfiqurasiyada dəyişiklik yoxdur.

Docker-siz `dotnet run` ilə API-ni işlədəndə konvertasiya lokal child prosesdə gedir
(`Conversion:Mode=Local`, yalnız Development-də icazəlidir). Converter aləti də
belədir: API onu `dotnet NeoPrint.Converter.dll` kimi ayrıca proses kimi işə salır —
bu rejimdə də PDFium / SkiaSharp API prosesinə yüklənmir.

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

**Qəbz (PDF) və email:** hər ödənişdən sonra qəbz avtomatik yaranır (`NP-2026-000001`).
Kiosk qəbzi email-ə göndərəndə (və ya tətbiqdə "Email-ə göndər") məktub PDF əlavəsi ilə
**Mailpit**-ə düşür: http://localhost:8025. E-kassa "Down" rejimindədirsə PDF-də "Fiskal çek
hazırlanır" yazılır; rejimi `Ok` edib çeki təkrarlayanda (admin → Fiskal çeklər → Təkrarla)
yenilənmiş qəbz eyni ünvana ikinci məktub kimi gəlir. Admin tokeni ilə PDF-i birbaşa yükləmək:

```bash
curl -H "Authorization: Bearer <ADMIN_TOKEN>" -o qebz.pdf http://localhost:5080/api/admin/receipts/<paymentId>/pdf
```

**Müştəri müraciəti (anonim, qəbzdəki kodlarla):**

```bash
curl -X POST http://localhost:5080/api/support/claims -H "Content-Type: application/json" \
  -d '{"jobCode":"123456","kioskCode":"KSK-0001","text":"Pul çıxdı, kağız çıxmadı","contact":"+994 50 000 00 00"}'
```

**Kioskda ödəniş axını.** Sifariş "Sifarişi təsdiqlə"dən sonra **Ödəniş** addımına keçir:
"Bank kartı və ya bank tətbiqi" → bank QR-i (yuxarıdakı `simulate-success` ilə ödəyin),
"NeoPrint tətbiqi" → tətbiqin QR-i (mobil tətbiqdə "QR oxut"). Ödəniş gələn kimi kiosk
"Ödəniş uğurlu" ekranını, fiskal çekin QR-ini və email klaviaturasını göstərir; sifariş
yalnız bundan sonra çap növbəsinə düşür. Ödəniş vaxtı (5 dəqiqə) bitəndə "Ödəniş vaxtı
bitdi" ekranı çıxır. Promokod qiymət kartındakı **Promokod** düyməsi ilə yazılır.
Development-də ödənişsiz axın (`Kiosk:SkipPayment`) söndürülüb — hər sifariş ödənilməlidir. Addım 3-dən qalan
lokal `appsettings.Development.json` dəyişikliyiniz varsa, `"SkipPayment": false` olduğunu yoxlayın.

**Mobil tətbiqlə ödəniş.** Kioskda "NeoPrint tətbiqi" seçin — ekranda `neoprint://pay/<paymentId>`
QR-ı çıxır.

- *Android telefon / emulyator:* ana səhifədə **QR oxut** → QR-ı kameraya göstərin (emulyatorda
  kamera kimi kompüterin veb-kamerası seçilə bilər: AVD → Advanced → Camera). Telefonun öz kamerası
  QR-ı oxusa da tətbiq `neoprint://pay` linki ilə açılır.
- *Chrome (Flutter web):* skaner yoxdur; ödənişi URL ilə açın — `paymentId` kiosk ekranındakı QR-ın
  sonundakı GUID-dir (və ya admin → Ödənişlər):

  ```text
  http://localhost:<flutter portu>/#/pay/<paymentId>
  ```

- Balansı test üçün artırmaq: tətbiqdə **Balans → Balansı artır** (mock kart həmişə uğurlu; rədd
  ssenarisi üçün `curl -X POST "http://localhost:5080/api/dev/mock-card/outcome?value=Decline"`).
- Ödənişdən sonra sifariş ana səhifədə "Aktiv çaplar"-da görünür, statusu `/hubs/user` ilə özü
  yenilənir; qəbz **Profil → Qəbzlər**-də, "Problem barədə bildir" mesajları **Profil → Müraciətlərim**-dədir.

**Qaytarmanın statusu səhifəsi (QR).** Çap alınmayanda kiosk refund kartını və QR-i
göstərir; QR bu səhifəni açır:

```text
http://localhost:5080/refund/<refundId>          # dil: ?lang=az | en | ru
```

Səhifədəki "Problem barədə bildir" forması `POST /api/refunds/<refundId>/claim` göndərir
(rate limit: `RateLimit.SupportClaimPerHour`). QR-dəki ünvanın əsası
**Tənzimləmələr → Public → `Public.SiteUrl`**-dir (development seed-i
`http://localhost:5080`, produksiyada `https://neoprint.az`). Telefondan açmaq üçün onu
kompüterin LAN ünvanına dəyişin, məs. `http://192.168.1.50:5080`.

**Fləşkadan gələn fayl tətbiqlə ödəniləndə** kiosk faylı şifrəli spool-da saxlayır və
serverə yükləyir (`POST /api/print-jobs/<jobId>/flash-upload`, kiosk tokeni ilə) ki, fayl
müştərinin hesabında qalsın. Şəbəkə yoxdursa kiosk hər 2 dəqiqədən bir yenidən cəhd edir.

**Printer xətasını simulyasiya etmək (fake printer, T-123).** Fake printerin çıxış qovluğunda
(`<spool>/printed`) iki fayl işləyir:

- Windows: `%ProgramData%\NeoPrint\spool\printed`;
- Linux: `/var/lib/neoprint/spool/printed`.

| Fayl (məzmunu vəziyyət adıdır) | Nə edir | Nəticə |
|---|---|---|
| `printer-state.txt` — `PaperJam`, `OutOfPaper`, `Offline` | Printer **çapdan əvvəl** xətadadır; printer telemetriyası da bunu göndərir (`stopped`, `media-jam` və s.) | Sifariş növbədə gözləyir. **30 saniyədən sonra** (`Kiosk.PrinterFaultAfterSeconds`) kiosk `Paused` olur, incident (Critical) açılır, **qonağın gözləyən bank ödənişi karta qaytarılır**, qeydiyyatlı istifadəçiyə seçim təklif olunur (Bölmə 7m). Faylı silin → kiosk özü `Online` olur, qalan sifarişlər çap olunur. Bir dəqiqə sonra `node tools/dev-kiosk/telemetry.mjs` hadisələrdə `JamStarted` / `JamEnded` göstərir |
| `printer-fail-next.txt` — `PaperJam` və s. | **Növbəti** iş printerdə qırılır, fayl özü silinir (birdəfəlik) | Ssenari A: iş `Interrupted` → avtomatik karta qaytarma, kioskda "Çap tamamlanmadı" |

```bash
# Linux kiosk (konteyner və ya cihaz)
echo PaperJam > /var/lib/neoprint/spool/printed/printer-fail-next.txt
```

```powershell
# Windows kiosk
Set-Content "$env:ProgramData\NeoPrint\spool\printed\printer-fail-next.txt" PaperJam
```

PowerShell-də `curl` əvəzinə `curl.exe` yazın (və ya `Invoke-RestMethod -Method Post <URL>`).

---

## 7d. Addım 5 — şəxsi QR ilə çap (plan 9.4)

1. Tətbiqdə (Chrome və ya telefon) **Fayllarım → faylın menyusu → Kiosk üçün hazırla**.
   Kiosk seçmədən **Hazırla** — ödənişsiz hazırlıq (istənilən kioskda); kiosk seçib
   **Əvvəlcədən ödə** — həmin kioskda gözləyən ödənilmiş sifariş. Siyahı: Ana səhifə → **Hazırladıqlarım**.
2. Kioskda QR oxutmaq. Kiosk USB skanerin yazdığı sətri qəbul edir (simvollar arası < 50 ms, sonunda Enter və ya LF):
   - **Real skaner** (USB HID, klaviatura rejimi): tətbiqdə **Mənim QR kodum** ekranını skanerə tutun.
   - **Windows dev kiosk** (`start-kiosk.ps1` ilə işləyir) — QR-ı skaner kimi yazır:

     ```powershell
     powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\scan-user-qr.ps1
     # başqa istifadəçi: -Email ... -Password ...
     ```

     Skript düymələri yalnız `NeoPrint.Kiosk` prosesinin pəncərəsinə göndərir (yuxarıdakı klaviatura qaydası):
     kiosk işləmirsə və ya yazmadan dərhal əvvəl ön planda başqa pəncərə varsa, heç nə yazmadan dayanır.
   - **Linux konteyner kiosku** (X11, pəncərə meneceri yoxdur): əvvəl pəncərəyə fokus, sonra kod və sətir sonu
     **bir** `xdotool type` çağırışında (ayrı `xdotool key Return` 50 ms-dən gec gəlir və rədd olunur).
     `CODE` — `GET /api/users/me/qr` cavabındakı `code` (60 saniyə keçərlidir). Kiosk prosesi və ya
     pəncərəsi yoxdursa, əmr heç nə yazmır:

     ```bash
     docker exec -e CODE="$CODE" neoprint-kshot sh -c '
       pidof dotnet >/dev/null || { echo "Kiosk işləmir — heç nə yazılmadı"; exit 1; }
       W=$(xdotool search --name "^NeoPrint$" | head -1)
       [ -n "$W" ] || { echo "Kiosk pəncərəsi yoxdur — heç nə yazılmadı"; exit 1; }
       xdotool windowfocus --sync "$W" && xdotool type --delay 3 "$CODE
     "'
     ```

3. Kioskda ekran 10 açılır: ödənişsizdə **Seç** → parametrlər (tətbiqdə seçilənlər qoyulur) → kioskda ödəniş;
   bu kiosk üçün ödənilmişdə **Çapa ver** → birbaşa növbə. Siyahı 60 saniyə toxunulmasa bağlanır.
4. Götürülməyən ödənilmiş sifariş `PreparedPrint.AutoCancelDays` (7) gündən sonra saatlıq `uncollected-print-jobs`
   Hangfire işi ilə ləğv olunur, pul balansa qayıdır (növbəti saat başında; Hangfire paneli Addım 7-də — T-167). İş
   vaxtı gəlməmiş sifarişə toxunmur.

---

## 7e. Addım 5 — planlanmış çap (plan 9.5)

1. Tətbiqdə **Fayllarım → faylın menyusu → Çapı planla** və ya **Planlanmışlar → Yeni planlama**: kiosk, parametrlər,
   tarix və saat (ən tez 15 dəqiqə, ən gec `Schedule.MaxDaysAhead` = 30 gün sonra), sonra ödəniş.
2. Vaxtın çatmasını gözləmədən yoxlamaq üçün planlamanın vaxtını keçmişə çəkin. Dəqiqəlik `scheduled-print-due` işi onu
   `AwaitingUser` edir, tətbiq açıqdırsa "Planlanmış çapın vaxtı çatıb" dialoqu çıxır:

   ```bash
   docker exec neoprint-mongo mongosh --quiet "mongodb://neoprint:devMongoPassword@localhost:27017/neoprint?authSource=admin&directConnection=true" \
     --eval "db.scheduled_prints.updateOne({_id: UUID('<schedule-id>')}, {\$set: {ScheduledAt: new Date(Date.now() - 60000)}})"
   ```

   `<schedule-id>` — `GET /api/schedules` cavabındakı `id`.
3. Avtomatik ləğvi yoxlamaq üçün `AwaitingUser` planlamanın `AwaitingSince` sahəsini 25 saat geri çəkin
   (`{$set: {AwaitingSince: new Date(Date.now() - 25*3600000)}}`). 10 dəqiqəlik `scheduled-print-auto-cancel` işi onu
   `Expired` edir, pul balansa qayıdır (ən çox 10 dəqiqə gözləyin; Hangfire paneli Addım 7-də — T-167).
4. Aktiv planlanmış çapı olan fayl silinmir. Planlamanı ləğv edəndən sonra fayl silinə bilər.

---

## 7f. Addım 5 — bildirişlər və push (T-146)

**FCM açarı olmadan (default).** Backend `LoggingPushSender` işlədir: bildiriş siyahıya düşür, tətbiq açıqdırsa banner
çıxır, push isə yalnız loqa yazılır. Tətbiq giriş edəndə cihazı qeydiyyatdan keçirir (web-də VAPID açarı verilməyibsə
keçirmir). Hansı göndəricinin işlədiyi API-nin başlanğıc loqundadır:

```bash
docker compose logs api | grep "Push:"                  # "FCM HTTP v1, project ..." və ya "FCM not configured"
docker compose logs -f api | grep -E "Push not sent|Notification .* delivered|FCM"
```

Bildiriş yaratmaq üçün real axınlar: sifarişi çap edin (`JobCompleted`), planlanmış çapın vaxtını keçmişə çəkin
(7e, `ScheduleDue`) və ya `AwaitingSince`-i geri çəkin (`ScheduleAutoCancelled`), kart / balans refund-u
(`RefundCompleted`). Bildirişlər: ana səhifənin başlığındakı zəng.

**Real FCM ilə.** Açarı OpenBao-ya yazın (yuxarıda "Firebase (FCM) service account açarı"), `docker compose restart api`.
Sonra:

```bash
# Android telefon (USB, eyni Wi-Fi; "Real telefon" bölməsi):
cd mobile && flutter run -d <telefon_id> --dart-define=NEOPRINT_API_URL=http://192.168.1.50:5080

# Web (Chrome): VAPID public açarı — Firebase Console → Project settings → Cloud Messaging → Web Push certificates
cd mobile && flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/ --dart-define=NEOPRINT_FCM_VAPID_KEY=<açar>
```

Brauzer bildiriş icazəsini soruşur. Push yalnız tətbiq **arxa planda** olanda sistem bildirişi kimi görünür; ön planda
tətbiqin öz banneri çıxır. Tətbiqi bağlayıb (web-də tabı başqa taba keçirib) yuxarıdakı axınlardan birini edin.

**FlutterFire CLI-ni yenidən işlətsəniz** (`flutterfire configure`): yalnız `lib/firebase_options.dart` saxlanılır.
CLI-nin `android/settings.gradle.kts` və `android/app/build.gradle.kts`-ə əlavə etdiyi `com.google.gms.google-services`
sətirlərini silin — tətbiq onlarsız işləyir, CI isə onlarla `google-services.json` axtarıb düşür.

---

## 7g. Addım 5 — məxfilik (T-147)

Mongo əmrləri üçün qısa yol (PowerShell-də `$mongo`, bash-da `mongo` funksiyası kimi saxlaya bilərsiniz):

```bash
docker exec neoprint-mongo mongosh --quiet "mongodb://neoprint:devMongoPassword@localhost:27017/neoprint?authSource=admin&directConnection=true" --eval "<əmr>"
```

**Faylın avtomatik silinməsi.** Tətbiqdə faylın menyusu → **Avtomatik silinmə**. "Çapdan sonra" fayl, onunla verilmiş
sifariş kioskda çap olunan kimi silinir (açıq sifariş / planlanmış çap varsa sonuncudan sonra). "N gün sonra"nı
gözləmədən yoxlamaq üçün yükləmə vaxtını geri çəkin, 5 dəqiqəlik `file-auto-delete` işi silir:

```js
db.files.updateOne({_id: UUID('<fayl-id>')}, {$set: {CreatedAt: new Date(Date.now() - 2*86400000)}})
```

**Məlumatlarımı yüklə.** Profil → **Məlumatlarımı yüklə** → **Arxiv hazırla**. Hazır olanda bildiriş gəlir; **Yüklə**
linki brauzerdə açır (24 saat). Vaxtı keçmiş arxivi saatlıq `data-export-cleanup` işi silir; gözləmədən:
`db.data_exports.updateOne({_id: UUID('<id>')}, {$set: {ExpiresAt: new Date()}})`, sonra növbəti saat başında iş
arxivi silir (Hangfire paneli Addım 7-də — T-167).

**Hesabımı sil.** Test üçün **ayrıca hesab** yaradın (`dev-user`-i silməyin — onu silsəniz API-nin növbəti
Development başlanğıcı yenisini yaradır, amma köhnə sifarişlər anonim hesaba qalır). Profil → **Hesabımı sil** →
şifrə + `SİL`. Faylların fiziki silinməsi `Privacy.AccountDeletionGraceHours` (24) sonradır; gözləmədən:

```js
db.users.updateOne({Email: /^deleted-/, ErasedAt: null}, {$set: {DeletedAt: new Date(Date.now() - 25*3600000)}})
```

10 dəqiqəlik `account-deletion` işi (ən çox 10 dəqiqə gözləyin) faylları bütün provayderlərdən silir və `ErasedAt` yazır.

## 7h. Addım 5 — mobil feature flag-lar (T-148)

Admin paneli → **Feature flag-lar** (`http://localhost:8080/features`): flag-ı söndürün (hamı üçün) və ya yalnız bir
kiosk üçün override qoyun (pilot). Dəyişiklik dərhal tətbiq olunur. Tətbiq flag-ları girişdə, ön plana qayıdanda və
server "funksiya bağlıdır" deyəndə yenidən oxuyur — brauzerdə səhifəni yeniləmək kifayətdir.

Söndürüləndə nə gizlənir və nə qalır — `docs/API.md` → "Söndürülmüş flag". İstifadəçi üçün hazırkı qiymətlər:

```bash
curl -s http://localhost:5080/api/features -H "Authorization: Bearer <istifadəçi tokeni>"
```

Yoxladıqdan sonra flag-ları geri açmağı unutmayın — dev mühitində bütün hesablar üçün ortaqdır.

## 7j. Linux konteyner kioskunu (KSK-9002) yeni kodla yeniləmək

Kiosk kodu dəyişəndən sonra konteyner kioskunu (`neoprint-kshot`) yeniləmək — publish, `/opt/neoprint`-ə köçürmə, restart
(aktivləşdirmə və spool `neoprint-kshot-data` həcmində qalır; skript sonda yeni `NeoPrint.Kiosk.dll`-in tarixini göstərir):

```bash
bash tools/dev-kiosk/update-linux-kiosk.sh
```

**Konteyneri image-dən yenidən qurmaq** (ilk dəfə, image dəyişəndə və ya konteyner silinibsə). Image
`tools/dev-kiosk/linux-container/Dockerfile`-dadır: Xvfb, xdotool, ImageMagick və real kiosk kimi **LibreOffice,
Ghostscript, fontlar** — Office faylları (DOCX / XLSX / PPTX) flaşdan açılır (T-168). Aktivləşdirmə
`neoprint-kshot-data` həcmində qalır; ilk dəfə enrollment token verin (bölmə 5):

```bash
bash tools/dev-kiosk/build-linux-kiosk.sh                                # mövcud aktivləşdirmə ilə
bash tools/dev-kiosk/build-linux-kiosk.sh --enrollment-token <TOKEN>     # ilk aktivləşdirmə
```

Skript sonda `soffice` və `gs` versiyalarını və "kiosk running" yazır. Konteynerdə konvertorlar tətbiqin öz
hesabı ilə `prlimit` altında işləyir; ayrıca şəbəkəsiz `neoprint-convert` hesabı yalnız provisioning-dədir (real cihaz — T-094).

**Kiosk və feature flag-lar (T-160).** Admin → Feature flag-lar → `mobile_payment_at_kiosk` / `promo_codes`-də
KSK-9002 üçün override "söndürülüb" — kioskun açıq ekranı dərhal dəyişir (ödəniş ekranında yalnız bank QR-ı,
parametrlərdə "Promokod" yoxdur). Yoxladıqdan sonra override-ı silin.

## 7i. Addım 5 — dil və hüquqi sənədlər (T-152)

Profil → **Dil**: seçim dərhal tətbiq olunur və hesabda saxlanılır (`PUT /api/users/me`). Hesabın hazırkı dili:

```js
db.users.findOne({Email: '<email>'}, {Language: 1, 'FcmTokens.Language': 1})
```

Web build-də giriş saxlanmır (səhifə yenilənəndə yenidən daxil olursunuz) — hər girişdə tətbiq hesabın dilini götürür,
bu, "başqa telefonda giriş"i yoxlamağın ən asan yoludur. Profil → **Hüquqi sənədlər** və qeydiyyat ekranında **Oxu**
admin panelində dərc olunmuş cari versiyanı göstərir (Admin → Hüquqi sənədlər); en/ru versiyası dərc olunmayıbsa
Azərbaycan mətni və "hələ yalnız Azərbaycan dilindədir" qeydi görünür.

**Köhnə bazada qaralamanın sonunda "Tam mətn: docs/legal/…" görünürsə (T-161).** Seed yalnız sənəd yoxdursa yazır, ona
görə 2026-10-04-dən əvvəl yaradılmış lokal bazada köhnə mətn qalır. Bir dəfə işlədin:

```js
db.legal_documents.find({Content: /Tam mətn: docs\/legal\//}).forEach(d => db.legal_documents.updateOne({_id: d._id}, {$set: {Content: d.Content.replace(/\n*Tam mətn: docs\/legal\/[A-Z_]+\.az\.md\n*$/, "\n")}}))
```

---

## 7k. Addım 6 — fayl paylaşımı (T-181)

Yeni kod üçün: `docker compose up --remove-orphans -d --build api`. İki hesab lazımdır: `dev-user@neoprint.local` və
tətbiqdə qeydiyyatdan keçən ikinci hesab (kod Mailpit-də — bölmə 4, "Mobil tətbiq — yeni hesab").

1. Alıcı: **Fayllarım → Paylaşılanlar** (app bar-da qutu ikonu) — "Sizin ID-niz" (`NP-XXXXXX`), **Kopyala** / **QR kodum**.
2. Göndərən: **Fayllarım → faylın menyusu → Paylaş** → alıcının ID-si (telefonda QR ikonu ilə alıcının QR-ını oxuyun) →
   **Göndər**. Həssas faylda əvvəl xəbərdarlıq və təsdiq dialoqu.
3. Alıcıda bildiriş "Sizə fayl göndərildi", **Paylaşılanlar → Alınanlar**-da "Yeni"; faylı açıb səhifələrinə baxın →
   **Fayllarıma saxla** — nüsxə "Paylaşılıb" nişanı ilə Fayllarım-dadır.
4. "Fayl artıq yoxdur" / "Dəyişdirilib": göndərən faylı silsin və ya redaktə etsin (alıcı saxlamamışdan əvvəl) —
   alıcıda status dəyişir, saxlamaq olmur.

Linklər: `neoprint://shares` (Paylaşılanlar), `neoprint://share/NP-XXXXXX` (Faylı paylaş, alıcı doldurulmuş). API:
`docs/API.md` → "Fayl paylaşımı". Feature flag: Admin → Feature flag-lar → `file_sharing` (söndürüləndə yalnız yeni
göndərmə bağlanır).

---

## 7l. Addım 6 — referral (T-182)

Yeni kod üçün: `docker compose up --remove-orphans -d --build api`. Tənzimləmələr (Admin → Tənzimləmələr):
`Referral.BonusPages` (10), `Referral.InviteeBonusPages` (0), `Referral.MaxRewardsPerMonth` (20; 0 = limitsiz),
`Referral.MaxBonusPagesPerJob` (5).

1. Dəvət edənin kodu: `GET /api/referrals/me` (`code`, `link`, statistika), QR — `GET /api/referrals/me/qr.png`.

   ```bash
   T=$(curl -s -X POST http://localhost:5080/api/auth/login -H "Content-Type: application/json" \
     -d '{"email":"dev-user@neoprint.local","password":"DevTest!2026","deviceId":"dev-shell"}' | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4)
   curl -s http://localhost:5080/api/referrals/me -H "Authorization: Bearer $T"
   ```

2. Yeni hesab bu kodla qeydiyyatdan keçir (tətbiqdə qeydiyyat ekranı, kod sahəsi; təsdiq kodu Mailpit-də) — **başqa
   cihazdan / brauzer profilindən**: dəvət edənin cihazı ilə eyni `deviceId` olsa bonus yazılmır (`Hold = SameDevice`).
3. Yeni hesab **pulla** (balans / kart; yalnız bonus səhifə sayılmır) bir çap ödəyir və kiosk onu çap edir — məsələn
   **Çapı planla** → kiosk `KSK-9002` (konteyner kiosku işləməlidir, bölmə 7j) → kartla ödə → **Çapa ver**.
4. Çap `Completed` olan kimi dəvət edənin balansında +`Referral.BonusPages` bonus səhifə və "Dəvət bonusu qazandınız"
   bildirişi; `GET /api/referrals/me`-də `rewarded` artır. Vəziyyəti bazada görmək:

   ```bash
   docker exec neoprint-mongo mongosh -u neoprint -p devMongoPassword --authenticationDatabase admin neoprint --quiet \
     --eval 'db.referrals.find({}, {Status:1, Hold:1, QualifiedAt:1, InviterBonusPages:1}).sort({CreatedAt:-1}).limit(5).forEach(printjson)'
   ```

   `Status: "Registered"` + `Hold` (`SameDevice` / `MonthlyLimit` / `InviterInactive`) — ilk ödənişli çap oldu, amma bonus
   yazılmadı; `QualifiedAt` yoxdursa ilk ödənişli çap hələ gözlənilir.
5. Admin paneli: **Referral** (http://localhost:8080/referrals, `referrals.view` — `dev-admin`, `dev-superadmin`): rəqəmlər, ən çox
   dəvət edənlər, tarixçə. **Müştərilər → müştəri**: "Referral" və "Fayllar" kartları (fayl adları maskalanmış gəlir).
   Yeni kod üçün `docker compose up --remove-orphans -d --build api admin`.

---

## 7m. Addım 7A — kiosk dayananda: incident-lər, qaytarma, bərpa (plan 9.6; T-209)

Linux konteyner kioskunda (KSK-9002, fake printer) plan 9.6-nın hər iki ssenarisi əllə keçilir. Printerin
vəziyyəti Bölmə 7c-dəki iki faylla idarə olunur (`printer-state.txt`, `printer-fail-next.txt`).

**Alətlər** (compose stack qalxmış olmalıdır; asılılıq yoxdur, Node 20+):

```bash
node tools/dev-kiosk/incidents.mjs                       # KSK-9002: status və açıq incident-lər
node tools/dev-kiosk/incidents.mjs all                   # bağlananlar da (necə bağlandığı ilə)
node tools/dev-kiosk/incidents.mjs show <incidentId>     # toxunduğu sifarişlər, ödənişlər, refund-lar
node tools/dev-kiosk/incidents.mjs ack <incidentId>
node tools/dev-kiosk/incidents.mjs resolve <incidentId> "nə edildi"

node tools/dev-kiosk/app-order.mjs                       # dev-user: ilk hazır faylı KSK-9002-də sifariş edir, balansdan ödəyir
node tools/dev-kiosk/app-order.mjs list                  # sifarişlərim: status, "can cancel", "KIOSK STOPPED"
node tools/dev-kiosk/app-order.mjs cancel <jobId>        # "Ləğv et və məbləği qaytar"
node tools/dev-kiosk/app-order.mjs notifications         # son bildirişlər (refund təklifi)
```

Giriş endpoint-i dəqiqədə 10 sorğu qəbul edir — alətləri dalbadal çox işlətsəniz "Too many sign-ins"
yazır; bir dəqiqə gözləyin.

**Ssenari A, printer çapdan əvvəl dayanır** (qeydiyyatlı istifadəçinin sifarişi gözləyir):

```bash
docker exec neoprint-kshot sh -c 'echo OutOfPaper > /var/lib/neoprint/spool/printed/printer-state.txt'
node tools/dev-kiosk/app-order.mjs          # 30 saniyə ərzində: kiosk hələ Online-dır, sifariş qəbul olunur
# ~40 saniyə sonra:
node tools/dev-kiosk/incidents.mjs          # KSK-9002 is Paused; PaperOut (Critical), "Printerdə kağız bitib"
node tools/dev-kiosk/app-order.mjs list     # sifariş: Queued, can cancel, KIOSK STOPPED
docker exec neoprint-kshot rm /var/lib/neoprint/spool/printed/printer-state.txt
# ~20 saniyə sonra: kiosk Online, incident "Printer yenidən hazırdır; incident avtomatik bağlandı", sifariş Completed
```

Kioskun ekranı: "Kiosk müvəqqəti dayanmışdır — Printerdə kağız bitib. Növbədəki sifarişlər itmir…"
(`docs/screenshots/step-7/m5-01-kiosk-printer-stopped.png`). Mobil tətbiqdə sifariş kartında xəbərdarlıq
və "Ləğv et və məbləği qaytar" (`m5-02` … `m5-04`).

**Ssenari A, çap ortasında xəta** (sifariş kəsilir, kiosk **dərhal** dayanır, printer hazır olanda sifariş
yenidən çap olunur):

```bash
docker exec neoprint-kshot sh -c 'echo PaperJam > /var/lib/neoprint/spool/printed/printer-fail-next.txt'
node tools/dev-kiosk/app-order.mjs          # sifariş: Interrupted, can cancel, KIOSK STOPPED
# növbəti heartbeat-də (≤ 15 saniyə) printer "hazırdır": kiosk Online, sifariş yenidən Queued → Completed
```

**Ssenari A, qonaq (bank QR)** — kioskun öz ekranından: fləş qovluğuna fayl qoyun
(`docker exec neoprint-kshot cp <pdf> /media/kiosk/NEOSTICK/hesabat.pdf`; konteyner yenidən başlayanda
qovluq boşalır), ekranda **Flash kartdan çap et** → fayl → **Davam et** → **Sifariş et** → **Bank kartı və
ya bank tətbiqi**; QR ekranında printeri dayandırın (`printer-state.txt`) və ödənişi mock bankla edin
(Bölmə 7c, `POST /api/dev/payments/<id>/simulate-success`). ~40 saniyə sonra sifariş `Cancelled`, refund
`Succeeded` (karta); qəbz ekranında "Çap tamamlanmadı — 0,20 ₼ kartınıza qaytarılır", ekran özü bağlanandan
sonra bildiriş "Kiosk müvəqqəti dayanmışdır" ekranında qalır (`m5-05`, `m5-06`).

**Ssenari B, kiosk susur:** `docker stop neoprint-kshot` → 60 saniyədən sonra (`Kiosk.OfflineAfterSeconds`;
monitor 15 saniyədə bir işləyir) kiosk `Offline`, incident `ConnectionLost` (Critical). Heç nə ləğv olunmur
və qaytarılmır. `docker start neoprint-kshot` → kiosk `Online`, incident "Kiosk yenidən əlaqədədir…".

**Tətbiqin çöküşü:** `docker kill -s KILL neoprint-kshot` + `docker start` → incident `AppCrash` (High).
`docker restart` (SIGTERM) çöküş sayılmır (T-236). 10 dəqiqədə üç çöküş → təhlükəsiz rejim, incident
`AppCrash` (Critical); konteynerdə təhlükəsiz rejimdən çıxmaq üçün:
`docker exec neoprint-kshot sh -c 'rm -f /var/lib/neoprint/state/runs.json'` və `docker restart neoprint-kshot`.

**Incident emaili (Mailpit).** Incident `incidents.view` icazəsi olan və həmin kioskun əhatəsindəki hesablara
email ilə gedir; lokalda məktublar **Mailpit**-ə düşür (http://localhost:8025, mövzu "NeoPrint — Critical
incident: KSK-9002"). Hədd tənzimləmədir — `Notification.MinSeverity` (default `High`: `Critical` və `High`
gedir, `Medium` yalnız paneldə qalır):

```bash
node tools/dev-kiosk/setting.mjs                                    # default-dan fərqli tənzimləmələr
node tools/dev-kiosk/setting.mjs Notification.MinSeverity           # bir tənzimləmə, izahı ilə
node tools/dev-kiosk/setting.mjs Notification.MinSeverity Critical  # saxla; yanlış dəyər 400 ilə rədd olunur
node tools/dev-kiosk/setting.mjs Notification.MinSeverity High      # geri qaytar
```

Yoxlamaq üçün: `docker stop neoprint-kshot` → ~60 saniyəyə `ConnectionLost` (Critical) → Mailpit-də hər
hesaba bir məktub; `docker kill -s KILL neoprint-kshot && docker start neoprint-kshot` → `AppCrash` (High) —
hədd `Critical` olanda bu incident üçün məktub gəlmir.

**Telegram** kanalı Faza 2-dədir və söndürülüdür: `Notification.TelegramEnabled` = `false`. Açıq olsa belə bot
token-i (`NEOPRINT_TELEGRAM_BOT_TOKEN`, OpenBao `neoprint/backend/providers`) olmadan heç nə göndərilmir.

**Avtomatik çap cəhdləri.** `PrintJob.MaxAutoRetries` (default 3, 1–10) — printerin dayandırdığı sifariş cəmi
neçə dəfə çapa göndərilir. `1` edib "Ssenari A, çap ortasında xəta"nı təkrarlasanız sifariş `Interrupted`
qalır və `High` incident açılır ("… 1 dəfə yarımçıq qaldı və bir daha avtomatik çapa göndərilmədi"); sonra
dəyəri `3`-ə qaytarın.

### Admin panel: canlı yenilənmə (T-248, T-245)

Panelin açıq səhifələri yenilənmədən ("F5"-siz) kioskun statusunu izləməlidir. İki skript bunu real stack-də
brauzerlə (Chrome / Edge, `playwright-core`) yoxlayır — `admin/` qovluğundan:

```bash
node e2e/live.mjs                    # KSK-9002 konteyneri dayandırılır / işə salınır; siyahı və dashboard izləməlidir
node e2e/live.mjs --connection       # panelin öz canlı bağlantısı kəsilir: 30 saniyəlik sorğu ilə izləməlidir
node e2e/scope.mjs                   # əhatəsi məhdud hesab: yalnız öz kioskunu görür və eşidir; əhatə dəyişəndə bağlantı yenilənir
node e2e/live.mjs KSK-9002 neoprint-kshot ../docs/screenshots/step-7/live   # ekran görüntüləri ilə
```

Hər skript zaman cədvəli (API-nin dediyi / siyahının göstərdiyi / dashboard), tab-ların aldığı hub
hadisələrini yazır və son sətirdə nəticəni deyir (çıxış kodu 0 — izlədi). `scope.mjs` hər qaçışda
`scope-check-<vaxt>@neoprint.local` adlı hesab və rol yaradır (təsadüfi şifrə ilə) və sonda hesabı söndürür;
`dev-superadmin` hesabı ilə işləyir.

**API-yə giriş limiti:** bir ünvandan dəqiqədə 10 giriş. `tools/dev-kiosk/*.mjs` skriptləri hər çağırışda
yenidən daxil olur — onları dövrədə (hər 3 saniyədən bir) çağırsanız `429` alacaqsınız; bir dəqiqə gözləyin.

---

## 7n. Addım 7A — sərf, servis tapşırıqları və anbar (plan 2.15, 9.9; T-210)

Admin və operator panelinin bu səhifələri M7–M9-dadır; backend hazırdır və bütün axın `tools/dev-kiosk/supplies.mjs`
ilə əllə keçilir (qayda və anbar işləri `dev-superadmin`, operatorun addımları `dev-operator` hesabı ilə gedir):

```bash
node tools/dev-kiosk/supplies.mjs                 # KSK-9002: lotoklar, toner səviyyələri, proqnoz, alert-lər, tapşırıqlar
node tools/dev-kiosk/supplies.mjs rules           # xəbərdarlıq qaydaları (seed: Kağız A4 < 200 / < 50 vərəq, Toner < 15 / < 3 %)
node tools/dev-kiosk/supplies.mjs inventory       # anbar
```

**Plan 9.9-u başdan-sona keçmək** (Linux konteyner kiosku, fake printer):

```bash
# anbar: bir material və 10 bağlama
node tools/dev-kiosk/supplies.mjs item PAPER-A4-500 "A4 kağız, 500 vərəq" PaperA4Ream 3
node tools/dev-kiosk/supplies.mjs inventory                       # son sütun — materialın id-si
node tools/dev-kiosk/supplies.mjs move <itemId> Purchase 10

# 1–2: lotokda 200 vərəq, bir sifariş → 199 → Warning alert + operatora tapşırıq + email (Mailpit)
node tools/dev-kiosk/supplies.mjs set 200
node tools/dev-kiosk/app-order.mjs
node tools/dev-kiosk/supplies.mjs                 # Alerts: Warning PaperA4 199 … ; Service tasks: Assigned High RefillPaper … Test Operator

# 3: 50-dən aşağı → eyni alert Critical, tapşırıq Critical
node tools/dev-kiosk/supplies.mjs set 50
node tools/dev-kiosk/app-order.mjs

# 4–5: operator başlayır və tamamlayır ("Tam doldur" + anbardan 1 bağlama)
node tools/dev-kiosk/supplies.mjs tasks
node tools/dev-kiosk/supplies.mjs start <taskId>
node tools/dev-kiosk/supplies.mjs complete <taskId> <itemId> 1
node tools/dev-kiosk/supplies.mjs                 # A4: 500 / 500, Alerts: none, tapşırıq Done
node tools/dev-kiosk/supplies.mjs inventory       # 9 bağlama
```

Nə vaxt nə baş verir:

- **Qiymətləndirmə** kağız sayğacı hər dəyişəndə (çap, test çap, doldurma, sayma, tamamlanan tapşırıq) bir-iki
  saniyəyə, əlavə olaraq hər 5 dəqiqədən bir bütün kiosklar üçün işləyir (toner telemetriyası və dəyişən qaydalar).
  Qayda yaradılanda / dəyişəndə dərhal.
- **Toner** seçilmiş printerin telemetriyasından oxunur; fake printer toner bildirmir, ona görə konteyner kioskunda
  `levels` boşdur (real printerdə MT-02).
- **Proqnoz** saatda bir (hər saatın 7-ci dəqiqəsi): bitməyə `Service.ForecastLeadHours` (24) qalanda tapşırıq açır.
  `supplies.mjs`-in "Forecast" sətirləri hər çağırışda yenidən hesablanır.
- **Eskalasiya** 5 dəqiqədən bir: `Critical` tapşırıq `Service.TaskOverdueEscalationMinutes` (60) ərzində başlanmasa
  `High` incident (email Mailpit-də). Tez yoxlamaq üçün: `node tools/dev-kiosk/setting.mjs Service.TaskOverdueEscalationMinutes 1`.
- **Anbar minimumu**: hərəkətdən dərhal sonra və saatda bir. `move <itemId> WriteOff 8` → "NeoPrint — High incident: Sistem".

Arxa plan işlərinin qeydiyyatını və son icrasını Hangfire-ın saxladığı yerdən görmək (şifrə konteynerin içində qalır):

```bash
docker exec neoprint-mongo sh -c 'mongosh --quiet -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" \
  --authenticationDatabase admin neoprint --eval "db.getCollection(\"hangfire.jobGraph\").find({ Key: /recurring-job:(supply|service-task|inventory)/ }).forEach(r => print(r.Key, r.Fields.Cron, r.Fields.LastExecution || \"-\"))"'
```

**Yeni aktivləşdirilən kioskun sayğacı 0-dır**: ilk qiymətləndirmədə "Kağız A4" üçün `Critical` alert və tapşırıq açılacaq —
lotoku doldurub sayğacı yazın ("Kağız doldur" və ya `supplies.mjs set <say> <kiosk kodu>`).

---

## 8. Build, test, lint

```bash
# Backend (integration testlər Docker tələb edir; Docker yoxdursa skip olunur)
dotnet build backend/NeoPrint.sln
dotnet test  backend/NeoPrint.sln

# Kiosk
dotnet build desktop/NeoPrint.Kiosk.sln
dotnet test  desktop/NeoPrint.Kiosk.sln

# Kiosk — Linux build və testləri (yalnız Docker lazımdır; Windows bin/obj-a toxunmur)
bash tools/dev-kiosk/linux-check.sh

# Mobil
cd mobile && flutter analyze && flutter test && dart format --set-exit-if-changed .
flutter build web          # lokalda; Android build CI-da (Android SDK 36 lazımdır)
# Hazır web build-i lokal API ilə açmaq üçün (release build default olaraq api.neoprint.az-a gedir):
flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/
# Kiosklar xəritəsi: release build ictimai OSM tile serverini işlətmir (T-061) — tile ünvanı verilməyibsə
# yalnız siyahı görünür. Lokal yoxlama üçün (az sayda sorğu, OSM siyasətinə uyğun):
flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/ "--dart-define=NEOPRINT_MAP_TILE_URL=https://tile.openstreetmap.org/{z}/{x}/{y}.png"
# flutter run (debug) xəritəni əlavə parametrsiz göstərir.

# Admin
cd admin && npm run lint && npm test && npm run build
node scripts/icon-subset.mjs --check   # istifadə olunan ikonlar şriftdədirmi
node scripts/icon-subset.mjs           # yeni ikon əlavə edəndən sonra şrifti yenidən qur

# Lisenziya siyasəti (bütün ekosistemlər)
node tools/license-check --strict

# Lisenziya yoxlayıcısının öz testləri
cd tools/license-check && npm test

# Admin testləri tək-tək (bu maşında bütöv dəst işə düşmür - T-171; runner başlamasa təkrar edin)
cd admin && npx ng test --watch=false --include=src/app/core/realtime/live-refresh.spec.ts

# Dizayn qaydaları: hardcoded rəng / şrift ölçüsü / boşluq (admin, mobil, kiosk)
node tools/design-lint --list
cd tools/design-lint && npm test

# Tapşırıq reyestri: docs/TASKS.md-nin hər sətri altı sütundur, statusu tanınır, ID təkrarlanmır
# (xana içində "|" yalnız "|" kimi yazılır). CI-da "design rules" işində də işləyir.
node tools/tasks-lint
cd tools/tasks-lint && npm test
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

**İnteqrasiya dəstini başqa ağır işlə eyni anda işlətməyin.** Ödəniş → refund →
fiskal çek kimi zəncirlər 4–5 asinxron addımdır (outbox, RabbitMQ, Hangfire) və
testlər nəticəni 30 saniyə gözləyir. Eyni anda `flutter test`, `flutter pub get`,
kiosk testləri və ya build gedəndə dəst 6 dəqiqədən 9+ dəqiqəyə uzanır və bir-iki
test vaxt aşımı ilə düşə bilər (`Waited 30 s for ...`). Belə düşmədə əvvəl dəsti tək
işlədin; tək işləyəndə də düşürsə, bu real xətadır (T-116).

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

### Windows-da "2 skip" nədir

Windows-da inteqrasiya dəsti `419/421, 2 skip` göstərir. Skip olunan iki test
`SandboxedProcessTests`-dəki resurs limiti testləridir:

- `A_program_that_spins_is_stopped_by_the_cpu_limit_long_before_its_timeout`;
- `A_program_that_allocates_without_bound_is_stopped_by_the_memory_limit`.

Konvertasiya prosesinin CPU və yaddaş limiti Linux-un `prlimit` əmri ilə
qoyulur. Bu yalnız backend image-ində (Linux) işləyir, ona görə testlər
`LinuxFact` ilə işarələnib və Windows-da skip olunur. Bu xəta deyil. CI
(`ubuntu-latest`) hər iki testi işlədir: `421/421, Skipped: 0`. Lokalda
işlətmək üçün dəsti WSL-də və ya Linux-da işə salın.

PDFium testləri (yuxarıda) DLL olmayanda skip olunur, onda skip sayı artır.
Sayı yoxlamaq üçün:

```bash
dotnet test backend/tests/NeoPrint.Api.IntegrationTests --logger "console;verbosity=normal" | grep "\[SKIP\]"
```

> Kiosk build-i "file is locked" xətası verirsə, işləyən kiosk tətbiqi DLL-ləri
> tutub saxlayır: `Stop-Process -Name NeoPrint.Kiosk -Force`.

### UI yoxlamaları (xam açar, sığmayan mətn, ekran şəkilləri — T-228)

Hər üç tətbiqdə avtomatikdir və CI-da işləyir; lokalda:

```bash
# Admin — statik: hər açar lüğətdə, serverin hər dəyərinin (enum, ayar, funksiya açarı) tərcüməsi var
cd admin && node scripts/i18n-check.mjs

# Admin — render: yığılmış panel real brauzerdə, az / en / ru; şəkillər admin/e2e/screenshots-da
cd admin && npx ng build --configuration production && npm run e2e
# Brauzer: maşındakı Chrome və ya Edge. Başqa birini göstərmək üçün:
#   E2E_CHROME="C:/…/chrome.exe" npm run e2e

# Admin — render testinin API cavablarını yenidən yazmaq (lokal stack qalxmış olmalıdır, ~2 dəqiqə)
cd admin && npm run e2e:record        # sonra: git diff e2e/fixtures/api.json — commit-dən əvvəl baxın

# Kiosk — 33 ekran × 3 dil, 1920×1080; şəkillər tests/NeoPrint.Kiosk.ScreenTests/bin/…/screens-da
dotnet test desktop/tests/NeoPrint.Kiosk.ScreenTests
#   NEOPRINT_SCREENS_DIR=<qovluq> ilə şəkillərin yeri dəyişir

# Mobil — bütün widget testləri 360 × 800 dp ekranda (daşma = uğursuz test)
cd mobile && flutter test
```

- `npm run e2e:record` cavabları repoya yazmazdan əvvəl təmizləyir (token, email, host adı, IP, MAC, seriya
  nömrəsi əvəz olunur; cədvəllər ilk 4 sətrə kəsilir) və API-nin sürət həddinə (`429`) düşsə heç nə yazmır.
  Yenidən yazmaq yalnız səhifə API-dən yeni bir şey istəyəndə lazımdır — test bunu özü deyir.
- Render testi `dev-admin`-in görə bildiyi səhifələri açır; `/admins`, `/roles` və `/storage` onun
  icazəsində deyil və bu testdə yoxdur (statik yoxlama onları da əhatə edir).
- Layihə sahibinin gördüyü xırda UI problemləri `docs/UI_BUGS.md`-yə yazılır.

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
