# NeoPrint API

Addım 1-in sonundaki vəziyyət. Bu sənəd yalnız **mövcud** endpoint-ləri təsvir
edir; planın Bölmə 6-dakı tam siyahısı sonrakı addımlarda gəlir.

Development-də Swagger UI: `http://localhost:5080/swagger`.

---

## Ümumi qaydalar

### Base URL

| Mühit | URL |
|---|---|
| Development (birbaşa) | `http://localhost:5080` |
| Development (`ng serve` proxy) | `http://localhost:4200/api/...` |
| Docker Compose (nginx) | `http://localhost:8080/api/...` |

Admin paneli və API **eyni origin**-dədir (nginx `/api` və `/hubs` yollarını
backend-ə proxy edir) — admin refresh cookie `SameSite=Strict` olduğu üçün bu
məcburidir.

### Autentifikasiya

Dörd növ token var, hamısı eyni sxem ilə yoxlanılır və `npt` claim-i ilə
ayrılır. Bir növün token-i digərinin endpoint-inə yaramır.

| Token | `npt` | Ömrü | Kim üçün |
|---|---|---|---|
| User | `user` | 15 dəq | Mobil istifadəçi |
| Admin | `admin` | 15 dəq | Admin / operator |
| Kiosk | `kiosk` | 1 saat | Aktivləşdirilmiş cihaz |
| Provisioning | `provisioning` | 1 saat | Aktivləşdirmə gözləyən cihaz |

`Authorization: Bearer <token>`.

**Cihaz token-ləri hər sorğuda cihazın cari vəziyyəti ilə yoxlanılır.** Cihaz
ləğv edilibsə token dərhal 403 alır — token-in bitməsini gözləmir.

Refresh token-ləri:
- **Mobil:** cavab gövdəsində, secure storage-da saxlanılır.
- **Admin:** `HttpOnly`, `SameSite=Strict` cookie (`neoprint_admin_rt`,
  `Path=/api/auth/admin`) — heç bir script onu oxuya bilmir.

Hər ikisi **istifadədə rotasiya olunur**. İstifadə olunmuş token təkrar
göndərilsə zəncir tamamilə ləğv olunur (replay aşkarlanması).

### CorrelationId

Hər sorğu `X-Correlation-Id` header-i ilə gələ bilər; yoxdursa server yaradır.
Hər iki halda cavab header-ində qaytarılır və serverin bütün log sətirlərində
olur. Uğursuz cavabın gövdəsində də `correlationId` sahəsi olur.

### Idempotency

Aşağıdakı endpoint-lər `Idempotency-Key` header-ini qəbul edir:

- `POST /api/devices/register`
- `POST /api/admin/devices/{id}/activate`
- `POST /api/admin/devices/{id}/revoke`
- `POST /api/admin/enrollment-tokens`
- `POST /api/consents`

| Vəziyyət | Nəticə |
|---|---|
| Açar yenidir | Sorğu normal icra olunur, cavab saxlanılır |
| Eyni açar, eyni gövdə, sorğu bitib | Saxlanılmış cavab qaytarılır (yeni iş görülmür) |
| Eyni açar, eyni gövdə, sorğu hələ icradadır | `409` — "Request in progress" |
| Eyni açar, **fərqli** gövdə | `409` — "Idempotency key reused" |
| Sorğu uğursuz oldu | Açar azad olunur, təkrar cəhd mümkündür |

Açar çağırana və endpoint-ə görə unikaldır: bir istifadəçinin açarı başqasının
cavabını qaytara bilməz. Maksimum 128 simvol, 24 saat saxlanılır.

### Xəta formatı

RFC 7807 ProblemDetails:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Request rejected",
  "status": 409,
  "detail": "Bu cihaz ləğv edilib. Kiosk tətbiqini yenidən quraşdırın.",
  "instance": "/api/devices/register",
  "correlationId": "69068640f208405dbe922627cf9b1638",
  "traceId": "0HNOQ1TUTAQ9H"
}
```

| Status | Nə vaxt |
|---|---|
| `400` | Validasiya (`errors` sahəsi ilə: sahə → mesaj siyahısı) |
| `401` | Token yoxdur və ya etibarsızdır |
| `403` | İcazə yoxdur, kiosk əhatəsindən kənardır, və ya cihaz ləğv edilib |
| `404` | Tapılmadı — **axtarılan dəyər cavabda qaytarılmır** |
| `409` | Domen qaydası pozuldu, konkurensiya, və ya idempotency konflikti |
| `429` | Rate limit |

`detail` sahəsi istifadəçiyə göstərilmək üçün yazılır və Azərbaycan dilindədir.

### Rate limit

| Sahə | Limit |
|---|---|
| Auth (`/api/auth/*`) | IP üzrə, konfiqurasiya olunan pəncərə |
| Digər | Qlobal, IP üzrə |

Konfiqurasiya: `RateLimits:*`. Testdə `RateLimits:Disabled=true`.

---

## İstifadəçi autentifikasiyası

### `POST /api/auth/register`

Anonim. Qeydiyyat **token vermir** — email təsdiqlənməlidir.

```json
{
  "email": "user@example.com",
  "password": "Password123",
  "language": "az",
  "consents": [
    { "docType": "Privacy", "version": "0.1-draft" },
    { "docType": "Terms", "version": "0.1-draft" }
  ]
}
```

→ `200` `{ "userId", "email", "confirmationRequired": true }`

Mövcud email üçün də `200` qaytarılır (hesabın varlığını açmamaq üçün), lakin
yeni hesab yaradılmır.

### `POST /api/auth/confirm-email`

`{ "email", "code" }` → sessiya (`accessToken`, `accessTokenExpiresAt`,
`refreshToken`, `user`).

### `POST /api/auth/resend-confirmation`

`{ "email" }` → `204`. Çox tez təkrar istənilsə səssizcə iqnor olunur.

### `POST /api/auth/login`

`{ "email", "password" }` → sessiya. Email təsdiqlənməyibsə `400`.

`user.pendingConsents` — qəbul edilməli yeni sənəd versiyaları.

### `POST /api/auth/refresh`

`{ "refreshToken" }` → yeni sessiya. **Token rotasiya olunur.**

### `POST /api/auth/logout`

`{ "refreshToken" }` → `204`.

---

## Hüquqi sənədlər və razılıqlar

### `GET /api/legal/{docType}?lang=az`

Anonim. `docType`: `Privacy` | `Terms`. Tərcümə yoxdursa Azərbaycan dili
qaytarılır.

→ `{ "docType", "version", "language", "content", "publishedAt" }`

### `GET /api/consents/pending?lang=az`

User token. Qəbul edilməmiş sənəd versiyaları.

### `POST /api/consents`

User token. `Idempotency-Key` qəbul edir.

```json
{ "consents": [ { "docType": "Terms", "version": "0.2" } ] }
```

→ `204`. **İdempotentdir:** eyni (istifadəçi, sənəd, versiya) üçün ikinci qeyd
yaradılmır; ilk qəbulun vaxtı, IP-si və cihazı olduğu kimi qalır.

---

## Cihaz qeydiyyatı və aktivləşdirmə

Bölmə 7.11. Kiosk tətbiqi **universal build**-dir: heç bir kiosk kimliyi
build-ə qoyulmur.

### `POST /api/devices/register`

Anonim. `Idempotency-Key` qəbul edir.

```json
{
  "publicKey": "<ECDSA P-256 SPKI, base64>",
  "fingerprint": { "hostname", "os", "osVersion", "machineId",
                   "macAddresses": [], "cpu", "ramGb", "screenResolution" },
  "platform": "Windows" | "Linux",
  "appVersion": "0.1.0",
  "agentVersion": "0.1.0",
  "provisioningVersion": "1",
  "enrollmentToken": "<hex>",
  "printers": [ { "name", "driverName", "isOnline", "color", "duplex", "a3" } ]
}
```

→ `{ "deviceId", "activated", "pairingCode", "pairingCodeExpiresAt", "kioskId", "kioskCode" }`

- Etibarlı `enrollmentToken` varsa cihaz **dərhal** aktivləşir
  (`activated: true`, `pairingCode: null`).
- Əks halda `XXXX-XXXX` formatında pairing kodu qaytarılır (30 dəq). Kod
  serverdə yalnız hash kimi saxlanılır.
- **Ləğv edilmiş cihaz** `409` alır: yenidən quraşdırma yeni açar yaradır.

### `POST /api/devices/challenge`

Anonim. `{ "deviceId" }` → `{ "nonce", "expiresAt" }` (5 dəq).

Nonce **bir dəfəlikdir** — istifadədən sonra silinir, ona görə tutulmuş imza
təkrar oynanıla bilməz.

### `POST /api/devices/token`

Anonim. `{ "deviceId", "nonce", "signature" }` (IEEE P1363 imza).

→ `{ "accessToken", "expiresAt", "tokenType": "kiosk" | "provisioning", "kioskId", "kioskCode" }`

Cihaz aktivdirsə `kiosk`, deyilsə `provisioning` token-i verilir.

### `POST /api/devices/me/pairing-code`

Provisioning token. Ekrandaki kodu yeniləyir → `{ "pairingCode", "expiresAt" }`

### `PUT /api/devices/me/printers`

Device token (provisioning və ya kiosk).

`{ "printers": [ { "name", "driverName", "isOnline", "color", "duplex", "a3" } ] }` → `204`

---

## Admin autentifikasiyası

### `POST /api/auth/admin/login`

Anonim. `{ "email", "password" }`

→ `{ "accessToken", "accessTokenExpiresAt", "admin" }` + `Set-Cookie: neoprint_admin_rt`

**Refresh token cavab gövdəsində yoxdur** — yalnız cookie-də.

5 uğursuz cəhddən sonra hesab 15 dəqiqəyə bloklanır; mesaj nə qədər qaldığını
deyir.

### `POST /api/auth/admin/refresh`

Cookie ilə, gövdə yoxdur. → yeni `accessToken` + yeni cookie.

### `POST /api/auth/admin/logout`

→ `204`, cookie silinir.

### `POST /api/auth/admin/change-password`

Admin token. `{ "currentPassword", "newPassword" }` → `204`.

**Bütün sessiyalar ləğv olunur**, o cümlədən cari sessiya. Seed edilmiş
SuperAdmin ilk girişdə bunu etməlidir (`admin.mustChangePassword`).

### `GET /api/admin/me`

Admin token. → `AdminSummary`:

```json
{
  "id", "email", "fullName",
  "roles": ["SuperAdmin"],
  "permissions": ["kiosks.view", "..."],
  "allKiosks": true,
  "kioskIds": [],
  "mustChangePassword": false
}
```

İcazələr **token-dən yox, resolver-dən** oxunur — rol dəyişikliyi yenidən login
olmadan görünür.

### `GET /api/admin/permissions`

`roles.manage`. İcazə kataloqu, rol redaktorunun qrupları ilə.

→ `[ { "key": "kiosks", "title": "Kiosklar", "permissions": ["kiosks.view", ...] } ]`

---

## Rollar

Hamısı `roles.manage` tələb edir.

| Metod | Yol | Nəticə |
|---|---|---|
| `GET` | `/api/admin/roles` | `RoleResponse[]` |
| `GET` | `/api/admin/roles/{id}` | `RoleResponse` / `404` |
| `POST` | `/api/admin/roles` | `RoleResponse` |
| `PUT` | `/api/admin/roles/{id}` | `RoleResponse` / `404` |
| `POST` | `/api/admin/roles/{id}/clone` | `RoleResponse` / `404` |
| `DELETE` | `/api/admin/roles/{id}` | `204` / `404` |

`RoleResponse`: `{ "id", "name", "description", "permissions", "isSystem", "userCount", "updatedAt" }`

Gövdə (create / update): `{ "name", "description", "permissions": [] }`
Gövdə (clone): `{ "name" }`

Qaydalar:
- Ad unikaldır → `409`.
- **Sistem rolu** (SuperAdmin) redaktə və silinə bilməz → `409`. Klonlanıb
  redaktə edilə bilər; klon **effektiv** icazələri alır, ona görə SuperAdmin-in
  klonu boş deyil.
- İstifadədə olan rol silinə bilməz → `409` (nə qədər istifadəçi olduğunu
  deyir).
- `permissions` kataloqdan kənar açar saxlaya bilməz → `409`.
- Dəyişiklik **dərhal** tətbiq olunur: resolver keşi təmizlənir və açıq
  panellərə SignalR ilə `PermissionsChanged` göndərilir.

---

## Kiosklar

### `GET /api/admin/kiosks`

`kiosks.view`. **Çağıranın kiosk əhatəsinə görə filtrlənir** — əhatəsi məhdud
operator yalnız öz kiosklarını görür.

→ `[ { "id", "code", "name", "status", "platform", "address", "defaultLanguage",
      "activeDeviceId", "appVersion", "selectedPrinter", "lastHeartbeatAt" } ]`

`status`: `NotActivated` | `NotConfigured` | `Online` | `Paused` | `Offline` |
`Maintenance` | `Disabled`

Kiosk CRUD-u Addım 2-dədir.

---

## Cihazların idarəsi

### `GET /api/admin/devices?status=Pending`

`devices.view`. `status`: `Pending` | `Active` | `Revoked` | `Expired` (boş
buraxılsa hamısı).

→ `[ { "id", "status", "platform", "appVersion", "agentVersion", "hostname",
      "os", "lastIp", "registeredAt", "lastSeenAt", "kioskId", "kioskCode",
      "hasValidPairingCode", "pairingCodeExpiresAt", "reportedPrinters": [] } ]`

**Pairing kodu qaytarılmır** — yalnız hash kimi saxlanılır. Panel kodun
etibarlı olub-olmadığını göstərir, kodu isə operator kiosk ekranından oxuyur.

### `POST /api/admin/devices/lookup`

`kiosks.activate`. `{ "codeOrQr": "K7QM-4XPT" }` — bare kod və ya
`neoprint://activate/{deviceId}?code=...` QR məzmunu.

→ `DeviceDetails`: `{ "deviceId", "status", "platform", "appVersion",
"fingerprint" {...}, "printers": [...], "lastIp", "registeredAt",
"lastSeenAt", "kioskId", "kioskCode" }`

Tapılmasa `404` — **axtarılan kod cavabda qaytarılmır**.

### `POST /api/admin/devices/{id}/activate`

`kiosks.activate`. `Idempotency-Key` qəbul edir.

```json
{
  "kioskId": null,
  "newKiosk": {
    "name": "Mərkəz filialı",
    "code": null,
    "lat": 40.4093, "lng": 49.8671,
    "address": "Bakı, ...",
    "pricingProfileId": null,
    "maxFileSizeMb": null, "maxPages": null, "queueDisplayLimit": null,
    "defaultLanguage": "az",
    "fiscalTerminalId": null,
    "paperCapacityA4": 500, "paperCapacityA3": 250,
    "adminPin": "135790"
  },
  "selectedPrinterName": "HP LaserJet"
}
```

`kioskId` **və ya** `newKiosk` verilir. `code` boş buraxılsa növbəti `KSK-nnnn`
avtomatik verilir. `adminPin` ən azı 4 rəqəm; kioska yalnız PBKDF2 hash-i gedir.

→ `{ "deviceId", "kioskId", "kioskCode", "kioskStatus" }`

- Mövcud kiosk çağıranın əhatəsindən kənardırsa `403`.
- Kioskda artıq cihaz varsa **köhnə cihaz ləğv olunur** (avadanlıq dəyişmə
  axını) — kiosk kodunu, konfiqurasiyasını və sayğaclarını saxlayır.
- Printer seçilməsə kiosk `NotConfigured` statusunda aktivləşir.
- Təkrar çağırış (eyni cihaz, eyni kiosk) xəta deyil.

### `POST /api/admin/devices/{id}/revoke`

`devices.revoke`. `Idempotency-Key` qəbul edir. `{ "reason": "..." }` → `204`

Səbəb audit log-a yazılır. Cihaz **dərhal** etibarsız olur: mövcud token-i
növbəti sorğuda 403 alır. Kiosk `NotActivated` olur, hər şeyini saxlayır.

### Enrollment token-lər

`devices.enroll`.

| Metod | Yol |
|---|---|
| `GET` | `/api/admin/enrollment-tokens` |
| `POST` | `/api/admin/enrollment-tokens` (`Idempotency-Key`) |
| `DELETE` | `/api/admin/enrollment-tokens/{id}` |

`POST` gövdəsi: `{ "kioskId", "newKiosk", "ttlHours" }`

→ `{ "id", "token", "expiresAt", "kioskId", "kioskCode", "windowsCommand", "linuxCommand" }`

**`token` yalnız bu bir dəfə qaytarılır** — serverdə yalnız hash-i saxlanılır.
`windowsCommand` / `linuxCommand` provisioning skripti üçün hazır əmrdir.

`GET` token-in özünü qaytarmır:
`{ "id", "kioskId", "kioskCode", "newKioskName", "createdAt", "expiresAt",
"usedByDeviceId", "revokedAt", "isUsable" }`

Token **bir dəfəlikdir**.

---

## Feature flag-lar

### `GET /api/features`

Anonim və ya hər hansı token. Çağıran üçün qiymətləndirilmiş flag-lar.

→ `{ "scan.enabled": true, "wallet.enabled": false }`

Qiymətləndirmə sırası: kiosk override → allowlist → faiz (yalnız qeydiyyatlı
istifadəçi üçün; anonim kiosk sessiyasında default qərar verir) → default.

### `GET /api/admin/features`

`features.manage`. Tam konfiqurasiya.

→ `[ { "id", "key", "description", "defaultEnabled", "platforms",
      "kioskOverrides": { "<kioskId>": true }, "userAllowlist": [],
      "rolloutPercent", "updatedAt" } ]`

### `PUT /api/admin/features/{key}`

`features.manage`.

`{ "defaultEnabled", "rolloutPercent", "kioskOverrides", "userAllowlist" }` →
yenilənmiş flag.

`[RequireFeature]` ilə qorunan endpoint söndürülmüş flag üçün `403` qaytarır.

---

## SignalR hub-ları

| Hub | Kim | Nə üçün |
|---|---|---|
| `/hubs/provisioning` | Provisioning token | Aktivləşdirmə anında cihaza xəbər verilir — cihaz kod ekranından özü keçir |
| `/hubs/admin` | Admin token | `PermissionsChanged`, `DeviceRevoked` |

Redis backplane ilə işləyir, ona görə bir necə API instansiyası eyni qrupları
xidmət edə bilir.

---

## Health check-lər

| Yol | Nə yoxlayır |
|---|---|
| `/health/live` | Proses qalxıb (heç bir asılılıq yoxlanmır) |
| `/health/ready` | Mongo, Redis, RabbitMQ, MassTransit, OpenBao |
| `/health` | Hamısı, detallı JSON cavabla |

Production-da OpenBao-da məcburi açar olmasa API **başlamır**.

---

## Hələ mövcud olmayan endpoint-lər

Planın Bölmə 6-dakı siyahısından bunlar sonrakı addımlardadır: fayllar və
konvertasiya (Addım 2), print job-lar və queue (Addım 3), ödəniş / refund /
wallet / fiskal (Addım 4), skan və paylaşım (Addım 6), telemetriya və operator
paneli (Addım 7). Admin istifadəçiləri, tənzimləmələr və hüquqi sənədlərin
idarəsi Addım 1-in qalan işidir.
