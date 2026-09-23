# OpenBao — secret idarəsi

Backend-in (API və Worker-lərin) **bütün** gizli açarları OpenBao-dan oxunur.
Environment variable-da yalnız OpenBao-ya qoşulma məlumatı qalır. Repo-ya heç bir real
secret yazılmır (Bölmə 2.13).

**Niyə OpenBao?** MPL-2.0 lisenziyalı, Linux Foundation-a məxsus, tamamilə pulsuzdur.
HashiCorp Vault BSL lisenziyasına keçdiyi üçün seçilməyib. API-si Vault ilə uyğundur.

---

## 1. Arxitektura

```
                       ┌──────────────────────┐
   BAO_ADDR            │      OpenBao         │
   BAO_ROLE_ID_FILE ──►│  KV v2: neoprint/    │
   BAO_SECRET_ID_FILE  │  auth: approle       │
                       │  audit: file         │
                       └──────────┬───────────┘
                                  │ AppRole login (read-only policy)
                       ┌──────────▼───────────┐
                       │  OpenBaoConfiguration│
                       │  Provider            │  hər 5 dəqiqədən bir yenidən oxuyur
                       └──────────┬───────────┘
                                  │
                            IConfiguration
```

- **Klient:** öz yüngül klientimiz (`NeoPrint.Infrastructure/SecretStore`, `HttpClient` ilə).
  Əlavə kitabxana yoxdur — AppRole login, KV v2 oxuma, token renew, token bitəndə yenidən login.
- **Konfiqurasiya provayderi:** `OpenBaoConfigurationProvider` dəyərləri `IConfiguration`-a
  yükləyir. Kodun qalan hissəsi dəyərin haradan gəldiyini bilmir.
- **Rotasiya:** hər 5 dəqiqədən bir yenidən oxunur; `IOptionsMonitor` ilə bağlı dəyərlər
  (SMTP, Telegram, provayder açarları) **restart olmadan** yenilənir.

### Environment variable-lar (yalnız bootstrap — secret deyil)

| Dəyişən | Məna |
|---|---|
| `BAO_ADDR` | OpenBao ünvanı (məs. `https://bao.neoprint.az:8200`) |
| `BAO_ROLE_ID_FILE` | AppRole `role_id` faylının yolu |
| `BAO_SECRET_ID_FILE` | AppRole `secret_id` faylının yolu (Docker secret kimi mount olunur; env-də açıq yazılmır) |
| `BAO_KV_MOUNT` | KV v2 mount adı, default `neoprint` |
| `BAO_CACERT` | Production TLS üçün CA sertifikatı |

### Başlanğıc davranışı

- OpenBao əlçatmazdırsa **60 saniyə** ərzində eksponensial retry.
- Bundan sonra **Production-da tətbiq başlamır**.
- Məcburi açar OpenBao-da yoxdursa tətbiq başlamır və hansı açarın çatışmadığını log-a yazır
  (**dəyəri log-a yazmır**).
- İş zamanı OpenBao qopsa son yüklənmiş dəyərlərlə davam edir və High səviyyəli incident yaranır.

---

## 2. Secret yolları (KV v2, mount `neoprint`)

| Yol | Açarlar |
|---|---|
| `neoprint/backend/core` | `NEOPRINT_MONGO_URI`, `NEOPRINT_REDIS`, `NEOPRINT_RABBITMQ_URI`, `NEOPRINT_JWT_SIGNING_KEY` |
| `neoprint/backend/crypto` | `NEOPRINT_KEK_CURRENT_ID`, `NEOPRINT_KEK__{id}`, `NEOPRINT_STORAGE_SECRETS_KEY`, `NEOPRINT_DATA_PROTECTION_KEY`, `NEOPRINT_QR_HMAC_KEY` |
| `neoprint/backend/providers` | `NEOPRINT_SMTP_*`, `NEOPRINT_FCM_SERVICE_ACCOUNT_JSON`, `NEOPRINT_TELEGRAM_BOT_TOKEN`, `NEOPRINT_FISCAL_*`, `NEOPRINT_BANK_*` |
| `neoprint/backend/seed` | `NEOPRINT_STORAGE_SEED_*`, `NEOPRINT_SEED_ADMIN_EMAIL`, `NEOPRINT_SEED_ADMIN_PASSWORD` |
| `neoprint/backend/backup` (Faza 2) | `NEOPRINT_BACKUP_KEY` |

### Policy-lər

| Rol | Yollar | İcazə |
|---|---|---|
| `neoprint-api` | `core`, `crypto`, `providers`, `seed` | yalnız **read** |
| `neoprint-worker` | `core`, `crypto`, `providers` | yalnız **read** |

Hər iki rola əlavə olaraq `auth/token/renew-self` (update) və `auth/token/lookup-self` (read)
verilir — provayder token-i yeniləyə bilsin. **Write icazəsi heç bir rola verilmir.**

Policy faylları: [`openbao/policies/`](../openbao/policies/).

> Admin paneldən daxil edilən storage provayder açarları OpenBao-ya **yazılmır** —
> onlar MongoDB-də `NEOPRINT_STORAGE_SECRETS_KEY` ilə AES-GCM şifrəli saxlanılır.
> Onları admin idarə edir (Bölmə 2.10).

---

## 3. Development

```bash
cp .env.openbao-seed.example .env.openbao-seed
docker compose up
```

`docker compose up` zamanı iki konteyner qalxır:

| Konteyner | İş |
|---|---|
| `openbao` | Server rejimi, fayl storage (volume-da), audit device konfiqurasiya ilə elan olunub |
| `openbao-init` | Birdəfəlik: init → unseal → KV v2 mount → AppRole → policy-lər → role_id/secret_id faylları → seed |

`openbao-init` **idempotentdir**: təkrar işə salınanda yalnız çatışmayanı düzəldir və
**mövcud secret dəyərini heç vaxt üstələmir**.

### Nə harada saxlanılır

| Fayl | Məzmun | Qeyd |
|---|---|---|
| `.openbao/init.json` | Unseal açarı + root token | **`.gitignore`-dadır.** Yalnız development |
| `.openbao/neoprint-api.role_id` | API-nin AppRole role_id-si | `api` konteyneri read-only mount edir |
| `.openbao/neoprint-api.secret_id` | API-nin AppRole secret_id-si | eyni |
| `.openbao/neoprint-worker.*` | Worker üçün eyni cüt | |

### Açar materialı

`.env.openbao-seed.example`-də kriptoqrafik açarlar **boş** qalır:

```
NEOPRINT_JWT_SIGNING_KEY=
NEOPRINT_KEK__k1=
NEOPRINT_STORAGE_SECRETS_KEY=
NEOPRINT_DATA_PROTECTION_KEY=
NEOPRINT_QR_HMAC_KEY=
```

`openbao-init` onları ilk işə düşmədə **təsadüfi 256-bit** dəyərlə doldurur. Beləliklə repo-da
(hətta nümunə faylında) heç bir açar materialı olmur və hər developer maşınının öz açarları olur.

### Əl ilə baxmaq

```bash
export BAO_ADDR=http://localhost:8200
export BAO_TOKEN=$(sed -n 's/.*"root_token": *"\([^"]*\)".*/\1/p' .openbao/init.json)

bao kv list neoprint/backend
bao kv get neoprint/backend/core
bao policy read neoprint-api
bao audit list
```

### Sıfırdan başlamaq

```bash
docker compose down
docker volume rm neoprint_openbao-file neoprint_openbao-logs
rm -rf .openbao
docker compose up
```

> `.openbao/init.json` silinsə, amma volume qalsa, `openbao-init` unseal açarını tapmayıb
> aydın xəta ilə dayanır — bu qəsdəndir, səssizcə yenidən init etmir.

---

## 4. Production

`openbao-init` Production-da **işlədilmir**. Aşağıdakılar əməliyyat komandasının işidir.

### Server

- Ayrıca server və ya klaster, **Raft** storage (fayl storage OpenBao 2.7-dən köhnəlmiş sayılır).
- **TLS məcburidir** — `tls_disable` yalnız development konfiqindədir.
- Audit device (file) konfiqurasiyada elan olunur.

### Unseal

İki variant:

1. **Auto-unseal** — Transit secret engine (ayrıca OpenBao instansiyası) və ya bulud KMS.
   Tövsiyə olunan: operator müdaxiləsi tələb etmir.
2. **Manual unseal** — Shamir bölgüsü **3/5**: 5 unseal açarı 5 fərqli məsul şəxsə paylanır,
   unseal üçün istənilən 3-ü kifayətdir. Açarlar fərqli yerlərdə (ayrı-ayrı parol menecerləri /
   seyf) saxlanılır. Root token unseal-dan sonra **ləğv edilir** (`bao token revoke`), gündəlik iş
   üçün ayrıca məhdud token yaradılır.

### Secret-lərin yazılması

```bash
bao kv put neoprint/backend/core \
    NEOPRINT_MONGO_URI="..." \
    NEOPRINT_REDIS="..." \
    NEOPRINT_RABBITMQ_URI="..." \
    NEOPRINT_JWT_SIGNING_KEY="$(head -c 32 /dev/urandom | base64)"
```

### secret_id

- **Qısa ömürlü** yaradılır: CIDR bağlı (`secret_id_bound_cidrs`) və istifadə sayı limitli
  (`secret_id_num_uses`).
- Deploy zamanı **Docker secret** kimi ötürülür, env-də açıq yazılmır.

```bash
bao write -f auth/approle/role/neoprint-api/secret-id \
    cidr_list="10.0.1.0/24" \
    num_uses=5
```

### Backup

```bash
bao operator raft snapshot save neoprint-$(date +%F).snap
```

Snapshot şifrələnmiş şəkildə saxlanılır. Bərpa:

```bash
bao operator raft snapshot restore neoprint-2026-09-23.snap
```

Bərpadan sonra unseal proseduru yenidən icra olunur.

---

## 5. KEK rotasiyası

Fayllar envelope encryption ilə şifrəlidir: hər obyektin öz DEK-i var, DEK isə aktiv KEK ilə
"wrap" olunur (Bölmə 2.13). Rotasiya:

1. Yeni açar yaradılır və OpenBao-ya əlavə edilir:

   ```bash
   bao kv patch neoprint/backend/crypto NEOPRINT_KEK__k2="$(head -c 32 /dev/urandom | base64)"
   ```

2. Aktiv açar dəyişdirilir:

   ```bash
   bao kv patch neoprint/backend/crypto NEOPRINT_KEK_CURRENT_ID=k2
   ```

3. Provayder **5 dəqiqə ərzində** yeni dəyəri oxuyur — restart lazım deyil. Yeni yazılar
   dərhal `k2` ilə wrap olunur.
4. Admin paneldən `KeyRewrapJob` başladılır: köhnə obyektlərin yalnız **DEK-ləri** yenidən wrap
   olunur (məzmun yenidən şifrələnmir).
5. Bütün obyektlər keçənə qədər köhnə KEK (`NEOPRINT_KEK__k1`) OpenBao-da **qalır**.
   Job tamamlandıqdan sonra silinə bilər.

---

## 6. Kiosk və mobil tətbiq

- **Kiosk OpenBao-ya qoşulmur və heç bir secret env-i istifadə etmir** (Bölmə 7.11).
  Kimlik cihazın öz yaratdığı ECDSA P-256 açar cütüdür; private açar cihazdan çıxmır.
  Bütün konfiqurasiya `GET kiosks/me/config` ilə backend-dən gəlir.
- **Mobil tətbiq və admin web** heç bir secret saxlamır — yalnız istifadəçi token-ləri.

---

## 7. Yoxlama siyahısı (deploy-dan əvvəl)

- [ ] TLS aktivdir, `tls_disable` yoxdur
- [ ] Storage `raft`-dır
- [ ] Audit device konfiqurasiyada elan olunub və `bao audit list` onu göstərir
- [ ] `neoprint-api` və `neoprint-worker` policy-lərində **write icazəsi yoxdur**
- [ ] Root token ləğv edilib
- [ ] Unseal açarları paylanıb (3/5) və ya auto-unseal qurulub
- [ ] secret_id CIDR bağlı və istifadə sayı limitlidir
- [ ] Snapshot backup planlanıb və bərpa bir dəfə sınaqdan keçirilib
- [ ] `.openbao/` qovluğu production maşınında **yoxdur**
