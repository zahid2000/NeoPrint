## ADR-0007 — Bildirişlər: bir dəfə saxlanır, outbox ilə çatdırılır, push öz FCM klientimizlə

### Status

Accepted (2026-10-04, Addım 5, T-146)

### Context

Plan 8.2 / 8.4 istifadəçiyə bildiriş siyahısı, tətbiq daxili banner və push tələb edir (`JobCompleted`, `ScheduleDue`,
`ScheduleAutoCancelled`, `RefundCompleted` …). Plan 2.0: push FCM HTTP v1 ilə, **öz klientimiz** (`HttpClient` + service
account JWT, BCL `RSA`), FirebaseAdmin yoxdur; açar yoxdursa `LoggingPushSender` (xəta vermir). Mobil tərəfdə
`firebase_core` + `firebase_messaging` təsdiqlidir.

Tələblər: eyni hadisə iki dəfə gəlsə də (consumer təkrarı, iş yenidən işləyəndə) bir bildiriş və bir push olmalıdır;
bildiriş itməməlidir; FCM müvəqqəti əlçatmaz olanda push sonra çatmalı, artıq çatmış cihaza ikinci dəfə getməməlidir;
mətn oxuyanın dilində olmalıdır (T-140 qaydası); açar yalnız OpenBao-da olmalı, token və açar loqa düşməməlidir.

### Decision

1. **Saxlama.** `notifications` kolleksiyası: `UserId`, `Type`, `Title` / `Body` — `UserMessage.Encode()` (açar +
   arqumentlər, mətn yox), `Link` (`neoprint://…`), `DedupKey` (**unikal**: `{tip}:{nə}`, məs.
   `ScheduleDue:{id}:{vaxt}`), `ReadAt`, `Push` (`Pending` / `Sent` / `NoDevice`), `PushedTo` (çatılmış cihazların token
   hash-ləri).
2. **Yaratma.** `NotifyUserCommand` (`ITransactionalRequest`): `DedupKey` varsa heç nə; yoxdursa insert +
   `UserNotificationCreated` eyni tranzaksiyada outbox-a (ADR-0006). Hadisələri bildirişə çevirən consumer-lər
   (`PrintJobStatusChanged → Completed`, `RefundCompleted`) və planlanmış çap işləri bu əmri göndərir.
3. **Çatdırılma.** `UserNotificationCreated` → `DeliverNotificationCommand`: `/hubs/user`-ə `Notification` nudge, sonra
   istifadəçinin hər cihazına push — `PushedTo`-da olan atlanır. `TokenGone` (FCM `UNREGISTERED`, `SENDER_ID_MISMATCH`,
   token `INVALID_ARGUMENT`) → token hesabdan silinir. `Failed` (şəbəkə, 429, 5xx) → handler istisna atır, MassTransit-in
   eksponensial retry-ı təkrarlayır, çatılmış cihazlar yenə atlanır. Hamısı çatanda `Push = Sent`.
4. **Cihazlar.** `users.FcmTokens` = `{ Token, Language, Platform, RegisteredAt }` siyahısı (ən çox 10). Token
   qeydiyyatda **başqa hesablardan silinir** (telefonda başqası daxil oldu). Dil cihazındır: tətbiq telefonun dilini
   izləyir, push həmin dildə yazılır; siyahı isə `Accept-Language` ilə.
5. **FCM klienti.** `FcmPushSender`: RS256 ilə imzalanmış JWT (RFC 7523) → OAuth2 access token (bitməyə 5 dəqiqə qalana
   qədər keşdə, 401-də bir dəfə yenilənir) → `POST fcm.googleapis.com/v1/projects/{id}/messages:send`
   (`notification` + `data { notificationId, type, link }`). Açar `NEOPRINT_FCM_SERVICE_ACCOUNT_JSON` (OpenBao
   `neoprint/backend/providers`); yoxdursa və ya oxunmursa `LoggingPushSender` (yanlış açarın səbəbi açarı sitat
   gətirmədən loqa yazılır).
6. **Mobil.** `PushPlatform` interfeysi (Firebase yalnız Android / iOS / web-də; testdə və desktop-da `NoPushPlatform`).
   Banner `/hubs/user`-in `Notification`-ından gəlir — FCM olmadan da işləyir; ön planda gələn FCM mesajı eyni bildirişi
   ikinci dəfə göstərmir.

### Alternatives

- **FirebaseAdmin SDK** — plan 2.0 qadağan edir (lazımsız asılılıq); bizə yalnız bir endpoint lazımdır.
- **Push-u hadisə consumer-lərinin içində birbaşa göndərmək** — təkrar çatdırılmada ikinci push, FCM düşəndə bildiriş
  itkisi; siyahı üçün ayrıca yazı da lazım olardı.
- **Mətni saxlamaq** — oxuyanın dili dəyişəndə köhnə dildə qalır (T-140-da rədd edilmiş yanaşma).
- **Data-only push + mətni tətbiqdə yazmaq** — tətbiq bağlı olanda (xüsusən iOS) bildiriş göstərilməz.

### Consequences

- Bir hadisə = bir bildiriş = hər cihaza bir push; FCM-in öz idempotency açarı yoxdur, ona görə çox nadir halda
  (FCM qəbul edib, `PushedTo` yazılmadan proses düşüb) bir cihaz eyni push-u iki dəfə ala bilər — qəbul olunan risk.
- Tam istifadəçi yazısı (`UserRepository.UpdateAsync` — replace) eyni anda əlavə olunan tokeni silə bilər; tətbiq hər
  açılışda tokeni yenidən göndərir.
- Plan 8.4-ün qalan tipləri (`FileShared`, `ReferralRewarded`, `ExportReady`, `FileAutoDeleted`, refund təklifləri) eyni
  `NotifyUserCommand` ilə öz addımlarında qoşulur.

### Security Impact

Service account açarı yalnız OpenBao-da; `tools/dev-secrets/set-fcm-key.ps1` onu stdin ilə yazır. Loqa açar, access
token, FCM token və bildiriş mətni (fayl adı ola bilər) yazılmır — yalnız token hash-i (SHA-256, 16 hex), HTTP status
və FCM xəta kodu. Token başqa hesaba keçəndə əvvəlki hesabdan silinir. `link` yalnız `neoprint://` ola bilər, tətbiq onu
öz marşrut cədvəlinə qarşı yoxlayır.

### Operational Impact

Açar dəyişəndə API restart olunur (konfiqurasiya başlanğıcda oxunur). Uğursuz çatdırılmalar MassTransit retry-ından sonra
`_error` növbəsinə düşür (admin dashboard-un növbə dərinliyi). Bildirişlər istifadəçi silinəndə T-147-də silinir.

### Migration

`users.FcmTokens` əvvəl sətir siyahısı idi və heç yerdə yazılmırdı (boş `[]`) — boş massiv yeni tipə də uyğundur.
