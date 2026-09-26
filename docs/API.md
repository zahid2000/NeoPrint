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

### `GET /api/devices/me`

Cihaz token-i (provisioning və ya kiosk). Cihazın öz vəziyyəti.

→ `{ "deviceId", "status", "kioskId", "kioskCode", "hasValidPairingCode",
     "pairingCodeExpiresAt" }`

`status`: `Pending` | `Active` | `Revoked` | `Expired`

Activation ekranı **bunu poll edir**: aktivləşmə baş verib-vermədiyini
yoxlamağın ucuz yolu. `challenge` + `token` ilə yoxlamaq hər dövrdə iki sorğu
deməkdir və onlar giriş endpoint-ləri ilə birlikdə **10 sorğu/dəqiqə** limitini
paylaşır — bu endpoint isə ümumi limitdədir (120/dəqiqə), ona görə 5 saniyəlik
poll rahat keçir.

**Pairing kodunun özü qaytarılmır** — bazada yalnız hash saxlanılır və kod
ekranda artıq mövcuddur. `hasValidPairingCode: false` olanda kiosk
`POST /api/devices/me/pairing-code` ilə yenisini alır.

Ləğv edilmiş cihaz `403` alır (hər sorğuda cihazın aktivliyi yoxlanılır) və
Activation ekranına qayıdır.

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

Rədd edilmiş hər hal **`401`** qaytarır və hamısı eyni mətni verir
(«Sessiya bitib. Yenidən daxil olun.»): cookie yoxdur, token tanınmır, vaxtı
keçib, artıq bir dəfə istifadə olunub (rotasiya — bu halda bütün ailə ləğv
edilir) və ya hesab deaktiv edilib. Hallar bir-birindən ayrılmır — fərq yalnız
hücumçuya faydalıdır, detal log-a yazılır.

`400` deyil, məhz `401`: sorğu düzgün formalaşdırılıb, sadəcə sessiya bitib.
`400` alan klient sorğusunun səhv olduğunu düşünüb təkrar cəhd edər; `401`
isə yenidən giriş deməkdir.

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

## Admin hesabları

Hamısı `admins.manage` tələb edir.

| Metod | Yol | Nəticə |
|---|---|---|
| `GET` | `/api/admin/admins` | `AdminUserResponse[]` |
| `GET` | `/api/admin/admins/{id}` | `AdminUserResponse` / `404` |
| `POST` | `/api/admin/admins` | `AdminUserResponse` (idempotent) |
| `PUT` | `/api/admin/admins/{id}` | `AdminUserResponse` / `404` |
| `POST` | `/api/admin/admins/{id}/active` | `204` / `404` |
| `POST` | `/api/admin/admins/{id}/reset-password` | `204` / `404` |

`AdminUserResponse`: `{ "id", "email", "fullName", "roleIds", "roleNames",
"allKiosks", "kioskIds", "isActive", "mustChangePassword", "isLockedOut",
"lastLoginAt", "createdAt" }`

**Şifrə hash-i və uğursuz giriş sayğacı cavabda yoxdur** — panelin onlara
ehtiyacı yoxdur (`docs/SECURITY_THREAT_MODEL.md`).

Gövdə (create): `{ "email", "fullName", "password", "roleIds": [], "allKiosks", "kioskIds": [] }`
Gövdə (update): `{ "fullName", "roleIds": [], "allKiosks", "kioskIds": [] }` — **email dəyişmir**, hesabı o müəyyən edir.
Gövdə (active): `{ "isActive", "reason" }` — səbəb audit log-a yazılır.
Gövdə (reset-password): `{ "newPassword" }`

Qaydalar:
- Email unikaldır → `409`.
- Ən azı bir rol məcburidir; mövcud olmayan rol → `400`.
- Əhatəsi məhdud hesab (`allKiosks: false`) ən azı bir kiosk saxlamalıdır.
- Şifrə ən azı **12** simvol (`change-password` ilə eyni qayda).
- Yeni hesab və şifrə sıfırlanması `mustChangePassword: true` qoyur.
- **Son aktiv SuperAdmin** deaktiv edilə və ya rolundan çıxarıla bilməz →
  `409`. Panelə heç kimin girə bilməməsi bərpa olunmayan vəziyyətdir.
- Deaktivləşdirmə və şifrə sıfırlanması hesabın **bütün refresh token-lərini
  ləğv edir** və icazə keşini təmizləyir: deaktiv hesabın açıq access token-i
  ilk sorğuda `403` alır (`Admin` policy-də `AdminIsActiveRequirement`).

---

## Tənzimləmələr

### `GET /api/admin/settings`

`settings.manage`. Bütün parametrlər, hər biri sənədləşdirilmiş default dəyəri
ilə.

→ `[ { "key", "value", "defaultValue", "valueType", "description", "isDefault",
      "updatedAt", "updatedBy" } ]`

`valueType`: `String` | `Integer` | `Decimal` | `Boolean` | `Json`

### `PUT /api/admin/settings`

`settings.manage`. `{ "values": { "Queue.DisplayLimit": "5" } }` → yenilənmiş
bütün siyahı.

Bir sorğuda bir necə parametr, çünki panel sahə deyil **səhifə** yadda saxlayır;
yarım tətbiq olunmuş səhifə heç nədən pisdir, ona görə bir tranzaksiyada gedir.
Kataloqda olmayan açar və ya tipi uyğun gəlməyən dəyər **heç nə yazılmadan**
rədd edilir → `400`.

Saxlama müddətləri də buradan idarə olunur: `Retention.StateTransitionDays`
(default `365`) və `Retention.ProviderEventDays` (default `180`). Dəyişiklik
API-nin növbəti başlanğıcında Mongo TTL indeksinə tətbiq olunur
(`docs/DATA_RETENTION.md`).

---

## Hüquqi sənədlərin idarəsi

### `GET /api/admin/legal-documents`

`legal.manage`. Bütün dərc olunmuş versiyalar, yenisi əvvəldə.

→ `[ { "id", "docType", "version", "language", "content", "publishedAt",
      "publishedBy", "isCurrent", "acceptances" } ]`

### `POST /api/admin/legal-documents`

`legal.manage`, idempotent. `{ "docType", "version", "language", "content" }` →
`LegalDocumentResponse`.

Sənəd **redaktə edilmir, yeni versiya dərc olunur**: razılıq qeydi istifadəçinin
həqiqətən gördüyü versiyaya istinad edir (Bölmə 2.13). Eyni tip, dil və versiya
təkrarlana bilməz → `409`. Köhnə versiyalar saxlanılır
(`docs/DATA_RETENTION.md`).

Yeni versiya dərc olunan kimi `GET /api/consents/pending` onu qaytarır —
istifadəçilər yenidən razılıq verməlidir.

---

## Kiosk (kiosk token-i ilə)

Kiosk tətbiqi **universal build-dir**: onu bu kiosk edən hər şey backend-dən
gəlir (Bölmə 7.11). Hər iki endpoint kiosk token-i tələb edir və hər sorğuda
cihazın hələ aktiv olduğunu yoxlayır — ləğv edilmiş cihaz `403` alır və
Activation ekranına qayıdır.

### `GET /api/kiosks/me/config`

```json
{
  "kioskId", "code", "name", "status", "defaultLanguage",
  "selectedPrinter", "effectiveCapabilities": { "color", "duplex", "a3" },
  "availablePrinters": [ { "name", "driverName", "isOnline", "detectedCapabilities" } ],
  "adminPinHash",
  "queueDisplayLimit", "maxFileSizeMb", "maxPages", "pricingProfileId",
  "paperStock": { "A4": { "capacity", "current" } },
  "features": { "scan.enabled": true },
  "heartbeatSeconds", "cacheTtlHours", "serverTime"
}
```

**Backend secret-i yoxdur** — admin PIN-i kiosk üçün yalnız PBKDF2 hash kimi
gedir, cihaz onunla tutuşdurur (Bölmə 2.13, 7.11).

`cacheTtlHours` — backend əlçatmaz olanda bu konfiqurasiya nə qədər müddət
etibarlıdır. Müddət keçəndən sonra kiosk sifariş qəbul etmir
(`docs/OFFLINE_BEHAVIOR.md`). `serverTime` cihazın saatı sürüşəndə istinad
nöqtəsidir.

Cihaz heç bir kioska bağlı deyilsə → `403`.

### `POST /api/kiosks/heartbeat`

`{ "appVersion", "agentVersion", "printerOnline" }` →
`{ "status", "nextInSeconds", "configurationChangedAt", "serverTime" }`

Kiosk bununla **geri qayıdır** da: heartbeat-i dayandığı üçün `Offline`
işarələnmiş kiosk növbəti heartbeat-də xidmətə qayıdır, operator müdaxiləsi
olmadan (Bölmə 7.9).

`nextInSeconds` — növbəti heartbeat nə vaxt; interval panelden dəyişdirilir,
kiosk yenidən yayımlanmır. `configurationChangedAt` irəli gedibsə kiosk
konfiqurasiyanı yenidən çəkir — bütün konfiqurasiyanı poll etmək lazım deyil.

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

## Fayllar (`/api/files`)

Hamısı **istifadəçi token-i** tələb edir (`files/formats` istisna). Başqasının
faylı **`404`** qaytarır, `403` yox: `403` id-nin mövcudluğunu təsdiqləyər və
sayma hücumuna imkan verər.

### `POST /api/files`

`multipart/form-data`:

| Sahə | Tip | Məna |
|---|---|---|
| `file` | fayl | Məcburi |
| `kioskId` | GUID | Seçilibsə həmin kioskun ölçü limiti tətbiq olunur |
| `source` | mətn | `Uploaded` (default), `Scan`, `Layout`, `Usb`, `Shared` |
| `isSensitive` | bool | Şəxsi sənəd (skan) — avtomatik silinmə defolt olaraq `AfterPrint` olur |

→ `201` + fayl obyekti.

**Format uzantı ilə deyil, məzmun imzası ilə müəyyən edilir.** Uzantı yalnız
konteyner formatlarında (OOXML/OpenDocument, köhnə Office) qeyri-müəyyənliyi
həll edir. Məzmunla uzantı ziddiyyət təşkil edərsə fayl rədd olunur.

Ölçü limiti **iki dəfə** yoxlanılır: elan edilmiş uzunluqla (oxumadan əvvəl) və
faktiki uzunluqla (yazıldıqdan sonra).

Cavabda `status` = `Pending`: konvertasiya sorğudan sonra, ayrıca prosesdə
işləyir. `Ready` olandan sonra `pageCount` doludur.

### `GET /api/files?page=&pageSize=`

→ `{ "items": [...], "total": n }`. Yenidən köhnəyə doğru, silinmişlər yoxdur.

### `GET /api/files/{id}`

→ Fayl obyekti: `id`, `fileName`, `extension`, `mediaType`, `source`, `status`
(`Pending` / `Converting` / `Ready` / `Failed`), `failureReason`, `sizeBytes`,
`pageCount`, `isSensitive`, `autoDelete`, `versionCount`, `createdAt`.

### `GET /api/files/{id}/content`

Faylın özü, deşifrə olunmuş. Range dəstəklənir.

**Pre-signed link verilmir:** store-dakı baytlar şifrəlidir (link yararsız
olardı) və link sahiblik yoxlamasını yan keçərdi.

### `GET /api/files/{id}/pdf`

Çevrilmiş PDF — preview, səhifə sayı və çap bunu işlədir. Fayl hələ
çevrilirsə və ya uğursuz olubsa `400` + izah.

### `PUT /api/files/{id}/auto-delete`

`{ "rule": "Never" | "AfterPrint" | "AfterOneDay" | "AfterSevenDays" | "AfterThirtyDays" }`
→ fayl obyekti.

### `DELETE /api/files/{id}`

→ `204`. Obyektlər **bütün** provayderlərdən silinir (mirror və migrasiya
nüsxələri daxil).

### `GET /api/files/formats`

Anonim. Qəbul edilən uzantıların siyahısı — mobil fayl seçicisi bununla
filtrləyir.


---

## Storage idarəsi (`/api/admin/storage`)

Hamısı **`storage.manage`** icazəsi tələb edir (defolt olaraq yalnız
SuperAdmin-də var). Dəyişiklik edən hər əməliyyat `audit_logs`-a yazılır.

**Secret key heç vaxt serverdən çıxmır** — nə açıq, nə şifrəli. Access key yalnız
son 4 simvolu ilə göstərilir (`accessKeyHint: "••••test"`).

### Provayderlər

| Metod | Yol | Məna |
|---|---|---|
| `GET` | `providers` | Siyahı: ad, tip, endpoint, bucket-lər, rol (`Primary` / `Secondary` / `Inactive`), sağlamlıq, son yoxlama |
| `POST` | `providers/test` | Hələ saxlanmamış credential-ları yoxlayır (formadakı "Bağlantını yoxla"). Hər bucket-də yazma → oxuma → müqayisə → silmə |
| `POST` | `providers` | Əlavə edir. **Bağlantı testi keçməzsə saxlanmır** (`400`). Yeni provayder `Inactive` olur |
| `PUT` | `providers/{id}` | Redaktə. `secretKey` boşdursa saxlanılan qalır (forma onu heç vaxt almır). İstifadədəki provayder yeni parametrlərlə testdən keçməlidir |
| `POST` | `providers/{id}/test` | Saxlanmış provayderi yoxlayır və nəticəni qeyd edir |
| `PUT` | `providers/{id}/role` | `{ "role": "Primary" \| "Secondary" \| "Inactive" }`. Rolu tutan digər provayder əvvəlcə `Inactive` olur — heç vaxt iki Primary olmur. Primary birbaşa söndürülə bilməz; aktiv sinxronizasiya varsa rol dəyişmir |

Provayder gövdəsi:

```json
{
  "name": "Scality RING",
  "type": "Scality",
  "endpoint": "https://s3.example.az",
  "region": "us-east-1",
  "useSsl": true,
  "accessKey": "…",
  "secretKey": "…",
  "buckets": { "files": "neoprint-files", "receipts": "neoprint-receipts", "temp": "neoprint-temp" }
}
```

### Sinxronizasiya

| Metod | Yol | Məna |
|---|---|---|
| `GET` | `sync-jobs` | Son 50 iş |
| `GET` | `sync-jobs/{id}` | Bir iş: status, obyekt və bayt sayğacları, checkpoint bucket-i, xəta səbəbi |
| `POST` | `sync-jobs` | `{ "mode": "Migration" \| "Mirror", "targetId", "switchPrimaryOnComplete" }` — mənbə həmişə cari Primary-dir. **Eyni anda yalnız bir aktiv iş.** Mirror hədəfi dərhal `Secondary` olur |
| `POST` | `sync-jobs/{id}/pause` \| `resume` \| `cancel` | Dayandır / davam et / ləğv et |
| `POST` | `sync-jobs/{id}/retry-failures` | Uğursuz işi yenidən verification-a göndərir — hədəfdə çatışmayan **hər şey** (uğursuzlar dəqiq) köçürülür, yalnız sonra keçid olur |
| `GET` | `sync-jobs/{id}/failures` | Köçürülə bilməyən obyektlər (bucket, key, xəta, cəhd sayı, həll olunubmu) |
| `POST` | `rewrap` | KEK rotasiyasından sonra bütün obyektləri yeni açara keçirir (fonda, provayder × bucket üzrə) → `202` |

**Canlı progress:** `/hubs/admin` üzərindən `StorageSyncProgress` hadisəsi (iş
başına saniyədə ən çox 2 dəfə; status dəyişikliyi həmişə göndərilir):
`{ jobId, mode, status, totalObjects, copiedObjects, failedObjects, totalBytes, copiedBytes, failureReason, at }`.

Migrasiya **restart-safe**-dir: hər obyektdən sonra checkpoint yazılır, worker
ölsə lease 2 dəqiqədə bitir və dəqiqəlik yoxlama işi checkpoint-dən davam
etdirir.

---

## Qiymət (`/api/pricing`, `/api/admin/pricing-profiles`)

### `POST /api/pricing/quote`

İstənilən daxil olmuş çağıran (mobil, kiosk, panel).

```json
{
  "kioskId": "…",              // null → default profil
  "options": { "pageCount": 10, "copies": 1, "duplex": false, "color": false, "paper": "A4", "pagesPerSheet": 1 },
  "promoCode": null,           // qəbul olunur, Addım 4-dən tətbiq edilir
  "useBonus": false            // qəbul olunur, Addım 4-dən tətbiq edilir
}
```

→ `{ profileId, profileName, price: { pages, sides, sheets, copies, color, duplex, paper, pricePerSide, paperMultiplier, basePrice, discountPercent, volumeDiscount, promoDiscount, bonusPages, minimumApplied, finalPrice, currency } }`

Formul (plan 2.3), yuvarlaqlaşdırma 2 onluq, half-to-even:

```
sides  = ceil(pageCount / pagesPerSheet) * copies
sheets = duplex ? ceil(sides / 2) : sides
base   = sides * qiymət(rəng) * kağızƏmsalı
endirim = ən böyük pillə (sides >= MinSides) — pillələr toplanmır
final  = max(MinOrder, base − endirim)
```

Hesablama `NeoPrint.Pricing`-dədir və **kiosk da eyni kodu işlədir** — ekranda
görünən qiymət tutulanla eynidir. Sifariş yaradılanda qiymət **snapshot** kimi
saxlanılır (dərəcələrin kopyası ilə); profil sonradan dəyişsə də snapshot
dəyişmir.

### Admin (`pricing.manage`)

| Metod | Yol | Məna |
|---|---|---|
| `GET` | `/api/admin/pricing-profiles` | Siyahı, hər profilə bağlı kiosk sayı ilə; default birinci |
| `GET` | `…/{id}` | Profil + bu profillə qiymətlənən kiosklar (default profil üçün öz profili olmayanlar da) |
| `POST` | `…` | Yarat. A4 əmsalı məcburi, qiymətlər mənfi ola bilməz, endirim 0–100 arası (sərhədlər daxil deyil), pillə üz sayları unikal |
| `PUT` | `…/{id}` | Redaktə. Əvvəlki dəyərlər `History`-yə yazılır, `revision` artır; iki admin eyni anda redaktə etsə ikincisi `409` alır |
| `DELETE` | `…/{id}` | Default profil və kioska bağlı profil silinmir (`400`, neçə kioska bağlı olduğu yazılır) |
| `POST` | `…/{id}/default` | Default et — həmişə tam olaraq bir default qalır |
| `PUT` | `…/{id}/kiosks` | `{ "kioskIds": [...] }` — bir neçə kiosku eyni anda bu profilə bağla |
| `POST` | `…/calculate` | Kalkulyator: `{ "profileId" }` və ya saxlanmamış `{ "draft": {...} }` + `options` → qiymət təfərrüatı |

---

## Hələ mövcud olmayan endpoint-lər

Planın Bölmə 6-dakı siyahısından bunlar sonrakı addımlardadır: print job-lar və
queue (Addım 3), ödəniş / refund / wallet / fiskal (Addım 4), skan və paylaşım
(Addım 6), telemetriya və operator paneli (Addım 7).

Addım 2-nin qalan hissəsi: fayl redaktəsi (`POST files/{id}/edit`), səhifə
şəkilləri (`GET files/{id}/pages/{n}.png`), versiyalar və kiosk CRUD-u
(`POST pricing/quote`).
