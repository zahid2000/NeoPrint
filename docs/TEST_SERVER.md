# NeoPrint — test serveri

İnternetdən əlçatan test mühiti: Ubuntu serverdə bütöv stack, GitHub Actions ilə
deploy, və bu serverə yönəlmiş Android APK. Lokal mühit üçün
[`DEV_ENVIRONMENT.md`](DEV_ENVIRONMENT.md)-ə baxın.

> **Bu, production deyil.** API burada `Development` rejimində işləyir (qərar
> 2026-10-14, `DECISIONS.md`): real bank adapteri gələnə qədər ödənişi yalnız
> mock bankın simulyatoru tamamlaya bilir, o isə yalnız `Development`-də var.
> Serverə real istifadəçi məlumatı və real pul daxil edilmir. OpenBao burada
> development quruluşundadır (bir unseal açarı, açar serverin diskində) —
> production qaydaları `OPENBAO.md` bölmə 4-dədir və dəyişməyib.

## Nə qurulur

```
telefon / brauzer / kiosk
        │  HTTPS (80 → 443)
        ▼
   ┌─────────┐   /api/dev/*  → parol  ┐
   │  Caddy  │   /refund/*   → api    │   yeganə açıq qapı;
   │ (TLS)   │   /download/* → APK    │   sertifikatı özü alır və yeniləyir
   └────┬────┘   qalan hər şey ↓      ┘
        ▼
   admin (nginx): panel + /api və /hubs → api      ← lokal mühitdəki kimi bir origin
        ▼
   api · converter · mongo · redis · rabbitmq · rustfs · openbao · mailpit
        (heç biri kənara port açmır)
```

- Stack lokal `docker-compose.yml`-in **üstünə** qoyulan
  [`deploy/test-server/compose.override.yml`](../deploy/test-server/compose.override.yml)-dir —
  ikinci nüsxə yoxdur, ona görə lokal mühitlə ayrıla bilməz.
- Serverdə heç nə build olunmur: image-ləri workflow qurur və `ghcr.io`-ya qoyur.
- **Serverin bütün parolları serverin özündə təsadüfi yaradılır**
  ([`deploy.sh`](../deploy/test-server/deploy.sh)) və oradan çıxmır. GitHub-a heç
  bir tətbiq secret-i yazılmır; nümunə faylındakı development parolları serverdə
  istifadə olunmur.

## Ünvanlar

Domen hələ yoxdur, ona görə serverin IP-sindən `sslip.io` adı düzəlir:
`203.0.113.7` → `203-0-113-7.sslip.io` (nöqtələr tire ilə). Aşağıda `<HOST>` budur.

| Nə | Ünvan |
|---|---|
| Admin paneli | `https://<HOST>/` |
| Operator paneli | `https://<HOST>/operator` |
| API (mobil tətbiq, kiosk) | `https://<HOST>/` — `api/...` və `hubs/...` |
| Test APK | `https://<HOST>/download/neoprint-test.apk` |
| Mailpit (qeydiyyat və şifrə kodları) | `https://mail.<HOST>/` — parolla |
| Dev endpoint-ləri (mock bank və s.) | `https://<HOST>/api/dev/...` — parolla |

Swagger, MongoDB, Redis, RabbitMQ, RustFS və OpenBao kənardan açılmır
(bax: "SSH tuneli").

---

## Quraşdırma (bir dəfə)

### 1. Serveri hazırlayın

Serverə daxil olun və hazırlıq skriptini işə salın. O, Docker-i Docker-in öz
apt repo-sundan qurur, deploy istifadəçisinə Docker icazəsi verir,
`/opt/neoprint` qovluğunu yaradır və `ufw` aktivdirsə 22, 80, 443-ü açır.

```bash
ssh <istifadəçi>@<SERVER_IP>

curl -fsSL https://raw.githubusercontent.com/zahid2000/NeoPrint/main/deploy/test-server/server-setup.sh -o server-setup.sh
sudo bash server-setup.sh          # deploy istifadəçisi: sudo-nu işlədən hesab
# başqa hesab və ya qovluq üçün:  sudo bash server-setup.sh deploy /opt/neoprint
```

Skript deploy istifadəçisini `docker` qrupuna əlavə edir; bunun qüvvəyə minməsi
üçün serverdən çıxıb yenidən daxil olun.

**Hostinq provayderinin öz firewall-u varsa** (bulud "security group"), orada da
**80/tcp, 443/tcp** (və HTTP/3 üçün 443/udp) açılmalıdır — sertifikat 80 və 443
üzərindən alınır.

Tələblər: Ubuntu 22.04, 24.04 və ya 26.04 (26.04.1 LTS-də yoxlanılıb); **4 GB RAM** və ~20 GB disk tövsiyə olunur
(Mongo, RabbitMQ, iki RustFS və LibreOffice-li converter bir yerdədir; converter
təkbaşına 2 GB-a qədər götürə bilər). Bu rəqəmlər hesablamadır, bu serverdə
ölçülməyib.

### 2. Deploy üçün SSH açarı yaradın

Workflow serverə parolla yox, yalnız bu iş üçün yaradılmış açarla daxil olur.
Öz kompüterinizdə (PowerShell və ya bash):

```bash
ssh-keygen -t ed25519 -f neoprint-deploy -N "" -C "neoprint-deploy"
```

İki fayl yaranır: `neoprint-deploy` (gizli — GitHub-a gedəcək) və
`neoprint-deploy.pub` (açıq — serverə gedəcək). Açıq açarı serverə əlavə edin.

**Deploy istifadəçisi ayrıca, parolsuz hesabdırsa** (tövsiyə olunan: `sudo adduser --disabled-password --gecos "" deploy`,
sonra `sudo bash server-setup.sh deploy`), açarı onun yerinə `sudo`-su olan hesabla qoyun —
`/home/deploy`-a başqa hesab `sudo`-suz yaza bilmir, `-t` isə `sudo`-ya parolu soruşmağa imkan verir:

```powershell
scp neoprint-deploy.pub <admin_hesabı>@<SERVER_IP>:~/
ssh -t <admin_hesabı>@<SERVER_IP> "sudo install -d -m 700 -o deploy -g deploy /home/deploy/.ssh && sudo sh -c 'cat ~<admin_hesabı>/neoprint-deploy.pub >> /home/deploy/.ssh/authorized_keys' && sudo chown deploy:deploy /home/deploy/.ssh/authorized_keys && sudo chmod 600 /home/deploy/.ssh/authorized_keys && rm ~/neoprint-deploy.pub"
```

Deploy istifadəçisi daxil olduğunuz hesabın özüdürsə:

```powershell
# PowerShell
Get-Content neoprint-deploy.pub | ssh <istifadəçi>@<SERVER_IP> "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
```

```bash
# bash
ssh-copy-id -i neoprint-deploy.pub <istifadəçi>@<SERVER_IP>
```

Yoxlayın — parol soruşmadan `ok` yazmalıdır:

```bash
ssh -i neoprint-deploy <istifadəçi>@<SERVER_IP> "docker compose version && echo ok"
```

Serverin kimlik açarını da götürün (workflow yalnız bu açarı tanıyan serverə
qoşulur, başqasına token vermir):

```bash
ssh-keyscan -t ed25519 <SERVER_IP>            # port 22 deyilsə: ssh-keyscan -p <PORT> -t ed25519 <SERVER_IP>
```

Windows-un `ssh-keyscan`-ı yeni OpenSSH ilə (məs. Ubuntu 26.04) danışa bilmir
(`choose_kex: unsupported KEX method`) və boş qaytarır. O halda açarı serverin özündən götürün:

```powershell
$key = (ssh <istifadəçi>@<SERVER_IP> "cat /etc/ssh/ssh_host_ed25519_key.pub").Split(' ')[0..1] -join ' '
gh variable set TEST_SERVER_KNOWN_HOSTS --body "<SERVER_IP> $key"
```

Çıxan sətir (`<SERVER_IP> ssh-ed25519 AAAA…`) növbəti addımda lazımdır.

### 3. Məlumatları GitHub-a yazın

**Harada:** repo → **Settings → Secrets and variables → Actions**. Orada iki tab
var: **Variables** (gizli olmayan dəyərlər) və **Secrets**.

**Variables** tabı → "New repository variable":

| Ad | Dəyər | Məcburi |
|---|---|---|
| `TEST_SERVER_HOST` | Serverin IP-si, məs. `203.0.113.7` | bəli |
| `TEST_SERVER_USER` | SSH istifadəçisi (addım 1-dəki deploy istifadəçisi) | bəli |
| `TEST_SERVER_KNOWN_HOSTS` | `ssh-keyscan`-ın verdiyi sətir (bütöv) | bəli |
| `TEST_SERVER_PORT` | SSH portu | yox — `22` |
| `TEST_SERVER_DIR` | Serverdəki qovluq | yox — `/opt/neoprint` |
| `TEST_DOMAIN` | Domen qoşulanda: `test.neoprint.az` | yox — `<ip>.sslip.io` |
| `TEST_ACME_EMAIL` | Sertifikat üçün əlaqə emaili | yox, amma **tövsiyə olunur** (aşağıda) |
| `TEST_MAP_TILE_URL` | APK-dakı xəritə üçün tile ünvanı | yox — onsuz yalnız kiosk siyahısı |

**Secrets** tabı → "New repository secret":

| Ad | Dəyər | Məcburi |
|---|---|---|
| `TEST_SERVER_SSH_KEY` | `neoprint-deploy` faylının **bütöv məzmunu** (`-----BEGIN…` sətrindən `-----END…` sətrinə qədər) | bəli |
| `TEST_ANDROID_KEYSTORE_B64` | APK imza açarı, base64 (aşağıda) | yox, amma tövsiyə olunur |

Eyni şeyi terminaldan da etmək olar (`gh` ilə, repo qovluğunda):

```powershell
gh variable set TEST_SERVER_HOST --body "203.0.113.7"
gh variable set TEST_SERVER_USER --body "deploy"
gh variable set TEST_SERVER_KNOWN_HOSTS --body (ssh-keyscan -t ed25519 203.0.113.7 | Out-String)
gh variable set TEST_ACME_EMAIL --body "siz@example.com"
Get-Content neoprint-deploy -Raw | gh secret set TEST_SERVER_SSH_KEY
```

Yazandan sonra `neoprint-deploy` faylını kompüterdə saxlamağa ehtiyac yoxdur —
silin və ya parol menecerinə qoyun. **Repo public-dir: bu faylı heç vaxt repo
qovluğuna qoymayın.**

**`TEST_ACME_EMAIL` niyə tövsiyə olunur.** `sslip.io` hamının paylaşdığı addır və
Let's Encrypt bir ada həftədə məhdud sayda sertifikat verir — limit başqalarına
görə dolmuş ola bilər. Email verilibsə Caddy belə halda ikinci sertifikat
mərkəzinə (ZeroSSL) keçir. Öz domeniniz qoşulanda bu problem aradan qalxır.

**`TEST_ANDROID_KEYSTORE_B64` nə üçündür.** APK debug açarı ilə imzalanır
(`mobile/android/app/build.gradle.kts`), GitHub-ın hər runner-i isə özünə yeni
açar yaradır. Android fərqli açarla imzalanmış build-i köhnəsinin üstünə
qurmur — hər dəfə tətbiqi silib yenidən qurmaq lazım gələr. Açarı bir dəfə
secret kimi yazsanız, bütün build-lər eyni imza ilə çıxır və yeniləmə kimi
qurulur. Kompüterinizdə Flutter artıq bu açarı yaradıb:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\.android\debug.keystore")) | gh secret set TEST_ANDROID_KEYSTORE_B64
```

(Fayl yoxdursa: `keytool -genkeypair -keystore "$env:USERPROFILE\.android\debug.keystore" -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"`.)

### 4. Deploy edin

GitHub → **Actions → "Deploy (test server)" → Run workflow** (branch: `main`).
İki seçim var, ikisi də işarəlidir: serverə deploy və APK.

```powershell
gh workflow run deploy-test.yml          # terminaldan
gh run watch                             # gedişinə baxmaq
```

İlk dəfə uzun çəkir — təxminən yarım saat (API image-i GhostPDL-i mənbədən
yığır); sonrakılar keşlə xeyli qısadır. Bitəndə işin "Summary" səhifəsində panelin və APK-nın
ünvanı yazılır.

Workflow yalnız əl ilə işə düşür: `main`-ə hər push serveri yenidən başlatmasın
deyə. Hər push-da avtomatik deploy istəsəniz, `deploy-test.yml`-in `on:`
bölməsinə `push: { branches: [main] }` əlavə etmək kifayətdir.

---

## İlk giriş və parollar

Parollar serverdədir və workflow log-una **heç vaxt yazılmır** (repo public-dir).
Onları serverdə oxuyun:

```bash
ssh <istifadəçi>@<SERVER_IP>
cd /opt/neoprint

grep -E '^NEOPRINT_SEED_ADMIN_(EMAIL|PASSWORD)=' .env.openbao-seed   # əsas SuperAdmin
grep '^NEOPRINT_DEV_TEST_PASSWORD=' .env.openbao-seed                # hazır test hesabları
cat .test-server/credentials                                          # Mailpit və /api/dev üçün
```

| Hesab | Email | Şifrə |
|---|---|---|
| Əsas SuperAdmin — ilk girişdə şifrə dəyişmək məcburidir | `admin@neoprint.local` | `NEOPRINT_SEED_ADMIN_PASSWORD` |
| SuperAdmin (test) | `dev-superadmin@neoprint.local` | `NEOPRINT_DEV_TEST_PASSWORD` |
| Admin / Operator / Maliyyə | `dev-admin@` / `dev-operator@` / `dev-finance@neoprint.local` | eyni |
| Mobil istifadəçi (email təsdiqlənib) | `dev-user@neoprint.local` | eyni |
| Mailpit və dev endpoint-ləri | `TEST_TOOLS_USER` | `TEST_TOOLS_PASSWORD` |

Test kioskları və qalan hər şey lokal mühitdəki kimidir
(`DEV_ENVIRONMENT.md` bölmə 4 və 5).

**Paneldə bir tənzimləmə:** Tənzimləmələr → `PublicSiteUrl` dəyərini
`https://<HOST>` edin — kioskun qaytarma QR-ı o zaman bu serverin
`/refund/...` səhifəsini açır.

## Mobil tətbiq

1. Telefonda brauzerdə `https://<HOST>/download/neoprint-test.apk` açın və
   qurun ("naməlum mənbələrdən quraşdırma"ya icazə lazımdır). Eyni fayl
   workflow işinin `neoprint-test-apk` artefaktıdır.
2. Tətbiq `https://<HOST>/` ünvanına qoşulur — Wi-Fi və ya mobil internet, fərqi
   yoxdur. Ünvan build zamanı verilir (`--dart-define=NEOPRINT_API_URL`), tətbiqdə
   tənzimləmə yoxdur.
3. Giriş: `dev-user@neoprint.local` və ya yeni hesab — qeydiyyat kodu
   `https://mail.<HOST>/`-da görünür (bu serverdən kənara email getmir).

**Ödənişi tamamlamaq** (mock bank; `PAYMENT_ID` — panelin Ödənişlər səhifəsində):

```bash
curl -u "tester:<TEST_TOOLS_PASSWORD>" -X POST "https://<HOST>/api/dev/payments/<PAYMENT_ID>/simulate-success"
```

Qalan dev əmrləri `DEV_ENVIRONMENT.md` bölmə 7c-dədir; ünvanda
`http://localhost:5080` əvəzinə `https://<HOST>` yazılır və `-u` əlavə olunur.

**Öz kompüterinizdə eyni APK-nı qurmaq:**

```powershell
cd mobile
flutter build apk --release --dart-define=NEOPRINT_API_URL=https://<HOST>
# debug rejimində telefonda işə salmaq:
flutter run -d <telefon_id> --dart-define=NEOPRINT_API_URL=https://<HOST>
```

**Bilinən məhdudiyyət:** dəvət linki (`https://neoprint.az/r/...`) və push
bildirişləri bu serverdə əlavə quraşdırma tələb edir (domen və Firebase açarı —
`DEV_ENVIRONMENT.md` "Firebase (FCM) service account açarı").

## Kiosku test serverinə yönəltmək

```powershell
powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1 -ApiUrl https://<HOST>
```

Cihaz paneldə görünəndə onu oradakı kioska bağlayın (`KIOSK_SETUP.md`).

---

## Gündəlik işlər

### Yeni versiyanı çıxarmaq

Workflow-u yenidən işə salın. Verilənlər (Mongo, fayllar, OpenBao) volume-lardadır
və deploy onlara toxunmur. Yalnız APK lazımdırsa "server" seçimini söndürün.

### Server yenidən başlayandan sonra

OpenBao hər başlanğıcda möhürlü (sealed) qalxır və API ona qoşula bilmir
(`OPENBAO.md` bölmə 3). Həlli — son deploy olunmuş versiyanı yenidən qaldırmaq:

```bash
cd /opt/neoprint && bash deploy/test-server/deploy.sh
```

(və ya workflow-u yenidən işə salın).

### Log-lar və vəziyyət

```bash
cd /opt/neoprint
alias dc='docker compose -f docker-compose.yml -f deploy/test-server/compose.override.yml'

dc ps                          # hər servisin vəziyyəti
docker logs -f neoprint-api    # API log-u
docker logs neoprint-caddy     # sertifikat problemi burada görünür
docker logs neoprint-openbao-init
```

### SSH tuneli (Swagger, RabbitMQ, Mongo…)

Bu servislər kənara port açmır. Lazım olanda konteynerin daxili ünvanına tunel
açın:

```bash
# Swagger: sonra brauzerdə http://localhost:5080/swagger
ssh -L 5080:$(ssh <istifadəçi>@<SERVER_IP> "docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' neoprint-api"):8080 <istifadəçi>@<SERVER_IP>
```

Digərləri üçün konteyner adını və portu dəyişin: `neoprint-rabbitmq` 15672,
`neoprint-mongo` 27017, `neoprint-openbao` 8200, `neoprint-rustfs` 9001.

### Domenə keçmək

1. Domenin (məs. `test.neoprint.az`) **və** `mail.test.neoprint.az`-ın A qeydini
   serverin IP-sinə yönəldin.
2. `TEST_DOMAIN` dəyişənini yazın: `gh variable set TEST_DOMAIN --body "test.neoprint.az"`.
3. Workflow-u hər iki seçimlə işə salın: Caddy yeni ada sertifikat alır, APK
   yeni ünvanla qurulur. **Köhnə APK köhnə ünvana gedir** — yenisini qurun.
4. Paneldə `PublicSiteUrl`-i yeni ünvana dəyişin.

### Seed faylına yeni açar əlavə olunanda

`.env.openbao-seed.example` dəyişəndə (`SEED_SCHEMA_VERSION` artır) deploy
`openbao-init`-in mesajı ilə dayanır və çatışmayan açarın adını deyir. Serverdə
`/opt/neoprint/.env.openbao-seed`-ə həmin sətri əlavə edin (nümunədəki dəyər
development parolu deyilsə, onu; paroldursa öz təsadüfi dəyərinizi) və
workflow-u təkrar işə salın. Mövcud dəyərlərə toxunulmur.

### Sıfırdan başlamaq

**Bütün test məlumatı silinir.**

```bash
cd /opt/neoprint
docker compose -f docker-compose.yml -f deploy/test-server/compose.override.yml down --volumes --remove-orphans
sudo rm -rf .openbao .test-server .env .env.openbao-seed downloads
```

Sonra workflow-u işə salın: yeni parollar yaranır.

> `caddy-data` volume-u da silinir, yəni sertifikat yenidən alınır. Bunu tez-tez
> etməyin — sertifikat mərkəzlərinin həftəlik limiti var.

---

## Təhlükəsizlik qeydləri

- **Açıq portlar:** yalnız 22, 80, 443. Docker öz portlarını `ufw`-dan yan
  keçirir, ona görə override faylı qalan bütün `ports:` bölmələrini silir —
  yeni servis əlavə edəndə bunu yoxlayın (`dc config | grep published`).
- **`Development` rejimi:** `api/dev/*` endpoint-ləri anonimdir; Caddy onların
  qabağına parol qoyur. Swagger API-nin özündədir və nginx onu ötürmür.
- **Deploy istifadəçisi `docker` qrupundadır** — bu, serverdə root deməkdir.
  Açar yalnız GitHub secret-indədir; şübhə yaransa serverdə
  `~/.ssh/authorized_keys`-dən `neoprint-deploy` sətrini silin və yenisini yaradın.
- **Image-lər** `ghcr.io/<hesab>/neoprint-api` və `neoprint-admin` paketləridir
  (default: private). Server onları yalnız deploy zamanı, işin müddəti bitəndə
  ölən token ilə çəkir və dərhal `docker logout` edir.
- **Server reboot-u, disk dolması, backup** — bu mühitdə avtomatlaşdırılmayıb;
  test məlumatı itə bilər.
