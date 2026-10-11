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

### `GET /api/consents/pending?lang=az&scan=false`

User token. Qəbul edilməmiş sənəd versiyaları (Privacy, Terms). `scan=true` —
`ScanNotice` də (ilk skandan əvvəl soruşulur, girişdə yox; Addım 6).

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
| `DELETE` | `/api/admin/roles/{id}` | `204` / `404` — səbəb `X-Audit-Reason` başlığında, `roles.delete` audit-inə düşür |

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

## Kiosk axtarışı (mobil tətbiq, Addım 5)

Yalnız mobil istifadəçi token-i (`401` anonim, `403` kiosk / admin token-i).
Müştəri yalnız gedə biləcəyi kioskları görür: `Online`, `Paused`, `Offline`,
`Maintenance`. `NotActivated`, `NotConfigured`, `Disabled` kiosk axtarışda
yoxdur, detalı `404 notFound.kiosk`.

`status` — müştərinin gözləyəcəyi: saxlanan status, amma son heartbeat-i
`Kiosk.OfflineAfterSeconds`-dan (60) köhnə olan kiosk `Offline` göstərilir
(statusu saxlayan monitor Addım 7-dədir, T-083).

### `GET /api/kiosks?search=&lat=&lng=&radiusKm=`

→ `{ "items": [ { "id", "code", "name", "address", "lat", "lng", "status", "distanceKm" } ] }`, ən çox 100.

- `search` — ad, kod və ya ünvanın bir hissəsi, böyük-kiçik hərf fərqi yoxdur (ən çox 100 simvol; mətn kimi axtarılır, pattern deyil). Kioskun `id`-si verilərsə dəqiq uyğunluq.
- `lat` + `lng` — ən yaxın birinci, `distanceKm` ilə (`2dsphere`, `Location.Point`). Məkansız sorğuda `distanceKm` yoxdur, sıra koda görədir.
- `radiusKm` — 0,1–100, yalnız `lat` / `lng` ilə.
- Xətalar (`400`): `kiosks.searchTooLong`, `kiosks.searchPointIncomplete` (yalnız biri verilib), `kiosks.latitudeRange`, `kiosks.longitudeRange`, `kiosks.searchRadiusWithoutPoint`, `kiosks.searchRadiusRange`.

### `GET /api/kiosks/{idOrCode}`

`idOrCode` — `id` və ya kod (`ksk-9002` də olar).

```json
{
  "id", "code", "name", "address", "lat", "lng", "status",
  "capabilities": { "color", "duplex", "a3" },
  "paper": [ { "size": "A4", "availableSheets": 480, "state": "Ok" } ],
  "queueLength": 1,
  "prices": { "profileName", "bwPerSide", "colorPerSide", "paperMultipliers", "minOrder", "tiers", "currency" }
}
```

`paper` — kioskun lotoku olan hər ölçü (A3 yalnız printer A3 bacaranda).
`availableSheets` — sayğac minus hələ çap olunmamış sifarişlərə ayrılmış vərəqlər;
lotok tutumu təyin olunmayıbsa sahə yoxdur və `state` = `Unknown`. `state`:
`Ok`, `Low` (200 vərəqdən az — plan 2.15-in A4 Warning seed həddi; admin qaydaları
Addım 7-də), `Out`. `prices` — kioskun qiymət profili (yoxdursa default);
sifariş zamanı qiymət serverdə yenidən hesablanır.

### `GET /api/kiosks/{id}/queue`

→ `{ "kioskId", "length" }` — növbədə və çapda olan sifarişlərin **sayı**; kimin olduğu heç vaxt verilmir.

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
Yaratma və ləğv `audit_logs`-a düşür (`devices.enrollmentToken.create` — hədəf kiosk və bitmə vaxtı; `devices.enrollmentToken.revoke`), tokenin özü heç vaxt yazılmır (T-165).
`windowsCommand` / `linuxCommand` provisioning skripti üçün hazır əmrdir.

`GET` token-in özünü qaytarmır:
`{ "id", "kioskId", "kioskCode", "newKioskName", "createdAt", "expiresAt",
"usedByDeviceId", "revokedAt", "isUsable" }`

Token **bir dəfəlikdir**.

---

## Feature flag-lar

### `GET /api/features`

Anonim və ya hər hansı token. Çağıran üçün qiymətləndirilmiş flag-lar.

→ `{ "scheduled_print": true, "data_export": false, ... }` (açarlar `FeatureFlag.MvpFlags`; mobil
tətbiq göndərilməyən açarı "açıq" sayır, backend də elə)

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
yenilənmiş flag. Saxlanan kimi Redis keşi (`features:all`) təmizlənir — dəyişiklik dərhal tətbiq olunur.

### Söndürülmüş flag: yalnız yeni istifadə bağlanır (T-148)

Rədd cavabı həmişə eynidir: `404`, `code = features.unavailable`, `feature = <açar>`
(`[RequireFeature]` atributu və ya use-case daxilində `FeatureDisabledException`). Kiosk bəlli olan
yoxlamalar use-case-dədir ki, kiosk override (pilot) tətbiq olunsun.

| Flag | Bağlanan (yeni) | Açıq qalan (artıq yaradılmış / ödənilmiş) |
|---|---|---|
| `scheduled_print` | `POST schedules` | siyahı, vaxtı dəyiş, "Çapa ver", ləğv, ödənilməmişin ödənişi |
| `user_qr_print` | `POST prepared-prints`, `POST prepared-prints/paid`, kioskda `POST print-jobs/pending-for-user/prepared/{id}`; QR oxunanda ödənilməmiş hazırlıqlar siyahıya düşmür | `GET users/me/qr(.png)`, kioskda QR oxutma + ödənilmiş sifarişin `release`-i, siyahı, ləğv |
| `mobile_session_print` | `POST sessions` (yeni sessiya) | açıq sessiyanın uzadılması və ondakı sifariş |
| `mobile_payment_at_kiosk` | kioskda `POST payments/intents` (`MobileApp`), kiosk kanalı ödənişinin `GET payments/{id}/app` və `pay-with-app`-i (ödənilməmiş, rezervasiyasız) | başlamış / ödənilmiş ödəniş, bank QR, tətbiqdə ödənilən sifarişlər (`Mobile` kanalı) |
| `promo_codes` | promokodun tətbiqi (`promos.disabled`) | — |
| `data_export` | `POST users/me/export` | `GET users/me/export`, hazır arxivin linki |

---

## SignalR hub-ları

| Hub | Kim | Nə üçün |
|---|---|---|
| `/hubs/provisioning` | Provisioning token | Aktivləşdirmə anında cihaza xəbər verilir — cihaz kod ekranından özü keçir |
| `/hubs/admin` | Admin token | `PermissionsChanged`, `DeviceRevoked`, `QueueUpdated` (yalnız hesabın əhatəsindəki kiosklar üçün) |
| `/hubs/kiosk` | Kiosk token | Qrup `kiosk:{kioskId}` (token-dəki kiosk). `QueueUpdated` (`{kioskId, queue[]}`), `JobAdded`, `JobCancelled` (`{kioskId, jobId}`), `PrinterConfigChanged`, `KioskConfigChanged`, `KioskCommand`, `PaymentConfirmed` (`{kioskId, paymentId, jobId}`), `SessionOpened` (`{kioskId, sessionId, expiresAt}` — telefon qoşuldu; kim olduğu yoxdur, ekran ictimaidir), `SessionClosed` (`{kioskId, sessionId, reason}`: `Closed` / `Expired` / `Replaced`) |
| `/hubs/user` | User token | Qrup `user:{userId}` (token-dəki istifadəçi; başqasının hadisəsi heç vaxt gəlmir). `JobStatusChanged` (`{jobId, jobCode, status}` — `Suspected` daxil), `PaymentStatusChanged` (`{paymentId, jobId, status}`), `RefundStatusChanged` (`{refundId, paymentId, jobId, status, amount, destination}`), `SessionClosed` (`{sessionId, kioskId, reason}`). Mesaj yalnız xəbərdir: cari statusu daşıyır, təfərrüat API-dən oxunur; itən və ya təkrar gələn mesaj heç nəyi pozmur |

Redis backplane ilə işləyir, ona görə bir necə API instansiyası eyni qrupları
xidmət edə bilir.

---

## Sessiyalar (`/api/sessions`, mobil tətbiq, Addım 5)

Yalnız mobil istifadəçi token-i. Sessiya — tətbiqin bir kioskla müvəqqəti əlaqəsi
(Bölmə 9.3): Redis-də `session:{id}` → `{UserId, KioskId, ExpiresAt}`, ömrü
`KioskSession.TtlMinutes` (10) fəaliyyətsizlik; sessiyada verilən hər sifariş müddəti
yenidən başladır. İstifadəçinin eyni anda bir sessiyası olur. Redis itsə açıq
sessiyalar itir, istifadəçi yenisini açır; verilmiş sifarişlər MongoDB-dədir.

### `POST /api/sessions`

`{ "kiosk": "KSK-9002" }` (kod — hərf fərqi yoxdur — və ya `id`) → `{ id, kioskId, kioskCode, kioskName, expiresAt, ttlMinutes }`.
Eyni kioskda təkrar açmaq **eyni sessiyanı** uzadır (ikiqat toxunuş iki sessiya açmır);
başqa kioskda açmaq əvvəlkini bağlayır (`SessionClosed`, `Replaced`). Kiosk `SessionOpened`
alır. Kiosk `Online` deyilsə → `409 sessions.kioskNotTakingOrders`; aktivləşdirilməmiş,
quraşdırılmamış, söndürülmüş və ya olmayan kiosk → `404 notFound.kiosk`; boş → `400 sessions.kioskRequired`.

### `GET /api/sessions/current` · `GET /api/sessions/{id}`

İstifadəçinin açıq sessiyası (`current` — yoxdursa `204`). Bitmiş, bağlanmış və ya başqasının sessiyası → `404 notFound.session`.

### `DELETE /api/sessions/{id}`

`204`. Kiosk `SessionClosed` (`Closed`) alır. Artıq bitmiş sessiya üçün də `204` (idempotent), heç kimə ikinci dəfə xəbər verilmir.

Vaxtı bitən sessiyalar dəqiqədə bir (`kiosk-session-expiry` Hangfire işi) kioska və tətbiqə
`SessionClosed` (`Expired`) kimi bildirilir — hər biri bir dəfə.

### `POST /api/sessions/{id}/print-jobs` (`Idempotency-Key` məcburi)

Plan 9.3 addım 2 (`Source=MobileSession`): hesabdakı hazır faylın sessiya kioskunda sifarişi.
`{ "fileId", "options": { copies, pageRange, color, duplex, paper, orientation, pagesPerSheet, scaling } }` →
`{ jobId, jobCode, paymentId, amount, currency, expiresAt, pages, sheets }`.

- Kioskun `POST /api/print-jobs`-i ilə **eyni qaydalar**: səhifə limiti, printerin imkanları, kağız (`Paper.BlockWhenInsufficient`), qiymət kioskun profilindən snapshot (`PrintOrderRules`).
- Sifariş `PendingPayment`; cavabdakı `paymentId` tətbiqin öz ödənişidir (`MobileApp`, kanal `Mobile`, istifadəçiyə bağlı) — `GET /api/payments/{id}/app` və `POST /api/payments/{id}/pay-with-app` ilə ödənilir (balans / kart / bonus / promokod, kiosk QR-ı ilə eyni). `Payment.TimeoutMinutes` keçərsə sifariş vaxtı bitir.
- Sifariş faylın **o anki PDF-ini** saxlayır (`PrintFile`); sifariş bitməyənə qədər (ödəniş gözləyir, ödənilib, növbədə, çapda, dayanıb, baxışda) faylı redaktə etmək və silmək → `409 files.inUseByOrder`.
- Xətalar: sessiya yoxdur / bitib / başqasınındır → `404 notFound.session`; fayl başqasınındır və ya yoxdur → `404 notFound.file`; fayl hazır deyil → `409 files.notReadyNamed`; kiosk sifariş qəbul etmir → `409 sessions.kioskNotTakingOrders`.
- Plan `POST print-jobs` yazır; həmin marşrut kioskundur (yalnız kiosk token-i), ona görə tətbiqin sifarişi sessiyanın altındadır (`DECISIONS.md`, 2026-10-03).

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
| `source` | mətn | Yalnız `Uploaded` (default). `Scan` / `Layout` / `Shared` → `400 files.sourceNotAllowed`: onların öz endpoint-i və yoxlamaları var (Addım 6) |
| `isSensitive` | bool | Şəxsi sənəd — avtomatik silinmə defolt olaraq `AfterPrint` olur |

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
`pageCount`, `isSensitive`, `autoDelete`, `versionCount`, `createdAt`,
`editLocked` (saxlanılmış skan — yalnız çap, Addım 6), `hasLayout` ("Tərtibatı
redaktə et" açılır), `documentType`, `physicalWidthMm`, `physicalHeightMm` (skan).

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
- Saxlanılmış skan (`editLocked`) → `400 files.edit.scanLocked`; tərtibat faylı →
  `400 files.edit.useLayoutEditor` (`PUT files/{id}/layout` ilə dəyişdirilir).

Ən çox `Files.VersionsToKeep` (default 5) versiya saxlanılır; köhnələri storage-dan
silinir. Əvəz olunan çevrilmiş PDF də silinir.


Bu faylla verilmiş sifariş hələ bitməyibsə → `409 files.inUseByOrder` (sifariş qiymətləndirildiyi PDF-i çap edir).

### `GET /api/files/{id}/versions`

→ `[ { "number", "extension", "sizeBytes", "createdAt", "note", "isCurrent" } ]`, yenisi əvvəldə.

### `GET /api/files/{id}/versions/{n}/content`

Bir versiyanın məzmunu, deşifrə olunmuş.

### `PUT /api/files/{id}/auto-delete`

`{ "rule": "Never" | "AfterPrint" | "AfterOneDay" | "AfterSevenDays" | "AfterThirtyDays" }`
→ fayl obyekti. Silinmə `FileAutoDeleteJob`-dadır (T-147): `AfterPrint` — bu faylla sifariş kioskda çap olunan kimi,
amma faylın açıq sifarişi və ya aktiv planlanmış çapı varsa sonuncudan sonra; `After…Days` — yükləmədən N gün sonra
(hər 5 dəqiqə). Bütün obyektlər bütün provayderlərdən silinir, istifadəçiyə `FileAutoDeleted` bildirişi gedir.

### `DELETE /api/files/{id}`

→ `204`. Obyektlər **bütün** provayderlərdən silinir (mirror və migrasiya
nüsxələri daxil).


Bu faylla verilmiş sifariş hələ bitməyibsə → `409 files.inUseByOrder` (sifariş qiymətləndirildiyi PDF-i çap edir).

### Skan və səhifə tərtibatı (Addım 6, plan 2.11)

Tərtibat JSON-u (`NeoPrint.Layout`, backend və kiosk eyni renderer):

```json
{ "version": 1, "paper": "A4", "orientation": "Portrait", "marginMm": 10,
  "pages": [ { "elements": [
    { "image": "front.jpg", "xMm": 62.2, "yMm": 89.52, "widthMm": 85.6, "heightMm": 53.98, "rotationDeg": 0 } ] } ] }
```

- `paper`: `A4` / `A3` / `A5`; `orientation`: `Portrait` / `Landscape`; ölçülər mm,
  kağızın sol yuxarı küncündən; `rotationDeg` qutunun mərkəzi ətrafında saat əqrəbi ilə.
  Şəkil qutusunun ölçüsü onun **çap ölçüsüdür** (real ölçü, 100%).
- Limitlər: 1–50 səhifə, səhifədə 1–40 şəkil, ən çox 50 müxtəlif şəkil, kənar
  0–50 mm, şəkil tərəfi 1–1000 mm, şəklin mərkəzi kağızın üzərində olmalıdır,
  JSON ≤ 512 KB. Pozulma → `400 layout.*` (izahlı mesajla).
- Şəkillər 300 DPI-dan yüksək saxlanılmır (yerləşdirildiyi ən böyük ölçüyə görə
  kiçildilir), EXIF oriyentasiyasına görə düzəldilir; 100 MP-dən böyük şəkil `400`.

#### `POST /api/files/scan`

Skan — bitmiş PDF kimi saxlanılır, **sonra redaktə olunmur** (`editLocked`),
şəkillər saxlanılmır (DECISIONS 2026-10-05). `multipart/form-data`:

| Sahə | Məna |
|---|---|
| `images` | Səhifə şəkilləri (JPEG/PNG…, imza ilə yoxlanılır), hər hissənin fayl adı `layout`-dakı `image` adıdır. Ən çox 50 |
| `layout` | Tərtibat JSON-u (skan sessiyasının son addımındakı yerləşdirmə) |
| `documentType` | `IdCard`, `Id2`, `Passport`, `Photo3x4`, `Photo10x15`, `A6`, `A5`, `A4` və ya `Custom` |
| `physicalWidthMm`, `physicalHeightMm` | Yalnız `Custom` üçün, 1–1000 |
| `isSensitive` | `IdCard` / `Id2` / `Passport` üçün göndərilməsə də `true` olur |
| `fileName` | Göstərilən ad — başlıqdır, fayl yolu deyil: `/` və `\` " - " ilə əvəz olunur, sondakı `.pdf` təkrarlanmır, başqa nöqtələr qalır ("Dr. Əliyev arayış" → `Dr. Əliyev arayış.pdf`); boşdursa `scan-<tarix>.pdf`. `POST files/layout`-da da belədir (T-197) |

→ `201` + fayl obyekti (`source` = `Scan`, `extension` = `pdf`). Şərt: istifadəçi
`ScanNotice`-in **cari versiyasını** qəbul edib (`GET /api/consents/pending?scan=true`
→ `POST /api/consents`), əks halda `400 scan.noticeRequired`. Saxlanılmış skan başqa
tərtibata mənbə ola bilməz (`400 files.layout.scanNotSource`).

#### `POST /api/files/layout`

`{ "layout": { ... }, "fileName": "Fotolar" }` → `201` + fayl obyekti (`source` =
`Layout`, `hasLayout` = `true`). Şəkil adları:

- `file:{id}` — şəkil faylının cari məzmunu (şəkil deyilsə 1-ci səhifə);
- `file:{id}:{n}` — istənilən faylın PDF-inin `n`-ci səhifəsi, şəkil kimi (A4 eni 300 DPI).

Tərtibat hər şəklin **öz şifrəli nüsxəsini** saxlayır (`asset:{key}` adı ilə):
mənbə fayl silinsə də yenidən açılır. Mənbələrdən biri həssasdırsa tərtibat da
həssasdır (avtomatik silinmə default `AfterPrint`). Başqasının faylı `404`.

#### `GET /api/files/{id}/layout`

→ `{ "fileId", "layout": { ...şəkillər "asset:{key}" adı ilə... }, "assets": [ { "name", "mediaType", "sizeBytes" } ] }`.
Tərtibat faylı deyilsə `400 files.layout.notLayout`.

#### `GET /api/files/{id}/layout/assets/{key}`

Tərtibatın şəkli, deşifrə olunmuş (`Cache-Control: private`), redaktorda çəkmək üçün.

#### `PUT /api/files/{id}/layout`

`{ "layout": { ... } }` — dəyişmiş tərtibat **yeni versiya** kimi saxlanılır
(`files.note.layoutEdited`), fayl `Pending` olur və yenidən çevrilir. Mövcud
şəkillər `asset:{key}`, yeniləri `file:…` adı ilə. İstifadə olunmayan şəkil
nüsxələri silinir. Açıq sifariş varsa `409 files.inUseByOrder`; həssaslıq heç
vaxt azalmır.

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

### `GET /api/print-jobs/my` (User token)

Tətbiqdə "Sifarişlərim": hesabın son 100 sifarişi, ən yenisi əvvəl —
`[{ id, jobCode, status, kioskId, kioskCode, kioskName, fileName, pages, copies, color, paper, amount, currency, printedSides, reason, paymentId, createdAt, finishedAt }]`.
Tətbiqlə ödənilən kiosk sifarişi ödəniş anında hesaba bağlanır və burada görünür; bank QR ilə ödənilən anonim sifariş görünmür. `amount` — kioskda göstərilən qiymət (promokod daxil); faktiki tutulan məbləğ qəbzdədir. `reason` — dayanma və ya ləğv səbəbi oxuyanın dilində.

### `GET /api/print-jobs/{id}/file` (Kiosk token)

Tətbiqdən verilmiş sifarişin (`MobileSession`) çap PDF-i, deşifrə olunmuş (plan 7.4): kiosk onu şifrəli
spool-a yazıb oradan çap edir. Yalnız sifarişin kioskuna və yalnız ödənildikdən sonra, bitməmiş halda
(`Paid`, `Queued`, `Printing`); əks halda `409 printJobs.fileNotPrintable`. Başqa kioskun sifarişi,
fləş sifarişi və ya faylsız sifariş → `404`. Bayt-bayt sifarişin qiymətləndirildiyi PDF-dir.

### `GET /api/print-jobs/{id}/refunds` (Kiosk token)

Kiosk ekranındakı refund kartı üçün: `[{ id, amount, currency, status, destination ("Card" | "Wallet" | "Voucher"), statusUrl, voucherCode? }]`. `statusUrl` — ictimai status səhifəsinin ünvanı (`{Public.SiteUrl}/refund/{refundId}`), kiosk onu QR kimi göstərir. Yalnız həmin kioskun sifarişi; başqa kioskun sifarişi → `404`.

### `POST /api/print-jobs/{id}/flash-upload` (Kiosk token, `multipart/form-data`, sahə `file`)

Fləşkadan gələn və **NeoPrint tətbiqi ilə ödənilən** sifarişin faylı müştərinin hesabına keçir (Bölmə 9.2, addım 5): `{ jobId, fileId, alreadyKept }`. Sifariş fləşkadan deyilsə və ya arxasında hesab yoxdursa → `409 printJobs.flashUploadNotForThis`; başqa kioskun sifarişi → `404`. Sifarişin artıq faylı varsa təkrar göndəriş onu qaytarır (`alreadyKept: true`, idempotent). Maksimum 200 MB. Kiosk faylı şifrəli spool-da saxlayır və şəbəkə yoxdursa sonra yenidən göndərir.

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
| `POST /api/admin/print-jobs/{id}/cancel` | `jobs.cancel` | `Idempotency-Key`; `{ "reason" }` **məcburidir** (boş → `400 printJobs.cancelReasonRequired`, sifariş toxunulmur); `audit_logs`-a `printJobs.cancel` (əvvəlki → sonrakı status, səbəb, IP) — artıq ləğv edilmiş sifarişə təkrar sorğu ikinci audit yazmır (T-165) |
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

### Tətbiqlə ödəniş (`User` token)

| Endpoint | Nə edir |
|---|---|
| `GET /api/payments/{id}/app` | Kioskdakı `neoprint://pay/{id}` QR-ı skan edəndən sonra təsdiq ekranı: məbləğ, kiosk, sifariş (səhifə, nüsxə, rəng, kağız), balans, bonus səhifələr və bu sifarişdə istifadə oluna bilən bonus (`bonusPagesUsable`, `bonusValue`). Bank QR-ı və başqasının götürdüyü ödəniş `404` |
| `POST /api/payments/{id}/pay-with-app` (`Idempotency-Key`) | `{ "source": "Wallet" \| "Card", "useBonus": true, "expectedAmount"?: 0.15, "promoCode"?: "YAZ10" }` — `promoCode` yalnız kioskda kod verilməyibsə nəzərə alınır (sifarişdə bir kod), uyğun deyilsə `400` (`errors.promoCode`) və heç nə tutulmur; əvvəl bonus səhifələr (1 səhifə = sifarişin bir üzünün qiyməti, ən çox `Referral.MaxBonusPagesPerJob`), qalanı balansdan və ya kartdan. Job, qəbz və flaş faylı istifadəçinin hesabına bağlanır |

Qaydalar: balans və bonus hissəsi kart çıxılmazdan **əvvəl** ledger-də ayrılır
(`payment:{id}:{cəhd}`); kart rədd etsə geri qaytarılır (`release:{id}:{cəhd}`) və
ödəniş açıq qalır — istifadəçi balansla yenidən cəhd edə bilər. Kartın cavabı itibsə
(`503`, `payments.outcomeUnknown` — "yenidən ödəməyin") ayrılan məbləğ uzlaşdırma
kartın öz statusunu öyrənənə qədər saxlanılır. Balans çatmırsa `409 wallet.insufficient`,
heç nə ayrılmır. Eyni ödəniş paralel iki dəfə göndərilsə pul bir dəfə çıxılır.

## Qaytarmalar (refund)

Qaytarma qərarı (`RefundRequested`) ödənişin **hər hissəsi üçün ayrıca** refund yaradır
(kart/bank → `OriginalMethod` və ya `Wallet`, balans → yalnız `Wallet`, bonus → yalnız
`Bonus`). Üç qoruyucu ikiqat qaytarmanı qeyri-mümkün edir: refund-un unikal `DedupKey`-i
(eyni qərar bir refund), hər hissə üzrə cəm (bir ödənişin refund-ları heç vaxt onu keçmir,
`Payment.ApplyRefund` də yoxlayır) və provayderə göndərilən açar (refund-un id-si — təkrar
sorğu ilk nəticəni qaytarır).

| Nə | Necə işləyir |
|---|---|
| A ssenarisi (printer xətası, kiosk əlaqədədir) | Kiosk `Interrupted` bildirəndə, ödəniş anonim **bank QR**-dırsa: çap olunmayan hissə sübutla hesablanır, job `Cancelled`, məbləğ avtomatik karta qaytarılır. Qeydiyyatlı istifadəçi üçün avtomatik refund yoxdur (təklif) |
| Sübut ardıcıllığı | Printer sayğacı (`print_attempts` əvvəl/sonra) → spooler ("tamamlandı") → kiosk jurnalı (`PrintedSides`) → heç biri yoxdursa **tam məbləğ**. Mənbələr ziddiyyətlidirsə **ən az çap olunmuş** saya inanılır (müştərinin xeyrinə). Seçilən mənbə və bütün dəyərlər refund-da (`evidence`) saxlanır |
| Job ləğvi | Kiosk (kodla), admin və ya sistem ləğv edəndə açıq intent-lər bankda bağlanır; ödənilmiş məbləğ qaytarılır (anonim — karta, qeydiyyatlı — balansa) |
| Provayder uğursuzluğu | `Refund.ProviderMaxAttempts` cəhd, aralarında 1, 2, 4… dəqiqə; sonra refund `Failed`, ödəniş `RefundFailed`, incident (`RefundFailed`, High), admin qərar verir |
| Voucher | Yalnız anonim müştərinin bank refund-u alınmayanda admin seçir: `RF-XXXX-XXXX`, `Refund.VoucherValidDays` gün; `POST /api/wallet/vouchers/redeem` ilə bir dəfə balansa köçür |

### İctimai status

`GET /api/refunds/{id}/public` (anonim) — kiosk ekranındakı / qəbzdəki QR bu səhifəni açır:
`{ status, amount, currency, destination ("Card" | "Wallet" | "Voucher"), createdAt, completedAt, voucherCode? }`. Şəxsi məlumat yoxdur.

`GET /refund/{id}?lang=az|en|ru` (anonim, API tərəfindən verilən HTML səhifə) — QR-in açdığı səhifənin özü: məbləğ, hara qaytarıldığı, status və izah, dil keçidləri və "Problem barədə bildir" forması. Forma `POST /api/refunds/{id}/claim` göndərir (`Idempotency-Key`, `{ text, contact? }`, rate limit `RateLimit.SupportClaimPerHour`) — refund-a bağlı müştəri müraciəti açılır. Səhifənin ünvanının əsası `Public.SiteUrl` tənzimləməsidir (default `https://neoprint.az`).

### Admin

| Endpoint | İcazə | Nə edir |
|---|---|---|
| `GET /api/admin/payments/{id}/refund-options` | `payments.refund` | Qaytarıla bilən: cəm, kart/bank, balans, bonus; kart hissəsinin mümkün istiqamətləri; job ləğv oluna bilərmi |
| `POST /api/admin/payments/{id}/refund` (`Idempotency-Key`) | `payments.refund` | `{ amount?, destination?, reason, cancelJob }` — səbəb məcburi (`400`), qalan məbləğdən çox olmaz (`409`); refund-lar eyni tranzaksiyada yaranır və dərhal icra olunur; audit `payments.refund` |
| `GET /api/admin/refunds?status=&reason=&paymentId=` · `GET /api/admin/refunds/{id}` | `refunds.view` | Siyahı və detal (cəhdlər, növbəti cəhd, xəta, sübut, voucher) |
| `POST /api/admin/refunds/{id}/retry` (`Idempotency-Key`) | `refunds.retry` | `{ destination?, note? }` — yalnız `Failed` refund; yeni cəhd limiti; anonim → kart və ya voucher, qeydiyyatlı → kart və ya balans, balans hissəsi → yalnız balans; audit |

### Development

| Endpoint | Nə edir |
|---|---|
| `POST /api/dev/refunds/{id}/simulate-fail` | Bu refund hər cəhddə uğursuz olur |
| `POST /api/dev/mock-bank/refunds-fail?fails=true\|false` | Bank bütün refund-ları rədd edir |

## Fiskal çeklər (e-kassa)

Fiskallaşdırma **asinxrondur**: ödəniş və refund e-kassanı gözləmir. `PaymentSucceeded` →
satış çeki (`Sale`) və ya balans artırılmasında avans çeki (`TopUp`, yalnız
`Fiscal.TopUpReceiptMode = OnTopUp`); `RefundCompleted` → qaytarma çeki (`Refund`, satış
çekinin fiskal ID-sinə istinadla — satış çeki hələ verilməyibsə gözləyir). Hər biri bir dəfə:
unikal `DedupKey` (`sale:{paymentId}`, `topup:{paymentId}`, `refund:{refundId}`), provayderə
çekin öz id-si ilə müraciət olunur (təkrar sorğu eyni sənədi qaytarır).

| Qayda | |
|---|---|
| Məbləğ | Pulla ödənilən hissə (kart / bank + balans). **Bonus səhifələr 0 AZN, çekə düşmür**; yalnız bonusla ödənişə çek yoxdur |
| Ödəniş növü (`paidWith`) | `BankQr` / `Card`; balans hissəsi `Advance` (`OnTopUp` rejimində) və ya `Wallet` (`OnService`) |
| Terminal | Kiosk satışı → kioskun `FiscalTerminalId`-si; tətbiq satışı və top-up → `Fiscal.OnlineTerminalId`. Kioskun terminalı yoxdursa çek **gözləyir** (`fiscal.noTerminal`), başqa terminal götürülmür. Qaytarma satışın terminalından keçir |
| Uğursuzluq | E-kassa cavab vermir → 1, 2, 4… (ən çox 30) dəqiqədən sonra təkrar; rədd edir → `Failed` + incident `FiscalFailed` (High). `Fiscal.MaxPendingMinutes` keçən gözləyən çek → incident (High, bir dəfə). Çek veriləndə incident-lər avtomatik bağlanır |
| `Fiscal.Enabled = false` | Çeklər yaranır, amma göndərilmir (`fiscal.disabled`); yandırılan kimi verilir, heç nə itmir; bu müddətdə gecikmə incident-i açılmır |

### `GET /api/payments/{id}/fiscal` (anonim)

Kiosk qəbz ekranı və tətbiq üçün: `{ receiptId, status (Pending/Issued/Failed), fiscalDocumentNumber?, fiscalId?, verificationUrl?, issuedAt? }`.
Çek hələ yoxdursa `404 notFound.fiscalReceipt`. Şəxsi məlumat yoxdur.

### Admin

| Endpoint | İcazə | |
|---|---|---|
| `GET /api/admin/fiscal-receipts?status=&type=&paymentId=&page=&pageSize=` | `fiscal.view` | Ən yenilər əvvəl; sətirlər, ödəniş növləri, terminal, cəhdlər, növbəti cəhd, son xəta (oxuyanın dilində) |
| `GET /api/admin/fiscal-receipts/{id}` | `fiscal.view` | Detal |
| `POST /api/admin/fiscal-receipts/{id}/retry` (`Idempotency-Key`) | `fiscal.manage` | `Failed` və ya gözləyən çek dərhal, yeni cəhd sayı ilə; verilmiş çek `409 fiscal.alreadyIssued`; audit `fiscal.receipts.retry` |
| `GET` · `PUT /api/admin/fiscal/settings` | `fiscal.manage` | Yalnız `Fiscal.*` tənzimləmələri (`{ values: { "Fiscal.OnlineTerminalId": "..." } }`); başqa açar `400 fiscal.settingNotFiscal`; tip yoxlaması və audit ümumi tənzimləmələrdəki kimi |
| `GET /api/admin/incidents?status=&type=&page=&pageSize=` | `incidents.view` | Incident siyahısı (T-129): kritik əvvəl, sonra ən yeni. `status` = `Open`/`Acknowledged`/`Resolved`, `type` = `FiscalFailed`, `RefundFailed`, `JobSuspected`, … (səhv dəyər `400 incidents.filterInvalid`). Sətir: `id, type, severity, status, details` (oxuyanın dilində), `entityType` + `entityId` (məs. `FiscalReceipt` + çekin id-si), `kioskId, createdAt, resolvedAt, resolution`. "Fiskal çeklər" səhifəsi açıq fiskal incident-ləri buradan götürür. Acknowledge / resolve və ayrıca "Incident-lər" səhifəsi Addım 7-dədir |

### Development

| Endpoint | Nə edir |
|---|---|
| `POST /api/dev/mock-fiscal/mode?mode=Ok\|Down\|Reject` | Mock e-kassa: işləyir / cavab vermir (timeout) / hər çeki rədd edir |
| `POST /api/dev/fiscal/{receiptId}/simulate-fail?fails=true\|false` | Bu çek hər cəhddə timeout olur |

Provayder `NEOPRINT_FISCAL_PROVIDER` ilə seçilir (default `mock`); başqa ad hələ yoxdur və API-nin başlamasını dayandırır.

## Promokodlar

Bir sifarişdə bir kod. Hesablama ardıcıllığı: profil qiyməti → həcm endirimi → **bonus
səhifələr** → promokod (qalan məbləğə). Faiz və ya sabit məbləğ, qalan məbləğdən çox
olmur. Endirim **ödəniş anında** hesablanır, çünki bonus yalnız tətbiqdə ödəyəndə seçilir;
sifarişin qiymət snapshot-u dəyişmir (ödənişdə `gross` = snapshot, `discount`, `amount` =
`gross − discount`).

| Harada | Nə olur |
|---|---|
| Kiosk sifarişi (`POST /api/print-jobs` + `promoCode`) | Kod yoxlanır (anonim: istifadəçi limiti yoxdur); uyğun deyilsə `400` (`errors.promoCode`, səbəb oxuyanın dilində). Cavabda `price.promoCode`, `price.promoDiscount` (bütün qiymətə), `price.amountDue` |
| Bank QR intent-i | Yer tutulur (şərti artım — limit iki paralel ödənişdə də keçilmir), bank endirimli məbləği görür. Kod artıq tətbiq olunmursa (son yer getdi, müddət bitdi) tam qiymət və `promoRefusal` — müştəri ödəmədən öncə görür. Endirim hamısını örtürsə ödəniş dərhal `Succeeded`, QR yoxdur |
| Tətbiqlə ödəniş (`pay-with-app`) | Bonusdan sonra qalan məbləğə; istifadəçi limiti yoxlanır. Kioskda kod verilməyibsə istifadəçi onu tətbiqdə yaza bilər (`promoCode`; önizləmə `POST promos/validate` + `kioskId` + bonusdan sonrakı məbləğ ilə). `GET /api/payments/{id}/app`: `promoCode`, `promoDiscount` (bonussuz), `promoDiscountWithBonus`, `promoRefusal`. İstəyə görə `expectedAmount` — tətbiqin göstərdiyi pul hissəsi; fərqlidirsə `409 payments.priceChanged`, heç nə tutulmur |
| Ödəniş baş tutmadı (vaxt bitdi, rədd, metod dəyişdi, kart rədd edildi) | Yer geri verilir (`Released`) |
| Tam refund | İstifadə ləğv olunur (`Cancelled`), yer boşalır; **endirim pul kimi qaytarılmır** — refund ödənilən məbləğdəndir |
| Gec gələn ödəniş (vaxtı bitmiş intent sonradan ödənildi) | İstifadə yenidən tutulur (limitdən asılı olmayaraq — pul endirimli alınıb) |

Feature flag `promo_codes` söndürüləndə heç bir kod qəbul olunmur (`promos.disabled`).

| Endpoint | Kim | |
|---|---|---|
| `POST /api/promos/validate` | istənilən giriş etmiş | `{ code, kioskId?, amount }` → `{ code, valid, discount, amountDue, reasonCode?, reason? }`; heç nə tutulmur |
| `POST /api/pricing/quote` + `promoCode` | istənilən giriş etmiş | Əlavə sahələr: `promoCode`, `promoDiscount`, `amountDue`, `promoRefusal` |
| `GET /api/admin/promos?code=&active=` · `GET /api/admin/promos/{id}` | `promos.manage` | Siyahı; `usedCount` (tutulan yerlər), `confirmedCount`, `totalDiscount` |
| `POST /api/admin/promos` (`Idempotency-Key`) · `PUT /api/admin/promos/{id}` | `promos.manage` | `{ code, kind: Percent\|Fixed, value, startsAt?, endsAt?, totalLimit?, perUserLimit?, minAmount, kioskIds?, profileIds?, description? }`; kod dəyişmir; tutulmuş kod `409 promos.codeTaken`; audit |
| `POST /api/admin/promos/{id}/activate` · `.../deactivate` | `promos.manage` | Silmə yoxdur — istifadə olunmuş kod ödənişlərin tarixçəsindədir |

## Qəbzlər (NeoPrint-in məlumat qəbzi)

Hər uğurlu ödənişin (çap və balans artırılması) **bir** qəbzi var: qəbzin id-si ödənişin
id-sidir, ona görə təkrar hadisə ikinci qəbz yarada bilməz və kiosk qəbzi ödəniş id-si ilə
tanıyır. Nömrə `NP-{il}-{000001}` — il üzrə ardıcıl, boşluqsuz (sayğac qəbzlə eyni
tranzaksiyadadır). PDF QuestPDF ilə hazırlanır (`docs/DESIGN_SYSTEM.md` §5.5), storage-da
şifrəli saxlanılır; PDF yalnız görünüşdür — oxuna bilməsə ödəniş və fiskal məlumatdan yenidən
qurulur. Fiskal çek hələ yoxdursa PDF-də "Fiskal çek hazırlanır" yazılır; çek veriləndə
(`FiscalReceiptIssued`) qəbz yenidən qurulur və göndərildiyi bütün ünvanlara təkrar gedir
(itən hadisə üçün 5 dəqiqəlik ehtiyat işi). Qəbzin dili: hesabın dili, anonimdə kioskun standart dili.

| Endpoint | Kim | |
|---|---|---|
| `GET /api/receipts` | `User` | Öz qəbzləri (ən yeni əvvəl): `id` (= ödəniş), `number`, `amount`, `purpose`, `fiscalStatus`, `fiscalShown`, `emailedCount`, `createdAt` |
| `GET /api/receipts/{id}/pdf` | `User` | Yalnız öz qəbzi (`404`) |
| `POST /api/receipts/{id}/email` (`Idempotency-Key`) | `User` | `{ email? }` — verilməsə hesabın təsdiqlənmiş ünvanına |
| `POST /api/kiosks/me/receipts/{paymentId}/email` (`Idempotency-Key`) | `Kiosk` | `{ email }` — yalnız həmin kioskun ödənişi və ödənişdən sonra 30 dəqiqə ərzində (`404`); kiosk tokeni poçt göndərən kimi istifadə oluna bilməz |
| `GET /api/admin/receipts?number=&paymentId=&page=&pageSize=` · `GET /api/admin/receipts/{id}/pdf` | `receipts.view` | Siyahı və PDF |
| `POST /api/admin/receipts/{id}/resend` (`Idempotency-Key`) | `receipts.view` | Yalnız əvvəl göndərildiyi ünvanlara (admin yeni ünvan əlavə edə bilməz); audit ünvanları yox, sayı yazır |

Email: PDF əlavə kimi; səhv ünvan `400 receipts.emailInvalid`; bir qəbz ən çox 5 ünvana
(`409 receipts.tooManyEmails`). Ünvanlar şəxsi məlumatdır — loqa maskalanmış yazılır.

## Nəticəsi naməlum sifarişlər (`Suspected`, B ssenarisi)

`StuckJobWatchdogJob` (hər dəqiqə): `Queued` job-un növbəyə düşmə, `Printing` job-un çapa
başlama vaxtı **və** kioskun son heartbeat-i `Job.SuspectedAfterMinutes`-dən (default 15)
köhnədirsə job `Suspected` olur. Avtomatik ləğv və refund **yoxdur**, ödəniş `Succeeded`
qalır; incident `JobSuspected` (High, `suspected:{jobId}` — bir dəfə). Uzun növbəsi olan,
amma heartbeat göndərən kiosk toxunulmur. `Job.SuspectedAutoResolveHours` (default 72)
keçəndə **bir dəfə** xatırlatma incident-i (`suspected-reminder:{jobId}`); başqa heç nə.

| Kiosk nə bildirir (`POST /api/print-jobs/{id}/status`) | Nəticə |
|---|---|
| `Suspected` job üçün `Printing` | Heç nə dəyişmir (cəhd `print_attempts`-ə yazılır) — nəticə gözlənilir |
| `Suspected` job üçün `Completed` | `Completed`, refund yoxdur; incident-lər avtomatik bağlanır |
| `Suspected` job üçün `Interrupted` | `Interrupted` → A ssenarisi (anonim bank ödənişi avtomatik karta qaytarılır); incident-lər bağlanır |
| Admin qərarından **sonra** gələn ziddiyyətli bildiriş | `200`, heç nə dəyişmir; cəhd saxlanılır, incident `suspected-late:{jobId}` (Medium) |

### Admin

| Endpoint | İcazə | |
|---|---|---|
| `GET /api/admin/print-jobs/suspected` | `jobs.view` | Ən köhnə əvvəl: `jobId`, `jobCode`, `kioskCode`, `suspectedAt`, `kioskLastHeartbeatAt`, `amount`, `paymentMethod`, `hasAccount`, `openClaims`, `overdue` (`SuspectedAutoResolveHours` keçib) |
| `GET /api/admin/print-jobs/{id}/investigation` | `jobs.view` | Araşdırma kartı: `detail` (job + tarixçə + `attempts` sayğac əvvəl/sonra), `payment` (metod, status, provider istinadı, qaytarıla bilən, kart hissəsinin istiqamətləri), kioskun statusu və son heartbeat-i, müştəri müraciətləri, həmin kioskda ±2 saat ərzində incident-lər və digər sifarişlər |
| `POST /api/admin/print-jobs/{id}/resolve` (`Idempotency-Key`) | `payments.refund` | `{ decision, amount?, destination?, reason, note }` — `reason` və `note` məcburi (`400`). `FullRefund` → `Cancelled` + bütün məbləğ (istiqamət matrisi və ya seçilən `destination`); `PartialRefund` (`amount` məcburi) → `Completed` + həmin məbləğ; `NoRefund` → `Completed`; `Reprint` → yenidən `Queued`, əlavə ödəniş yoxdur. Artıq həll olunubsa `409 printJobs.suspected.alreadyResolved`; ödənişsiz sifarişdə qaytarma qərarı `409 printJobs.suspected.nothingPaid`. Audit `printJobs.suspected.resolve`; incident-lər bağlanır |

Admin qərarı kağız sayğacını dəyişmir — sayğac yalnız printerin bildirişinə tabedir.

## Müştəri müraciətləri

| Endpoint | Kim | |
|---|---|---|
| `POST /api/support/claims` (`Idempotency-Key`) | anonim, IP üzrə saatda 10 (`RateLimits:SupportClaimPerHour`) | `{ jobCode, kioskCode, text, contact? }` — hər iki kod uyğun gəlməlidir, əks halda `404` (hansının səhv olduğu deyilmir). Cavab: `{ id, status, createdAt }` |
| `POST /api/me/claims` (`Idempotency-Key`) | `User` | `{ jobId, text }` — yalnız öz sifarişi (`404`). "Problem barədə bildir" |
| `GET /api/me/claims` | `User` | Öz müraciətləri və cavablar (`contact` qaytarılmır) |
| `GET /api/admin/support/claims?status=&jobId=&page=&pageSize=` | `jobs.view` | Siyahı; `contact` yalnız adminə görünür |
| `POST /api/admin/support/claims/{id}/answer` · `.../close` (`Idempotency-Key`) | `payments.refund` | Cavab (1–1000 simvol) / bağlama; bağlı müraciətə cavab `409 support.closed`; audit |

Mətn 1–1000 simvol (`400 support.textLength`); bir sifarişdə ən çox 5 açıq müraciət
(`409 support.tooManyOpen`). Hər müraciət `audit_logs`-a düşür; mətn və əlaqə məlumatı
audit-ə və loqa yazılmır.

## Balans (`/api/wallet`, `User` token)

| Endpoint | Nə edir |
|---|---|
| `GET /api/wallet` | `{ "balance": 5.00, "bonusPages": 0, "currency": "AZN" }` |
| `GET /api/wallet/transactions?page=&pageSize=` | Ledger, ən yenisi əvvəl: `type` (`TopUp`, `Payment`, `Refund`, `Bonus`, `VoucherRedeem`, `AdminAdjust`), `amount` (işarəli), `bonusPages`, `balanceAfter`, `createdAt`, `note` (refund səbəbi sorğunun `Accept-Language` dilində; admin düzəlişinin qeydi yazıldığı kimi) |
| `POST /api/wallet/top-up` (`Idempotency-Key`) | `{ "amount": 5 }` (1–500 ₼, 2 onluq) → kartla artırma; cavab `{ paymentId, status, wallet }`. Kart rədd etsə `409 payments.declined`, heç nə yazılmır |

**Balansı karta çıxarmaq, nağdlaşdırmaq və ya qaytarmaq üçün heç bir endpoint yoxdur** (plan 2.14) — test marşrutların tam siyahısını yoxlayır.

Həqiqət mənbəyi `wallet_transactions` ledger-idir; `wallets.Balance` onun cəmidir və
yalnız ledger sətri ilə eyni tranzaksiyada dəyişir. Hər sətrin unikal `DedupKey`-i var
(`topup:{paymentId}`, `payment:{id}:{n}`, `release:{id}:{n}`, `refund:{refundId}`, `adjust:{açar}`) — eyni hərəkət ikinci dəfə yazıla bilmir.

### Admin (`/api/admin/wallets`)

| Endpoint | İcazə | Nə edir |
|---|---|---|
| `GET /api/admin/wallets/{userId}`, `GET …/transactions` | `users.view` | Balans və ledger |
| `POST /api/admin/wallets/{userId}/adjust` (`Idempotency-Key`) | `wallets.adjust` | `{ "amount": 2.00, "bonusPages": 0, "reason": "…" }` — səbəb məcburidir (`400`), audit-ə düşür; balansı sıfırdan aşağı sala bilmir (`409 wallet.insufficient`), ona görə pulu heç yerə çıxara bilmir |

### Development (`/api/dev`, başqa mühitlərdə `404`)

| Endpoint | Nə edir |
|---|---|
| `POST /api/dev/payments/{id}/simulate-success?delaySeconds=&duplicate=&dropCallback=` | Müştəri bank tətbiqində ödəyir; callback gecikir / iki dəfə eyni anda gəlir / itir |
| `POST /api/dev/payments/{id}/simulate-fail` | Bank rədd edir (`payment.failed` callback) |
| `POST /api/dev/mock-bank/create-fails?fails=true\|false` | Bank "düşür": QR yaratmaq timeout verir |
| `POST /api/dev/mock-card/outcome?value=Success\|Decline\|Timeout` | Mock kartın növbəti çıxışları: uğur / rədd / pul tutulur, amma cavab itir |

Vaxtı keçmiş (bankda bağlanmış) QR-ı ödəmək `409 { "outcome": "QrNoLongerValid" }` qaytarır — bank tətbiqi də onu rədd edir.

## Admin: ödənişlər, gəlir, müştərilər (Addım 4, M10)

Hamısı admin tokeni ilə; siyahılar adminin **kiosk əhatəsinə** daraldılır (əhatədən kənar ödəniş `404`). Balans artırılması (kiosksuz ödəniş) yalnız bütün kiosklara baxan adminə görünür.

| Endpoint | İcazə | Nə edir |
|---|---|---|
| `GET /api/admin/payments?status=&method=&channel=&purpose=&kioskId=&from=&to=&hadError=&refund=pending\|failed&search=&page=&pageSize=` | `payments.view` | Ən yenisi əvvəl. `status`/`method` təkrarlana bilər. `search` — ödəniş ID-si, 6 rəqəmli sifariş kodu və ya provayder istinadı. `refund=pending` — yolda olan refund-u olanlar, `failed` — `RefundFailed`. `hadError=true` — xəta səbəbi olan və ya `Failed`/`RefundFailed`. Sətir: `{ id, status, method, channel, purpose, amount (pul hissəsi), refunded, currency, kioskId, kioskCode, jobId, jobCode, userId, failureReason, createdAt, paidAt }` |
| `GET /api/admin/payments/{id}` | `payments.view` | Ödəniş detalı: `payment` (yuxarıdakı sətir), `split` (`gross`, `volumeDiscount`, `promoDiscount`, `promoCode`, `external`, `wallet`, `bonusPages`, `bonusValue`), `refundable`, `provider`, `providerRef`, `expiresAt`, `kioskName`, `job`, `user` (`publicId`, `email`), `receiptNumber`, `fiscal[]` (növ, status, fiskal ID, terminal, xəta), `refunds[]` (məbləğ, istiqamət `OriginalMethod`/`Wallet`/`Voucher`, status, səbəb, kim başladıb, cəhdlər, xəta, voucher kodu, qeyd), `timeline[]`. Əməliyyatlar mövcud endpoint-lərlədir: `GET …/refund-options`, `POST …/refund`, `POST /api/admin/refunds/{id}/retry` |
| `GET /api/admin/vouchers?page=&pageSize=` | `vouchers.view` | `{ code, amount, status (Active\|Redeemed\|Expired), paymentId, refundId, createdAt, expiresAt, redeemedBy, redeemedAt }` |
| `GET /api/admin/reports/revenue?from=yyyy-MM-dd&to=yyyy-MM-dd&kioskId=` | `reports.view` | Bakı günləri, hər ikisi daxil, ən çox 366 gün (`400 reports.periodInvalid`). `totals` — `sales` (çap ödənişlərinin pul hissəsi: kart/bank + balans), `payments`, `refunds` (dövrdə tamamlanan qaytarmalar), `net`, `discounts` (promokod), `topUps` (balans artırılması — gəlir deyil, müştərinin pulu); `days[]`, `methods[]`, `kiosks[]`. Bonus səhifələr pul deyil, sayılmır |
| `GET /api/admin/dashboard/payments` | `payments.view` | Dashboard-un pul kartı: `today`, `lastSevenDays[]` (7 gün), `failedRefunds`, `failedFiscal`, `openMoneyIncidents` (uğursuz refund, fiskal çek, `Suspected`) |
| `GET /api/admin/users?search=&status=Active\|Blocked&page=&pageSize=` | `users.view` | Tətbiq müştəriləri (adminlər `api/admin/admins`-dir). `search` — email-in bir hissəsi və ya dəqiq `NP-XXXXXX`; silinmiş hesablar yoxdur |
| `GET /api/admin/users/{id}` | `users.view` | `profile`, `language`, `wallet`, `recentJobs[]` (son 20), `fileCount`, `fileBytes`, `referred`, `consents[]`; Addım 6: `files[]` (son 50, yalnız metadata — `{ id, maskedName, extension, sizeBytes, source, isSensitive, status, pageCount, createdAt }`; **ad serverdə maskalanır**: ilk iki hərf + `•••` + uzantı, həssas faylın adı tam gizli; məzmun və tam ad heç vaxt qaytarılmır) və `referrals` — `{ invitedBy?: { id, publicId }, totals: { registered, rewarded, waiting, bonusPagesEarned }, items[] }` (onun son 20 dəvəti, aşağıdakı `ReferralRow`) |
| `GET /api/admin/referrals?status=Waiting|Rewarded|Held&page=&pageSize=` | `referrals.view` | Referral proqramı: `totals` `{ registered, rewarded, waiting, held, bonusPagesGiven }` (bütün proqram üzrə; `held` — ilk ödənişli çap oldu, bonus yazılmadı; `bonusPagesGiven` — dəvət edən + dəvət olunan), `top[]` (ən çox mükafatı olan 10 dəvət edən: `{ inviter, invited, rewarded, bonusPages }`), `items[]` — tarixçə, ən yenisi əvvəl: `ReferralRow` = `{ id, inviter: { id, publicId }, invitee: { id, publicId }, status: Registered|Rewarded, hold: None|SameDevice|MonthlyLimit|InviterInactive, createdAt, qualifiedAt?, inviterBonusPages, inviteeBonusPages }`, `total`, `page`, `pageSize` (≤ 100). İstifadəçilər PublicId ilə verilir, email yoxdur (silinmiş hesabın `publicId`-si boşdur). Yanlış `status` → `400 referrals.statusInvalid` |
| `POST /api/admin/users/{id}/block` · `.../unblock` (`Idempotency-Key`) | `users.block` | `{ reason }` məcburi (1–500, `400 users.reasonRequired`), audit-ə düşür. Blok: bütün refresh token-lər ləğv olunur və istifadəçi Redis blocklist-ə düşür — əlindəki access token **dərhal** `403` alır (Redis cavab vermirsə yoxlama MongoDB-dəki statusa keçir). Balansa toxunulmur. Təkrar blok / aktiv hesabı blokdan çıxarmaq heç nə dəyişmir |

## Hesabın dili (`User` token, Addım 5, T-152)

### `PUT /api/users/me`

`{ "language": "az" | "en" | "ru" }` → `204`. Plandakı `PUT users/me`; bu addımda yalnız dil (ad dəyişmir).
Hesabın dilidir: email-lər, push-lar (hesabın **bütün** push cihazlarının dili eyni atomik yeniləmədə dəyişir) və
növbəti girişdə `user.language` bu dildə olur — tətbiq başqa telefonda da onu götürür. Başqa dəyər, boş və ya böyük
hərf (`EN`) → `400 common.languageInvalid`. Eyni dili təkrar göndərmək heç nə dəyişmir.

Hüquqi sənədlər üçün yeni endpoint yoxdur: tətbiq `GET /api/legal/{Privacy|Terms}?lang=` (anonim) ilə cari versiyanı
oxuyur; tərcümə yoxdursa cavabın `language`-i `az` olur və ekran bunu deyir. Qeydiyyat bu versiyaları göndərir.

## Şəxsi QR və "Kiosk üçün hazırla" (mobil tətbiq + kiosk, Addım 5)

Plan 9.4; layihə sahibinin qərarı `DECISIONS.md` (T-144). Qiymət həmişə kioskun **cari** profili ilə hesablanır.

### `GET /api/users/me/qr` · `GET /api/users/me/qr.png` (`User`)

`{ code, expiresAt, rotationSeconds }` — `code` = `NPU:{userId:N}:{unixSeconds}:{hmac}` (HMAC-SHA256, açar `NEOPRINT_QR_HMAC_KEY`, base64url),
`UserQr.RotationSeconds` (60) müddətində keçərli; tətbiq vaxtı bitməzdən əvvəl yenisini alır. `qr.png` eyni kodu PNG kimi qaytarır,
bitmə vaxtı `X-Qr-Expires-At` başlığındadır. Hər iki cavab `Cache-Control: no-store`.

### `/api/prepared-prints` (`User`)

- `GET` → `{ items: [{ kind, id, fileId, fileName, pages, preset?, options?, state, kioskId?, kioskCode?, kioskName?, amount?, currency?, jobCode?, createdAt, collectBy? }] }`.
  `kind`: `Prepared` (ödənişsiz, istənilən kiosk) və ya `Paid` (əvvəlcədən ödənilmiş, bir kiosk). `state`: `Ready`, `Printing`, `Printed`, `FileUnavailable`, `Paid`.
- `POST` (`Idempotency-Key`) `{ fileId, options?: { copies?, color?, duplex?, paper?, pagesPerSheet?, pageRange? } }` — ödənişsiz hazırlıq; `null` sahə kioskda seçilir. Səhv seçim → `400`.
- `DELETE /{id}` → `204` (siyahıdan çıxarır).
- `POST /paid` (`Idempotency-Key`) `{ kiosk, fileId, options, expectedAmount }` → `{ jobId, jobCode, paymentId, amount, currency, expiresAt, pages, sheets }`.
  Kioskda `Source=UserQr`, `HeldForUser` sifariş; `pay-with-app` ilə ödənilir və **növbəyə düşmür** (`Paid` qalır, kağız ayrılmır).
  `expectedAmount` kioskun cari qiymətindən fərqlidirsə → `409 printJobs.priceChanged` (`{amount}` — yeni qiymət), heç nə yaradılmır.
  Kiosk seçimi dəstəkləmirsə → `400 printJobs.colorUnavailable` / `a3Unavailable` / `duplexUnavailable`. `GET /api/payments/{id}/app` cavabında `collectAtKiosk: true`.
- `POST /paid/{jobId}/cancel` → `204`, pul balansa (`RefundReason.UserCancel`); artıq götürülüb və ya ödənilməyibsə `409 printJobs.notHeld`.
- Götürülməyən ödənişli hazırlıq `PreparedPrint.AutoCancelDays` (7) gündən sonra saatlıq `uncollected-print-jobs` işi ilə ləğv olunur, pul balansa (`RefundReason.NotCollected`).

### Kiosk: `/api/print-jobs/pending-for-user` (`Kiosk`)

- `POST /api/print-jobs/pending-for-user` `{ code }` → `{ grant, expiresAt, items: [{ kind, id, fileName, pages, preset?, options?, amount?, currency?, here, kioskCode?, createdAt }] }`.
  QR-ın HMAC-ı və yaşı yoxlanır: tanınmayan / dəyişdirilmiş / bloklanmış hesab → `409 userQr.invalid` (hesabın olub-olmadığı deyilmir), köhnə → `409 userQr.expired`.
  `grant` — yalnız **bu kiosk** və bu istifadəçi üçün 5 dəqiqəlik icazə (Redis `userqr:{grant}`), hər istifadədə uzanır. Kod URL-ə düşməsin deyə `POST`.
  `here: false` — başqa kiosk üçün ödənilmiş sifariş (göstərilir, burada çap olunmur).
- `GET /api/print-jobs/pending-for-user` (`X-User-Qr-Grant` başlığı) — eyni siyahı; icazə bitib və ya başqa kioskundur → `409 userQr.grantExpired`.
- `POST /api/print-jobs/pending-for-user/prepared/{id}` (`X-User-Qr-Grant`, `Idempotency-Key`) `{ options, promoCode? }` → `PrintJobResponse`
  (`Source=UserQr`, `PendingPayment`, istifadəçiyə bağlı). Kioskun sifariş qaydaları və ödəniş ekranları (9.1 / 9.2) ilə eynidir.
- `POST /api/print-jobs/pending-for-user/paid/{jobId}/release` (`X-User-Qr-Grant`) → `PrintJobResponse` (`Queued`). Printer imkanı və kağız indi yoxlanır.
  Başqa kiosk üçün ödənilib → `409 printJobs.paidAtAnotherKiosk` (`{kiosk}`). Təkrar çağırış dəyişiklik etmir.
- `GET /api/print-jobs/{id}/file` götürülməmiş ödənişli hazırlıq üçün `409 printJobs.fileNotPrintable`.

## Planlanmış çap (`/api/schedules`, `User` token, Addım 5)

Plan 9.5; qərarlar `DECISIONS.md` (T-145). Sifarişin özü planlama anında yaradılan `Source=Scheduled` job-dur
(ödənilir, `Paid` qalır, kağız ayırmır, kioskun QR siyahısında görünmür). Qiymət kioskun cari profilindən snapshot olunur.

- `POST` (`Idempotency-Key`) `{ kiosk, fileId, options, scheduledAt, expectedAmount }` →
  `{ scheduleId, jobId, jobCode, paymentId, amount, currency, paymentExpiresAt, scheduledAt }`; ödəniş `payments/{paymentId}/pay-with-app` ilə.
  Ödənişdən sonra `GET /api/payments/{id}/app` cavabında `scheduledAt` (və `collectAtKiosk: false`) olur.
  `scheduledAt` UTC: ən azı 15 dəqiqə sonra (`400 schedules.tooSoon`), ən çox `Schedule.MaxDaysAhead` (30) gün sonra (`400 schedules.tooFar`).
  Qiymət dəyişibsə `409 printJobs.priceChanged` (`{amount}`), kiosk seçimi dəstəkləmirsə `400` — `/api/prepared-prints/paid` ilə eyni.
- `GET` → `{ items: [{ id, jobId, jobCode, fileId, fileName, pages, options, kioskId, kioskCode?, kioskName?, amount, currency, scheduledAt,
  status, jobStatus, paymentId?, autoCancelAt?, createdAt }] }`, ən yenisi əvvəl. `status`: `PendingPayment`, `Scheduled`, `AwaitingUser`,
  `Sent`, `Cancelled`, `Expired`; ödənilmədən ləğv olunanlar siyahıda yoxdur. `autoCancelAt` — `AwaitingUser` üçün avtomatik ləğv vaxtı.
- `PUT /{id}/time` `{ scheduledAt }` → `ScheduleView` (`Scheduled`); eyni vaxt qaydaları. Gözləmədə deyilsə `409 schedules.notActive`.
- `POST /{id}/print-now` (`Idempotency-Key`) → `ScheduleView` (`Sent`, job `Queued`). Vaxtından əvvəl də işləyir. Kiosk `Online` deyilsə
  `409 schedules.kioskUnavailable` (`{kiosk}`, `{status}`); printer/kağız çatmırsa kiosk sifariş qaydalarının `409`-u. Təkrar çağırış dəyişiklik etmir.
- `DELETE /{id}` → `204`; job ləğv olunur, ödənilmişsə pul balansa (`RefundReason.ScheduleCancel`, qaytarma fiskal çeki ilə). Çapa verilibsə `409 schedules.notActive`.
- Arxa plan: `scheduled-print-due` (hər dəqiqə) — vaxtı çatan `AwaitingUser` olur, istifadəçiyə `/hubs/user` üzərindən `ScheduleDue`
  `{ scheduleId, jobId, scheduledAt, status }` (bir dəfə; `Schedule.ReminderLeadMinutes` > 0 olanda vaxtdan o qədər əvvəl);
  `scheduled-print-auto-cancel` (10 dəq) — `Schedule.AutoCancelHours` (24) cavabsız qalan `Expired`, pul balansa.
- Aktiv planlanmış çapı olan fayl silinmir: `DELETE /api/files/{id}` → `409 files.inUseBySchedule`.
- Vaxt çatanda və avtomatik ləğvdə bildiriş + push da gedir (`ScheduleDue`, `ScheduleAutoCancelled` — aşağıda "Bildirişlər").

---

## Fayl paylaşımı (`/api/shares`, `User` token, Addım 6)

Plan 6, 8.2; qərarlar `DECISIONS.md` (2026-10-05, paylaşımın icazə və texniki modeli). Alıcıya heç nə öz razılığı olmadan
düşmür: paylaşım göndərənin faylına, **göndərilən andakı PDF-ə** istinad edir; alıcının faylı yalnız "Fayllarıma saxla"
ilə yaranır. Kolleksiya `file_shares`.

- `POST` `{ fileId, toPublicId, message?, confirmSensitive }` → `ShareView`. Feature flag `file_sharing` söndürülübsə
  `404 features.unavailable` (siyahılar və saxlama açıq qalır). `toPublicId` — `NP-XXXXXX` (böyük-kiçik hərf fərqi yoxdur);
  formatı səhvdirsə `400 shares.publicIdInvalid`, belə istifadəçi yoxdursa, bloklanıb və ya silinibsə `404 notFound.shareRecipient`
  (bloklanma göndərənə bildirilmir), özünə `400 shares.toSelf`. Fayl göndərənin deyilsə `404`, hazır deyilsə `409 files.notReadyNamed`.
  Həssas fayl `confirmSensitive: true` olmadan `400 shares.sensitiveNeedsConfirmation` (tətbiq xəbərdarlığı göstərib təsdiqlə
  yenidən göndərir). `message` — ən çox 200 simvol, bir sətir (idarəetmə və bidi simvolları boşluğa çevrilir), artığı
  `400 shares.messageTooLong`. Eyni faylın eyni PDF-i eyni alıcıya, o saxlamadan, yenidən göndərilirsə — **eyni paylaşım**
  qaytarılır (ikinci bildiriş getmir). Alıcıya `FileShared` bildirişi + push.
- `GET /sent`, `GET /received` → `{ items: [ShareView] }`, ən yenisi əvvəl (100). `ShareView` =
  `{ id, fileName, pageCount, isSensitive, message?, otherPublicId, createdAt, availability, savedFileId?, savedAt? }`;
  `otherPublicId` — göndərilənlərdə alıcının, alınanlarda göndərənin id-si. `availability`: `Available`, `Saved`
  (alıcının nüsxəsi var), `Gone` (göndərən faylı və ya hesabı silib / bloklanıb — "Fayl artıq yoxdur"), `Changed`
  (göndərən faylı göndərdikdən sonra redaktə edib — alıcı göndəriləndən başqa şey almır).
- `GET /{id}/pages/{n}.png?width=` — yalnız alıcı, saxlamadan əvvəl önizləmə (`GET /api/files/{id}/pages/{n}.png` ilə eyni
  qaydalar). Başqasının paylaşımı `404`; `Gone` / `Changed` → `409 shares.fileGone` / `shares.fileChanged`.
- `POST /{id}/save` → `UserFileResult` (`source: "Shared"`, `Ready`): alıcının kvotasında müstəqil şifrəli nüsxə (cari
  məzmun və PDF yenidən şifrələnib kopyalanır; tərtibatın JSON-u və şəkilləri keçmir — nüsxə PDF-dir). Həssaslıq, skan
  kilidi və real ölçü kağızı keçir; avtomatik silinmə alıcının default qaydası ilə (həssas → `AfterPrint`). Yenidən
  çağırış nüsxə varsa **eyni nüsxəni** qaytarır; yarıda qalmış saxlama eyni id ilə davam edir. Kvota dolubsa
  `400 files.quotaFull`; `Gone` / `Changed` → `409`. Göndərənin sonradan silməsi saxlanmış nüsxəyə təsir etmir.
- `GET /qr.png` — istifadəçinin "mənə fayl göndər" QR-ı: `neoprint://share/NP-XXXXXX` (tətbiq skan edəndə paylaşım
  ekranı alıcı doldurulmuş açılır).
- Hesab silinəndə (`AccountDeletionJob`) istifadəçinin hər iki istiqamətdəki paylaşımları silinir.

---

## Referral (`/api/referrals`, `User` token, Addım 6)

Plan 2.6, 6; qərarlar `DECISIONS.md` (2026-10-05, referral qaydaları və texniki model).

- Qeydiyyat: `POST /api/auth/register` `referralCode` (böyük-kiçik hərf fərqi yoxdur). Naməlum kod qeydiyyatı dayandırmır, sadəcə
  dəvət yaranmır; `referral` flag-i söndürülübsə kod nəzərə alınmır. Uyğun kodla `referrals`-da `Registered` qeyd yaranır.
- Mükafat: `PrintJobCompleted` → `ReferralRewardConsumer`. Dəvət olunanın pulla ödənmiş (balans / kart / bank, `Parts.Money > 0`)
  ilk `Completed` job-u dəvəti bir dəfə həll edir: dəvət edənə `Referral.BonusPages`, dəvət olunana `Referral.InviteeBonusPages`
  (ledger `Bonus`, dedup `referral:{id}:inviter|invitee`). Yalnız bonus səhifə / 100% promokodla ödənən job sayılmır. Mane olan
  hallar (bonus yazılmır, qeyd `Registered` qalır): eyni cihaz (dəvət edənin cihazı və ya artıq mükafatlanmış başqa dəvət olunanın
  cihazı), `Referral.MaxRewardsPerMonth` (20; 0 = limitsiz, təqvim ayı UTC), dəvət edən bloklanıb / silinib. Sonra
  `ReferralQualified` hadisəsi → `ReferralRewarded` bildirişi (dəvət edənə `neoprint://referral`, bonus aldısa dəvət olunana
  `neoprint://wallet`).
- `GET /me` → `{ code, link, invited, rewarded, waiting, bonusPagesEarned, bonusPagesPerInvite, inviteeBonusPages }`;
  `link` = `https://neoprint.az/r/{code}`; `waiting` — ilk ödənişli çapı hələ olmayanlar.
- `GET /me/qr.png` — linkin QR-ı (backend QRCoder).

---

## Bildirişlər və push (`User` token, Addım 5)

Plan 6, 8.2, 8.4; qərarlar `DECISIONS.md` (T-146). Başlıq və mətn serverdə mesaj açarı + arqumentlər kimi saxlanır,
oxuyanın dilində yazılır: siyahı `Accept-Language` ilə, push cihazın qeydiyyatdakı dili ilə.

- `GET /api/notifications?take=20&before={next}` → `{ items: [{ id, type, title, body, link?, createdAt, readAt? }], unread, next? }`,
  ən yenisi əvvəl. `take` 1–50. `unread` — istifadəçinin bütün oxunmamışları (zəngin sayğacı). `next` sonuncu səhifədə yoxdur.
- `POST /api/notifications/{id}/read` → bildiriş (`readAt` ilə). Təkrar çağırış ilk vaxtı saxlayır. Başqasınınkı → `404 notFound.notification`.
- `POST /api/users/me/fcm-token` `{ token, language, platform }` → `204`. `language` az/en/ru (başqası → az), `platform` android/ios/web
  (başqası → `unknown`). Eyni token başqa hesabdadırsa oradan silinir (telefonda başqası daxil oldu). Hesabda ən çox 10 cihaz, ən köhnəsi düşür.
  Boş və ya boşluqlu token → `400 notifications.tokenInvalid`.
- `DELETE /api/users/me/fcm-token` `{ token }` → `204` (çıxış). Yalnız öz hesabındakı token silinir.
- `/hubs/user` → `Notification` `{ notificationId, type }`: yeni bildiriş saxlandı (siyahı və banner üçün).
- Push (FCM HTTP v1, `notification` + `data`): `data` = `{ notificationId, type, link? }`. FCM açarı OpenBao-da yoxdursa
  `LoggingPushSender` yalnız loqa yazır. FCM tokeni tanımırsa (`UNREGISTERED` və s.) token hesabdan silinir; müvəqqəti xətada
  yalnız çatmayan cihaza təkrar göndərilir.

| `type` (plan 8.4) | Nə vaxt | `link` |
|---|---|---|
| `JobCompleted` | Qeydiyyatlı istifadəçinin sifarişi çap olundu | `neoprint://jobs/{jobId}` |
| `ScheduleDue` | Planlanmış çapın vaxtı çatdı (və ya xatırlatma) | `neoprint://schedules/{id}` |
| `ScheduleAutoCancelled` | Cavab verilmədi, ləğv olundu, pul balansa | `neoprint://schedules/{id}` |
| `RefundCompleted` | Pul qaytarıldı (planlanmış çapın ləğvi istisna) | `neoprint://wallet` |
| `JobInterruptedRefundOffer`, `KioskOutOfServiceRefundOffer` | Addım 7 (kiosk dayananda) | — |
| `FileShared` | Başqa istifadəçi fayl göndərdi (`{from}` — onun `NP-` id-si, `{name}`) | `neoprint://shares` |
| `ReferralRewarded` | Dəvət mükafatlandı: dəvət edənə (`{pages}`), bonus aldısa dəvət olunana | `neoprint://referral` / `neoprint://wallet` |
| `ExportReady`, `FileAutoDeleted` | Addım 5 / M6 (T-147) | — |

---

## Məxfilik (`User` token, Addım 5)

Plan 2.13; qərarlar `DECISIONS.md` (T-147).

- `POST /api/users/me/export` → `{ id, status, requestedAt, readyAt?, expiresAt?, sizeBytes, includesFiles, downloadUrl?, failureReason? }`.
  Arxiv arxa planda hazırlanır (`status`: `Preparing` → `Ready` / `Failed`, 24 saatdan sonra `Expired`); hazırlanan və ya hələ
  keçərli olan varsa yenisi yaranmır, o qayıdır. Hazır olanda `ExportReady` bildirişi (`neoprint://privacy/export`).
  ZIP: `README.txt`, `profile.json`, `payments.json`, `refunds.json`, `balance.json`, `orders.json`, `receipts/*.pdf`, `files.json`,
  `files/` (orijinal + versiyalar; ümumi ölçü `Privacy.ExportMaxMegabytes`-dan böyükdürsə yalnız `files.json`, `includesFiles: false`).
  Şifrə hash-i, token-lər, push token-ləri, provayder istinadları yoxdur.
- `GET /api/users/me/export` → sonuncu arxiv, yoxdursa `204`. `downloadUrl` yalnız `Ready` ikən: `api/exports/download?token=…`.
- `GET /api/exports/download?token=…` (**anonim**, sistem brauzeri üçün) → `application/zip`. Token HMAC-dır (açar
  `NEOPRINT_DATA_PROTECTION_KEY`-dən HKDF ilə), arxivin vaxtı bitənə qədər keçərlidir; saxta / vaxtı keçmiş → `404 privacy.exportLinkInvalid`.
- `GET /api/users/me/delete` → `{ blockers: { activePrints, scheduledPrints, uncollectedOrders, pendingRefunds, any }, balance, currency, bonusPages }`
  — silməyə nə mane olur və nə itəcək.
- `POST /api/users/me/delete` (`Idempotency-Key`) `{ password, confirmation }` → `204`. `confirmation` — `privacy.deleteWord`
  (az `SİL`, en `DELETE`, ru `УДАЛИТЬ`; böyük-kiçik hərf və İ/I fərq etmir). Mane varsa `409 privacy.deleteBlocked`, yanlış şifrə
  `400 privacy.deletePassword`, yanlış söz `400 privacy.deleteConfirmation`. Hesab dərhal bağlanır: email hash olur (həmin
  ünvanla dərhal yeni qeydiyyat mümkündür), ad, şifrə, push cihazları silinir, sessiyalar ləğv olunur, access token blok
  siyahısına düşür. `AccountDeletionJob` (10 dəq) `Privacy.AccountDeletionGraceHours` (24) keçəndən sonra faylları bütün
  provayderlərdən, arxivləri, bildirişləri silir; qəbzlərdən email, razılıqlardan IP / cihaz, müraciətlərdən mətn silinir.
  Ödəniş, refund, fiskal çek, qəbz və balans hərəkətləri anonim hesaba bağlı qalır.

---

## Hələ mövcud olmayan endpoint-lər

Planın Bölmə 6-dakı siyahısından bunlar sonrakı addımlardadır: skan və paylaşım (Addım 6), telemetriya, incident səhifəsi və operator paneli (Addım 7). Addım 4-ün ödəniş,
refund, wallet, fiskal çek, qəbz, promokod, istifadəçi hub-ı və admin pul səhifələri yuxarıdakı bölmələrdədir.

Addım 2-nin bütün endpoint-ləri (fayllar, redaktə və versiyalar, səhifə
şəkilləri, formatlar, storage provayderləri və sinxronizasiya, kiosk idarəsi,
qiymət profilləri və `pricing/quote`) yuxarıdakı bölmələrdədir.
