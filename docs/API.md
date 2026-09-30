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

### Dil (`Accept-Language`)

Serverin istifadəçiyə yazdığı hər mətn (xəta, validasiya, fayl emalı səbəbi, versiya qeydi, saxlama testi nəticəsi) `Accept-Language` başlığındakı dildə qaytarılır: `az` (standart), `en`, `ru`. Çəkili siyahı dəstəklənir (`de-DE,ru;q=0.5` → `ru`); dəstəklənməyən dil → `az`. Cavabda `Content-Language` başlığı seçilən dili göstərir. Mobil tətbiq, admin paneli və kiosk öz **interfeys** dilini göndərir (brauzerin dilini yox). Email isə həmişə alıcının öz dilində (`users.language`) gedir.

Mətnlər `backend/src/NeoPrint.Application/Localization/messages.{az,en,ru}.json` kataloqlarındadır. Bazada saxlanan səbəblər (məs. `files.failureReason`) açar + arqument kimi saxlanır və oxuyanın dilində yazılır; Addım 2-dən qalan köhnə mətnlər olduğu kimi göstərilir.

### Xəta formatı

RFC 7807 ProblemDetails; `title` və `detail` sorğunun dilindədir, `code` isə mesajın dildən asılı olmayan açarıdır (müştəri mətni parse etmədən konkret qaydaya reaksiya verə bilər):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Sorğu rədd edildi",
  "status": 409,
  "detail": "Bu cihaz ləğv edilib. Kiosk tətbiqini yenidən quraşdırın — yeni cihaz açarı yaradılacaq.",
  "code": "devices.revokedReinstall",
  "instance": "/api/devices/register",
  "correlationId": "69068640f208405dbe922627cf9b1638",
  "traceId": "0HNOQ1TUTAQ9H"
}
```

Validasiya xətasında (`400`) `errors` sahə üzrə mətnlərdir; tək xəta olanda `code` da verilir.

| Status | Nə vaxt |
|---|---|
| `400` | Validasiya (`errors` sahəsi ilə: sahə → mesaj siyahısı) |
| `401` | Token yoxdur və ya etibarsızdır |
| `403` | İcazə yoxdur, kiosk əhatəsindən kənardır, və ya cihaz ləğv edilib |
| `404` | Tapılmadı — **axtarılan dəyər cavabda qaytarılmır** |
| `409` | Domen qaydası pozuldu, konkurensiya, və ya idempotency konflikti |
| `429` | Rate limit |

`detail` sahəsi istifadəçiyə göstərilmək üçün yazılır və Azərbaycan dilindədir.

### Audit səbəbi (`X-Audit-Reason`)

Admin panelində dağıdıcı əməliyyat (silmə, mənbəni təmizləmə, ləğv) səbəb tələb edir
(plan 8A.1). Səbəb `X-Audit-Reason` başlığında **URL-encoded** göndərilir (başlıqlar UTF-8
deyil) və handler öz səbəbini yazmayıbsa `audit_logs.Reason`-a düşür. Yalnız admin
token-i ilə qəbul olunur, 500 simvoldan uzunu kəsilir.

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

## Dashboard (`/api/admin/dashboard`)

### `GET /api/admin/dashboard`

İcazə: `dashboard.view`. Plan Bölmə 8A.3-ün kartları addım-addım əlavə olunur; Addım 2:

```json
{
  "files": {
    "count": 124,
    "totalBytes": 734003200,
    "inProgress": 2,
    "failedLast24Hours": 1,
    "failedLast7Days": 3
  },
  "storage": {
    "providers": [
      { "id": "…", "name": "Local RustFS", "type": "RustFS", "role": "Primary",
        "healthStatus": "Healthy", "lastCheckedAt": "2026-09-27T16:40:00Z" }
    ],
    "activeSync": null,
    "lastSync": { "…": "StorageSyncJobResponse, storage bölməsindəki kimi" }
  }
}
```

- `files.totalBytes` — silinməmiş faylların orijinal ölçüləri (şifrələnmədən əvvəl).
- `failedLast24Hours` / `failedLast7Days` — `state_transitions`-dakı `StoredFile → Failed` keçidlərindən sayılır.
- `storage` yalnız `storage.manage` icazəsi olan hesaba qaytarılır (Admin rol şablonunda storage icazəsi yoxdur); provayder üçün endpoint, açar izi və bucket adları verilmir.
- Canlı yenilənmə: panel `/hubs/admin`-in `StorageSyncProgress`, `StorageSyncCompleted`, `StorageHealthChanged` hadisələrində sorğunu təkrarlayır.

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

### `POST /api/kiosks/me/analytics`

Kiosk token, `Idempotency-Key` — **paketin** açarı (itən cavabdan sonra eyni paket eyni açarla yenidən gəlir, bir dəfə saxlanılır). `{ "events": [{ "event", "sessionId", "at", "props": { … } }] }`, ən çox 200 hadisə → `202 { "accepted", "rejected" }`. Yalnız kataloqdakı hadisə və sahələr saxlanılır (plan 2.17): `screen_view {screen}`, `file_selected {format, sizeMb, pages}`, `conversion_failed {format, error}`, `options_set {copies, color, duplex, paper, pagesPerSheet, range}`, `price_shown {total, currency}`, `order_placed {sheets, jobCode}`, `print_completed {sheets, jobCode}`, `print_failed {sheets, error, jobCode}`, `flow_abandoned {lastScreen, seconds}`; başqa sahə (məs. fayl adı) və naməlum hadisə atılır, dəyər 64 simvola kəsilir. 7 gündən köhnə və ya 5 dəqiqədən çox gələcəkdə olan hadisə atılır. Pis hadisə bütün paketi rədd etmir. `analytics_events` — MongoDB time-series, `Analytics.RetentionDays` (180) sonra özü silinir.

### `POST /api/kiosks/heartbeat`

`{ "appVersion", "agentVersion", "printerOnline", "safeMode" }` →
`{ "status", "nextInSeconds", "configurationChangedAt", "serverTime" }`

`safeMode: true` — kiosk tətbiqi 10 dəqiqədə `Kiosk.CrashLoopThreshold` dəfə
çökdüyü üçün təhlükəsiz rejimdə açılıb ("Texniki xidmət", sifariş yoxdur;
Bölmə 7.12). `Online` kiosk `Maintenance`-ə keçir (səbəb `kiosks.safeMode`),
admin kioskunda `safeModeSince` görünür, yeni sifariş `printJobs.kioskNotTakingOrders`
ilə rədd olunur. `safeMode: false` gələndə yalnız təhlükəsiz rejimin gətirdiyi
`Maintenance` `Online`-a qayıdır; adminin özünün qoyduğu `Maintenance` qalır.

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
      "activeDeviceId", "appVersion", "selectedPrinter", "lastHeartbeatAt",
      "lat", "lng", "pricingProfileId" } ]` — `lat`/`lng` xəritə görünüşü üçündür.

`status`: `NotActivated` | `NotConfigured` | `Online` | `Paused` | `Offline` |
`Maintenance` | `Disabled`

**Null sahələr JSON-a yazılmır** (məs. cihazsız kioskda `activeDeviceId` yoxdur).

### Kiosk yarat / redaktə (Addım 2)

Hamısı çağıranın **kiosk əhatəsinə** tabedir: əhatədən kənar kiosk `404` qaytarır
(`403` yox — cavab kioskun mövcudluğunu təsdiqləmir). Hər dəyişiklik
`audit_logs`-a yazılır (admin PIN və onun hash-i heç vaxt yazılmır) və
`configurationChangedAt`-i yeniləyir — kiosk bunu növbəti heartbeat-də görüb
konfiqurasiyasını yenidən alır.

| Metod | Yol | İcazə | Məna |
|---|---|---|---|
| `GET` | `admin/kiosks/{id}` | `kiosks.view` | Detal (aşağıda) |
| `POST` | `admin/kiosks` | `kiosks.create` | Cihazsız kiosk → `201`, status `NotActivated`. `code` boşdursa növbəti `KSK-XXXX` |
| `PUT` | `admin/kiosks/{id}` | `kiosks.edit` | `{ name, lat, lng, address, defaultLanguage, queueDisplayLimit }` |
| `DELETE` | `admin/kiosks/{id}` | `kiosks.delete` | → `204`. **Yalnız heç vaxt cihaz bağlanmamış** kiosk; tarixçəsi olan `400` |
| `PUT` | `admin/kiosks/{id}/limits` | `kiosks.limits.manage` | `{ maxFileSizeMb?, maxPages? }` — `null` = "Default istifadə et" |
| `PUT` | `admin/kiosks/{id}/pricing-profile` | `pricing.manage` | `{ pricingProfileId? }` — `null` = default profil; mövcud olmayan profil `400` |
| `PUT` | `admin/kiosks/{id}/fiscal-terminal` | `fiscal.manage` | `{ fiscalTerminalId? }` |
| `PUT` | `admin/kiosks/{id}/paper/capacity` | `kiosks.edit` | `{ a4, a3 }` (0–100000 vərəq). Kapasitet azaldılanda sayğac ona endirilir |
| `POST` | `admin/kiosks/{id}/admin-pin` | `kiosks.edit` | `{ pin }` — 4–12 rəqəm; yalnız hash saxlanılır |
| `GET` | `admin/kiosks/{id}/devices` | `devices.view` | Cihaz tarixçəsi: bağlı və ayrılmış bütün cihazlar (`isActive`, platforma, versiya, hostname, aktivləşdirmə / ayrılma vaxtı və səbəbi, son əlaqə) |

**POST gövdəsi:**

```json
{
  "name": "Nizami küçəsi",
  "code": null,
  "lat": 40.4093, "lng": 49.8671, "address": "Nizami küç. 10, Bakı",
  "defaultLanguage": "az",
  "queueDisplayLimit": 5,
  "maxFileSizeMb": null, "maxPages": null,
  "pricingProfileId": null,
  "fiscalTerminalId": null,
  "paperCapacityA4": 500, "paperCapacityA3": 250,
  "adminPin": "482915"
}
```

Qaydalar: ad və ünvan məcburi; `queueDisplayLimit` 3 və ya 5; `defaultLanguage`
az / en / ru; limitlər 1–2048 MB və 1–10000 səhifə; kod `KSK-0000` formatında və unikal.

**Detal cavabı:** `id, code, name, status, lat, lng, address, defaultLanguage,
queueDisplayLimit, maxFileSizeMb { override, default, effective }, maxPages { … },
pricingProfileId, pricingProfileName` (bağlı və ya default profil)`, fiscalTerminalId,
paper { A4: { capacity, current, lastRefillAt }, A3: { … } }, hasAdminPin,
activeDeviceId, platform, appVersion, selectedPrinter, lastHeartbeatAt,
configurationChangedAt, createdAt, revision`. **PIN heç vaxt qaytarılmır.**

Printerlər, əmrlər, telemetriya və status əməliyyatları kiosk tətbiqi ilə birlikdə
Addım 3-dədir.

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
| `/hubs/admin` | Admin token | `PermissionsChanged`, `DeviceRevoked`, `QueueUpdated` (yalnız hesabın əhatəsindəki kiosklar üçün) |
| `/hubs/kiosk` | Kiosk token | Qrup `kiosk:{kioskId}` (token-dəki kiosk). `QueueUpdated` (`{kioskId, queue[]}`), `JobAdded`, `JobCancelled` (`{kioskId, jobId}`), `PrinterConfigChanged`, `KioskConfigChanged`, `KioskCommand` |

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

### `GET /api/files/{id}/pages/{n}.png?width=`

Bir səhifə PNG kimi (mobil "Fayllarım" önizləməsi). `n` 1-dən başlayır.
`width` 320 / 640 / 960 / 1280 / 1600-dən ən yaxın böyüyə yuvarlanır (default 960;
hündürlük səhifənin nisbətinə görə). Fayl `Ready` olmalıdır, əks halda `400` +
izah; olmayan səhifə `400`; başqasının faylı `404`.

`Cache-Control: private, max-age=86400` və PDF məzmununa bağlı `ETag` — eyni
səhifə üçün `If-None-Match` göndərilsə `304`, yenidən render olunmur.

### `POST /api/files/{id}/edit`

Redaktəni **yeni versiya** kimi saxlayır (plan 2.2). Orijinal toxunulmaz qalır;
fayl `Pending` olur və upload kimi yenidən çevrilir — səhifə sayı, önizləmə və
qiymət redaktəyə uyğunlaşır. Gövdədə **yalnız biri**:

```json
{ "pages": [ { "page": 3, "rotate": 90 }, { "page": 1, "rotate": 0 } ] }
{ "image": { "rotate": 90, "crop": { "x": 0, "y": 0, "width": 0.5, "height": 1 },
             "grayscale": true, "brightness": 10, "contrast": -5 } }
{ "text": "TXT faylının yeni məzmunu" }
```

- `pages` — nəticənin səhifələri, sırası ilə: göstərilməyən səhifə silinir, sıra
  dəyişir, `rotate` (0/90/180/270, saat əqrəbi ilə) fırladır. Cari PDF üzərində
  işləyir, ona görə Office faylları üçün də (konvertasiyadan sonra) — nəticə PDF versiyadır.
- `image` — yalnız şəkil faylları: kəsmə (0–1 nisbətlər), fırlatma, ağ-qara,
  parlaqlıq / kontrast (-100…100), bu ardıcıllıqla. JPEG/PNG/WebP öz formatında,
  qalanları PNG kimi yazılır.
- `text` — yalnız TXT (ən çox 1 000 000 simvol).
- PS / PCL / XPS redaktə olunmur (`400`) — çapda səhifə aralığı seçilir.
- Fayl `Ready` deyilsə `400`. "Səhifədə yerləşdir" `files/layout`-dadır (plan 2.11).

Ən çox `Files.VersionsToKeep` (default 5) versiya saxlanılır; köhnələri storage-dan
silinir. Əvəz olunan çevrilmiş PDF də silinir.

### `GET /api/files/{id}/versions`

→ `[ { "number", "extension", "sizeBytes", "createdAt", "note", "isCurrent" } ]`, yenisi əvvəldə.

### `GET /api/files/{id}/versions/{n}/content`

Bir versiyanın məzmunu, deşifrə olunmuş.

### `PUT /api/files/{id}/auto-delete`

`{ "rule": "Never" | "AfterPrint" | "AfterOneDay" | "AfterSevenDays" | "AfterThirtyDays" }`
→ fayl obyekti.

### `DELETE /api/files/{id}`

→ `204`. Obyektlər **bütün** provayderlərdən silinir (mirror və migrasiya
nüsxələri daxil).

### `GET /api/files/formats`

Anonim. **Hazırda aktiv olan** uzantıların siyahısı (`Files.AllowedFormats`; admin-in
söndürdüyü format burada yoxdur) — mobil fayl seçicisi bununla filtrləyir.

### `GET /api/admin/files/formats` · `PUT /api/admin/files/formats`

`formats.manage` ("Fayl formatları" səhifəsi). GET →
`{ formats: [ { extension, mediaType, group, route, printerNative, enabled } ],
defaultMaxSizeMb, defaultMaxPages, userQuotaGb }`.
`group`: `Pdf` | `TextDocument` | `Spreadsheet` | `Presentation` | `Image` | `PageDescription`;
`route`: `None` | `LibreOffice` | `Ghostscript` | `Image`.

PUT `{ enabled: ["pdf", "docx", …], defaultMaxSizeMb, defaultMaxPages, userQuotaGb }`.
Kataloqda olmayan format (məs. `exe`) və boş siyahı `400` — format əlavə etmək
konvertor tələb edir, bu konfiqurasiya deyil. Söndürülmüş format upload-da
rədd olunur. Hər dəyişən ayar üçün ayrıca audit (`formats.update`).


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
| `POST` | `providers/{id}/set-primary` | Provayderi Primary edir (plan Bölmə 6). Əvvəlki Primary `Inactive` olur |
| `POST` | `providers/{id}/set-secondary` | Provayderi mirror-un Secondary-si edir. Əvvəlki Secondary `Inactive` olur |
| `PUT` | `providers/{id}/role` | `{ "role": "Primary" \| "Secondary" \| "Inactive" }` — yuxarıdakı iki qısa yolun ümumi forması; `Inactive` (söndürmə) yalnız bununla. Rolu tutan digər provayder əvvəlcə `Inactive` olur — heç vaxt iki Primary olmur. Primary birbaşa söndürülə bilməz; aktiv sinxronizasiya varsa rol dəyişmir |
| `DELETE` | `providers/{id}` | Provayderi silir → `204`. Yalnız `Inactive`, heç bir fayl ona istinad etmirsə və aktiv sinxronizasiya işində iştirak etmirsə (əks halda `400`). **İçindəki obyektlərə toxunmur** |

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

`type`: `RustFS` | `Scality` | `MinIO` (böyük-kiçik hərf fərqi yoxdur). Tip yalnız sağlamlıq yoxlamasının readiness endpoint-ini seçir (RustFS `/health/ready`, MinIO `/minio/health/ready`, Scality — yalnız imzalı S3 yoxlaması); bütün oxuma və yazılar eyni S3 API ilə gedir.

### Sinxronizasiya

| Metod | Yol | Məna |
|---|---|---|
| `GET` | `sync-jobs` | Son 50 iş |
| `GET` | `sync-jobs/{id}` | Bir iş: status, obyekt və bayt sayğacları, checkpoint bucket-i, xəta səbəbi; mənbənin təmizlənməsi: `sourcePurgeAllowedFrom` (tamamlanma + `Storage.SourceRetentionDays`, yalnız tamamlanmış migrasiyada), `sourcePurgeRequestedAt`, `sourcePurgedAt`, `sourcePurgedObjects`, `sourceKeptObjects` |
| `POST` | `sync-jobs` | `{ "mode": "Migration" \| "Mirror", "targetId", "switchPrimaryOnComplete" }` — mənbə həmişə cari Primary-dir. **Eyni anda yalnız bir aktiv iş.** Mirror hədəfi dərhal `Secondary` olur |
| `POST` | `sync-jobs/{id}/pause` \| `resume` \| `cancel` | Dayandır / davam et / ləğv et |
| `POST` | `sync-jobs/{id}/retry-failed` | Uğursuz işi yenidən verification-a göndərir — hədəfdə çatışmayan **hər şey** (uğursuzlar dəqiq) köçürülür, yalnız sonra keçid olur |
| `GET` | `sync-jobs/{id}/failures` | Köçürülə bilməyən obyektlər (bucket, key, xəta, cəhd sayı, həll olunubmu) |
| `POST` | `sync-jobs/{id}/purge-source` | **Mənbəni təmizlə** (admin təsdiqi). Yalnız tamamlanmış migrasiya, `sourcePurgeAllowedFrom` keçibsə və mənbə `Inactive`-dirsə (əks halda `400` — nə vaxt mümkün olacağı mesajda). Fonda işləyir; obyekt mənbədən **yalnız** hədəfdə eyni açar və ölçü ilə varsa və heç bir fayl qeydi onu mənbədən oxumursa silinir, qalanlar saxlanılır və sayılır. Təkrar çağırış eyni təsdiqdir (idempotent); yarıda qalan təmizləmə davam etdirilir |
| `POST` | `rewrap` | KEK rotasiyasından sonra bütün obyektləri yeni açara keçirir (fonda, provayder × bucket üzrə) → `202` |

**Canlı progress:** `/hubs/admin` üzərindən `StorageSyncProgress` hadisəsi (iş
başına saniyədə ən çox 2 dəfə; status dəyişikliyi həmişə göndərilir):
`{ jobId, mode, status, totalObjects, copiedObjects, failedObjects, totalBytes, copiedBytes, failureReason, at }`.
İş `Completed` olanda eyni məzmunla əlavə olaraq `StorageSyncCompleted` göndərilir (provayder
siyahısını yeniləmək üçün — Primary dəyişmiş ola bilər). Hər dəqiqəlik health check-də status
dəyişən provayder üçün `StorageHealthChanged`: `{ providerId, name, role, status, message, at }`.

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
| `POST` | `…/{id}/assign` | `{ "kioskIds": [...] }` — bir neçə kiosku eyni anda bu profilə bağla (plan Bölmə 6) |
| `POST` | `…/calculate` | Kalkulyator: `{ "profileId" }` və ya saxlanmamış `{ "draft": {...} }` + `options` → qiymət təfərrüatı |

---

## Çap sifarişləri (`/api/print-jobs`, Addım 3)

Kiosk token-i ilə. Hər endpoint yalnız token-dəki kioskun sifarişləri ilə işləyir; başqa kioskun sifarişi `404` qaytarır.

### `POST /api/print-jobs` (`Idempotency-Key` məcburi)

Flash-dan sifariş. Fayl kioskda qalır (şifrəli spool, Bölmə 7.5) — serverə yalnız ad, səhifə sayı və parametrlər gəlir.

```json
{
  "localFileName": "hesabat.pdf",
  "pageCount": 10,
  "options": { "copies": 1, "pageRange": "1-3,5", "color": false, "duplex": false,
               "paper": "A4", "orientation": "Auto", "pagesPerSheet": 1, "scaling": "Fit" },
  "dev": true
}
```

- Qiyməti server kioskun profilindən hesablayır və **snapshot** kimi saxlayır (`price`) — profil sonradan dəyişsə/silinsə də sifarişin qiyməti dəyişmir (T-047).
- Yoxlamalar: kiosk `Online` olmalıdır (`409 printJobs.kioskNotTakingOrders`); səhifə sayı kioskun limitindən çox deyil (`printJobs.tooManyPages`); printerin effektiv imkanları (`printJobs.colorUnavailable` / `duplexUnavailable` / `a3Unavailable`); parametrlər (nüsxə 1–50, kağız A4/A3, vərəqdə 1/2/4 səhifə, aralıq `1-3,5,8-10`); kağız: `Paper.BlockWhenInsufficient` açıqdırsa, lotokdakı vərəq − hələ çap olunmamış sifarişlərə ayrılan vərəq < lazım olan → `409 printJobs.notEnoughPaper`. Lotok tutumu təyin olunmayıbsa (0) yoxlama edilmir.
- `dev: true` — **yalnız Development**: ödəniş keçilir, sifariş dərhal `Queued` olur (`paymentSkipped: true`). Başqa mühitdə `400 printJobs.paymentSkipNotAllowed`. Bayraqsız sifariş `PendingPayment`-də gözləyir (ödəniş Addım 4-də).
- `201` + sifariş: `id`, `jobCode` (6 rəqəm), `status`, `price {total, currency, basePrice, volumeDiscount, discountPercent, minimumApplied, sides, sheets, profileId, profileName, takenAt}`, `requiredSheets` (fiziki vərəq), `queuePosition`.

### `GET /api/print-jobs/{id}`, `GET /api/print-jobs/by-code/{code}`

Sifariş; kod ilə axtarış ləğv ekranı üçündür (Bölmə 7.3, ekran 11).

### `POST /api/print-jobs/{id}/cancel` (`Idempotency-Key`)

`Draft`, `PendingPayment`, `Queued`, `Interrupted` → `Cancelled`. Çap olunan sifariş ləğv olunmur (`409 printJobs.cancelWhilePrinting`). Ödənilmiş sifarişin geri qaytarılması Addım 4-dədir.

### `POST /api/print-jobs/{id}/status`

Kiosk çapın gedişini bildirir: `{ "status": "Printing" | "Completed" | "Interrupted", "printedSides": 8, "printedSheets": 4, "errorCode": "printer.outOfPaper" }`. Eyni hesabat təkrar gəlsə heç nə dəyişmir (idempotent). Printer sifarişdən çox vərəq bildirsə sayğac sifarişin həddinə qədər azalır. Tanınmayan `errorCode` → `printer.error`. Xəta kodları: `printer.offline`, `printer.outOfPaper`, `printer.paperJam`, `printer.notSelected`, `printer.notFound`, `printer.spoolFailed`, `printer.fileMissing`, `printer.restarted`, `printer.error`.

İstəyə bağlı `attempt` — hesabatın aid olduğu çap cəhdi, `print_attempts`-ə yazılır (plan 2.5; "çap olubmu?" sualının sübutu): `{ "number": 1, "printer", "spoolerJobId", "startedAt", "finishedAt", "counterBefore", "counterAfter", "counterUnit": "sheets" | "impressions", "recovered" }`. `spoolerJobId` — CUPS request id və ya Windows sənəd adı; `counterBefore`/`counterAfter` — printerin SNMP sayğacı (şəbəkə printeri deyilsə `null`); `recovered: true` — nəticə kiosk restart-dan sonra spooler və sayğacla müəyyən edilib. Bir iş + nömrə = bir sətir: təkrar hesabat sətri yeniləyir. Yanlış məlumat → `400 printJobs.attemptInvalid`. Cəhd job-un statusu dəyişməsə də (məs. job artıq ləğv olunub) yazılır.

### `GET /api/kiosks/me/queue`

Növbə sırası ilə (`Queued` + `Printing`): `[{ jobId, jobCode, position, status, pages, copies, userInitial?, fileName?, queuedAt }]`. Qonaq sifarişində `userInitial` yoxdur.

Növbə Redis sorted set-dədir (`queue:{kioskId}`, bal = növbəyə düşmə vaxtı); həqiqət mənbəyi MongoDB-dir. Redis cavab verməsə oxuma MongoDB-dən gedir, boş set MongoDB ilə yoxlanıb bərpa olunur, `print-queue-reconciliation` Hangfire işi hər dəqiqə bütün növbələri MongoDB-dən yenidən qurur.

### Printerlər və əmrlər (Bölmə 7.7, 7.8)

Kiosk (kiosk token):

- `PUT /api/kiosks/me/printers` — `{ printers: [{ name, driverName, isOnline, color, duplex, a3 }] }`: işə düşəndə, siyahı dəyişəndə və `RefreshPrinters` əmri gələndə. Siyahı dəyişibsə panelə `KioskPrintersUpdated`.
- `GET /api/kiosks/me/commands` — cavablanmamış əmrlər (kiosk offline olanda göndərilənlər). 24 saatdan köhnə əmr `Expired` olur və icra edilmir.
- `POST /api/kiosks/me/commands/{id}/result` — `{ succeeded, messageKey, sheetsUsed }`; təkrar cavab heç nə dəyişmir. Uğurlu `TestPrint`: `NotConfigured` kiosk → `Online`; istifadə olunan vərəq A4 sayğacından çıxılır.
- `GET /api/kiosks/me/config` indi `prices` (profil adı, ağ-qara/rəngli üz qiyməti, kağız əmsalları, minimum sifariş, pillələr — seçim ekranında canlı qiymət üçün; sifarişin əsl qiymətini server hesablayır), `allowedFormats` (boşdursa bütün kataloq), `paperBlockWhenInsufficient`, `idleResetSeconds` da qaytarır.

Admin:

| Endpoint | İcazə | |
|---|---|---|
| `GET /api/admin/kiosks/{id}/printers` | `printers.view` | Aşkarlanan printerlər, seçilən, aşkarlanan / override / effektiv imkanlar |
| `PUT /api/admin/kiosks/{id}/printer` | `printers.select` | `{ name }` — yalnız kioskun bildirdiyi printer; override-lar sıfırlanır; kioska `PrinterConfigChanged` |
| `PUT /api/admin/kiosks/{id}/capabilities` | `printers.capabilities.manage` | `{ color?, duplex?, a3? }` — yalnız söndürmək olar (`false`); printerin olmayan imkanını yandırmaq `400 printers.cannotEnable` |
| `POST /api/admin/kiosks/{id}/printers/refresh` | `printers.refresh` | `RefreshPrinters` əmri |
| `POST /api/admin/kiosks/{id}/test-print` | `printers.testprint` | `TestPrint` əmri; printer seçilməyibsə `409 printer.notSelected`; nəticə `/hubs/admin` `CommandResult` |
| `GET /api/admin/kiosks/{id}/commands` | `kiosks.view` | Son 20 əmr |
| `GET /api/admin/kiosks/{id}/supplies?limit=50` | `supplies.view` | "Sərf" tabı: `paper` (ölçü üzrə `capacity`, `current`, `lastRefillAt`) + `events[]` (`resource` `PaperA4`/`PaperA3`, `type` `JobPrinted`/`TestPrint`/`Refill`/`Recount`, `delta`, `before`, `after`, `jobId`, `by`, `at`), ən yenilər əvvəl |
| `POST /api/admin/kiosks/{id}/paper/refill` | `paper.refill` | `Idempotency-Key`. `{ "paper": "A4", "mode": "Add" \| "Full" \| "Exact", "sheets": 200 }` — əlavə edilən vərəq / tutuma qədər (sheets nəzərə alınmır) / sayılmış dəqiq say. Tutumdan çox → `409 kiosks.paperRefillInvalid`; lotok tutumu 0 → `409 kiosks.paperNoTray`. `supply_events` + audit (`paper.refill`); kioska `KioskConfigChanged`. Cavab: "Sərf" tabının özü |

Kağız sayğacı (plan 2.15): job `Completed` və ya `Interrupted` olanda onun `printedSheets`-i (sifarişin `requiredSheets`-i ilə məhdud) kioskun həmin kağız ölçüsü sayğacından çıxılır — status hesabatı ilə **eyni tranzaksiyada**, bir dəfə (təkrar hesabat heç nə dəyişmir). Test səhifəsi də A4-dən çıxılır (`TestPrint`). Sayğac 0-dan aşağı düşmür.

### Admin (`/api/admin`)

| Endpoint | İcazə | |
|---|---|---|
| `GET /api/admin/print-jobs?kioskId=&status=Queued,Printing&from=&to=&code=&page=&pageSize=` | `jobs.view` | Ən yenilər əvvəl; yalnız hesabın kiosk əhatəsi |
| `GET /api/admin/print-jobs/{id}` | `jobs.view` | Sifariş + `history[]` (`from`, `to`, `actorType`, `reason`, `at`) + `attempts[]` (`number`, `printer`, `spoolerJobId`, `startedAt`, `finishedAt`, `counterBefore`, `counterAfter`, `counterUnit`, `reportedSides`, `reportedSheets`, `status`, `error` — oxuyanın dilində, `recovered`) |
| `POST /api/admin/print-jobs/{id}/cancel` | `jobs.cancel` | `Idempotency-Key`; səbəb audit-ə |
| `GET /api/admin/kiosks/{kioskId}/queue` | `jobs.view` | Kioskun canlı növbəsi |

## Ödənişlər (`/api/payments`, Addım 4)

Kioskda ödənişin gedişi (plan Bölmə 9.1): kiosk job-u yaradır (`PendingPayment`)
→ intent açır → müştəri ödəyir → bank imzalı callback göndərir → job `Paid` →
`Queued`, kioska SignalR `PaymentConfirmed`. Məbləğ job-un qiymət
snapshot-undan götürülür, sonradan yenidən hesablanmır.

### `POST /api/payments/intents` (Kiosk token, `Idempotency-Key` məcburi)

`{ "jobId": "…", "method": "BankQr" | "MobileApp" }` → `200`:

```json
{ "id": "…", "jobId": "…", "jobCode": "565946", "method": "BankQr", "status": "Pending",
  "amount": 0.20, "currency": "AZN", "qrContent": "MOCKBANK:…", "expiresAt": "…", "secondsLeft": 300 }
```

- `secondsLeft` serverin saatı ilədir, kiosk geri sayımı bundan başlayır; müddət
  `Payment.TimeoutMinutes`-dir (paneldə dəyişəndə növbəti intent yeni dəyərlə açılır).
- Eyni job və metod üçün hələ ödənilə bilən intent varsa **eyni** intent qaytarılır —
  iki ödənilə bilən QR heç vaxt olmur. Metod dəyişəndə əvvəlki QR bankda bağlanır; müştəri
  onu artıq ödəyibsə həmin ödəniş qəbul olunur və qaytarılır (ikinci ödəniş açılmır).
- `MobileApp` → `qrContent` = `neoprint://pay/{id}`.
- Bank cavab vermirsə `503` (`code: payments.providerUnavailable`) — heç nə tutulmayıb, job ödəniş gözləməyə davam edir.
- Job ödəniş gözləmirsə `409` (`payments.jobNotAwaitingPayment`).
- Ödənilmiş, vaxtı keçmiş və ya rədd olunmuş ödənişdə `qrContent` qaytarılmır.

### `GET /api/payments/{id}` (Kiosk token)

Yuxarıdakı cavab; kiosk `PaymentConfirmed` gəlmədikdə bununla yoxlayır (polling).

### `POST /api/payments/callback/{provider}` (anonim, imzalı)

Provayderin webhook-u. İmza gövdənin **dəqiq baytları** üzərindədir (mock bank:
`X-MockBank-Signature` = HMAC-SHA256, açar `NEOPRINT_BANK_CALLBACK_SECRET`).
İmza yanlışdırsa `401`, tanınmayan provayder `404`. Eyni hadisə (`provider_events`
unikal indeksi) ikinci dəfə gələndə `200 { "outcome": "Duplicate" }` — heç nə dəyişmir.
Bankın bildirdiyi məbləğ ödənişlə üst-üstə düşmürsə tətbiq edilmir (Critical log).

### Fon işləri

| İş | Tezlik | Nə edir |
|---|---|---|
| `PaymentExpirationJob` | 1 dəq | Vaxtı keçmiş intent-i bankda bağlayır (bank "ödənilib" deyirsə ödəniş qəbul olunur), sonra `Expired`; job-un başqa açıq intent-i yoxdursa job da `Expired`. Intent açılmadan qalmış job-lar da `Payment.TimeoutMinutes` sonra `Expired` |
| `PaymentReconciliationJob` | 5 dəq | `Payment.ReconcileAfterMinutes`-dən köhnə, cavabı gəlməmiş (`Pending` / `Expired`) ödənişlərin statusunu bankdan soruşur — itmiş və ya gec callback |

Bank pulu job artıq ödənilə bilməyəndə (vaxtı keçib, ləğv olunub, başqa ödənişlə
ödənilib) təsdiqləsə ödəniş `Succeeded` yazılır və eyni tranzaksiyada qaytarma
tələb olunur (`RefundRequested`, səbəb `LatePayment` / `DuplicatePayment`).

### Development (`/api/dev`, başqa mühitlərdə `404`)

| Endpoint | Nə edir |
|---|---|
| `POST /api/dev/payments/{id}/simulate-success?delaySeconds=&duplicate=&dropCallback=` | Müştəri bank tətbiqində ödəyir; callback gecikir / iki dəfə eyni anda gəlir / itir |
| `POST /api/dev/payments/{id}/simulate-fail` | Bank rədd edir (`payment.failed` callback) |
| `POST /api/dev/mock-bank/create-fails?fails=true\|false` | Bank "düşür": QR yaratmaq timeout verir |

Vaxtı keçmiş (bankda bağlanmış) QR-ı ödəmək `409 { "outcome": "QrNoLongerValid" }` qaytarır — bank tətbiqi də onu rədd edir.

## Hələ mövcud olmayan endpoint-lər

Planın Bölmə 6-dakı siyahısından bunlar sonrakı addımlardadır: ödəniş / refund / wallet / fiskal (Addım 4), skan və paylaşım
(Addım 6), telemetriya və operator paneli (Addım 7).

Addım 2-nin bütün endpoint-ləri (fayllar, redaktə və versiyalar, səhifə
şəkilləri, formatlar, storage provayderləri və sinxronizasiya, kiosk idarəsi,
qiymət profilləri və `pricing/quote`) yuxarıdakı bölmələrdədir.
