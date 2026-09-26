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
docker compose up -d

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
docker compose up -d --build api
```

**Dayandırma:**

```bash
docker compose stop            # datanı saxlayır
docker compose down            # konteynerləri silir, volume-lar qalır
docker compose down -v         # HƏR ŞEYİ silir (Mongo, OpenBao, RustFS datası)
```

### OpenBao restart-dan sonra möhürlənir (sealed)

`docker compose restart openbao` və ya maşını söndürdükdən sonra OpenBao
**sealed** vəziyyətdə qalxır və API secret oxuya bilmir. Açmaq üçün:

```bash
# Unseal açarı openbao-init tərəfindən yazılıb (bu fayl .gitignore-dadır)
docker compose up -d openbao-init      # init.sh mövcud vəziyyəti görüb yalnız unseal edir
docker compose restart api
```

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
portlarını tutur. İki yol:

**A. Sıfırdan (tövsiyə olunur, bütün lokal data silinir):**

```bash
docker compose down -v --remove-orphans
cp .env.openbao-seed .env.openbao-seed.bak        # köhnəsini saxlayın
cp .env.openbao-seed.example .env.openbao-seed    # RustFS dəyərləri
docker compose up -d --build
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

### Admin paneli — SuperAdmin (seed edilir)

| | |
|---|---|
| Ünvan | http://localhost:8080 |
| Email | `admin@neoprint.local` |
| Şifrə | `ChangeMe!2026` |

**İlk girişdə şifrə dəyişmək məcburidir** (plan Bölmə 2.8). Dəyişdiyiniz şifrəni
buraya yazın ki, itməsin:

| | |
|---|---|
| Yeni şifrə (siz təyin edirsiniz) | `…` |

### Digər rollar

Seed yalnız rolları yaradır, istifadəçiləri yaratmır. Aşağıdakı rollar hazırdır:

| Rol | Nə görür |
|---|---|
| `SuperAdmin` | Hər şey. Sistem rolu — redaktə/silinə bilməz, ən azı 1 aktiv qalmalıdır |
| `Admin` | Storage, admin istifadəçiləri, rollar və audit **xaric** hamısı |
| `Operator` | Kiosk xidməti: kiosklar, printerlər, sərf materialları, servis tapşırıqları, incident-lər + `/operator` paneli |
| `Maliyyə` | Ödəniş, qaytarma, qəbz, fiskal çeklər, wallet düzəlişi |

Test istifadəçisi yaratmaq: SuperAdmin ilə daxil ol → **Admin istifadəçiləri** →
**Yeni** → rol seç. Sistem müvəqqəti şifrə verir və ilk girişdə dəyişməyi tələb edir.
Yaratdığınız hesabları buraya yazın:

| Email | Rol | Şifrə |
|---|---|---|
| `…` | `…` | `…` |

### Mobil tətbiq istifadəçisi

Seed edilmir, lakin lokal mühitdə bir hesab artıq yaradılıb və emaili
təsdiqlənib:

| | |
|---|---|
| Email | `test@neoprint.local` |
| Şifrə | `TestUser!2026` |

Yeni hesab yaratmaq: tətbiqdən qeydiyyatdan keçin. Təsdiq kodu email ilə gəlir
və **Mailpit**-də görünür: http://localhost:8025

Kodu tərminaldan götürmək:

```bash
ID=$(curl -s "http://localhost:8025/api/v1/messages?limit=1" | grep -o '"ID":"[^"]*"' | head -1 | sed 's/.*://;s/"//g')
curl -s "http://localhost:8025/api/v1/message/$ID" | grep -o '[0-9]\{6\}' | head -1
```

> `docker compose down -v` bu hesabı silir (Mongo volume-u ilə birlikdə) —
> onda yuxarıdakı addımlarla yenidən yaradın.

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

PowerShell-i **"Run as administrator"** ilə açın:

```powershell
cd C:\Users\EFSO\source\zahid\Ai\NeoPrint
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
# Windows: cihaz faylları
Remove-Item -Recurse -Force "$env:ProgramData\NeoPrint" -ErrorAction SilentlyContinue
```

```bash
# Linux
sudo rm -rf /var/lib/neoprint
```

Alternativ (daha düzgün yol): admin paneldə **Cihazlar → Cihazı ayır**. Kiosk
növbəti sorğuda 403 alır və özü Activation ekranına qayıdır.

### Linux-da işə salmaq

```bash
export NEOPRINT_KIOSK_ENVIRONMENT=Development
dotnet run --project desktop/src/NeoPrint.Kiosk
```

Cihaz açarı `/var/lib/neoprint/device/key.pem` (`chmod 600`) faylındadır, ona
görə həmin qovluğa yazma icazəsi lazımdır.

---

## 6. Mobil tətbiqi işə salmaq (Flutter)

API ünvanı **debug build-də avtomatik** seçilir:

| Hədəf | Avtomatik URL |
|---|---|
| Android emulyator | `http://10.0.2.2:5080` |
| iOS simulyator, brauzer, desktop | `http://localhost:5080` |
| Release build | `https://api.neoprint.az` |

```bash
cd mobile
flutter pub get

# Android emulyator
flutter run

# Brauzer (yalnız UI-a baxmaq üçün; plan hədəfi Android/iOS-dur)
flutter run -d chrome
```

Real telefondan kompüterdəki API-yə qoşulmaq üçün ünvanı əl ilə verin:

```bash
flutter run --dart-define=NEOPRINT_API_URL=http://192.168.1.50:5080
```

> **Brauzerdə (Flutter web) işlədəndə API-nin CORS-u lazımdır.** API yalnız
> `ASPNETCORE_ENVIRONMENT=Development` olduqda `http://localhost:*` mənşələrini
> qəbul edir (`Cors:DevelopmentOrigins`). Produksiyada CORS tamamilə bağlıdır.

Qeydiyyat kodu Mailpit-də: http://localhost:8025

---

## 7. Admin panelini işə salmaq (Angular)

```bash
cd admin
npm ci
npm start            # http://localhost:4200
```

`proxy.conf.json` `/api` və `/hubs`-u `http://localhost:8080`-ə (admin
konteynerinin nginx-inə) yönləndirir, ona görə `docker compose up -d` işləməlidir.

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

# Admin
cd admin && npm run lint && npm test && npm run build

# Lisenziya siyasəti (bütün ekosistemlər)
node tools/license-check --strict

# Lisenziya yoxlayıcısının öz testləri
cd tools/license-check && npm test
```

### Konvertasiya testləri (PDFium)

Səhifə sayı və preview PDFium native kitabxanasını tələb edir. Backend Docker
image-i və CI onu daşıyır; Windows maşınında yoxdursa həmin testlər **skip**
olunur (`PdfiumFact` səbəbini yazır). Lokalda da işlətmək istəsəniz:

```bash
# Linux / WSL
curl -fsSL -o /tmp/pdfium.tgz \
  "https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8066/pdfium-linux-x64.tgz"
tar -xzf /tmp/pdfium.tgz -C /tmp
sudo cp /tmp/lib/libpdfium.so /usr/local/lib/ && sudo ldconfig
```

Windows üçün eyni release-dən `pdfium-win-x64.tgz` götürüb `pdfium.dll`-i test
çıxış qovluğuna qoyun.

LibreOffice və Ghostscript marşrutları yalnız backend image-ində işləyir; onlar
olmayanda konvertasiya faylı `Failed` edir və səbəbini yazır — sorğu xəta
atmır.

> Kiosk build-i "file is locked" xətası verirsə, işləyən kiosk tətbiqi DLL-ləri
> tutub saxlayır: `Stop-Process -Name NeoPrint.Kiosk -Force`.

---

## 9. Tez-tez lazım olan yoxlamalar

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
