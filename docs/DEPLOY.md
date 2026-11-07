# Staging və production — runbook

Bu sənəd staging və production quraşdırmasında **təhlükəsizliyə təsir edən** parametrləri və onların yoxlanmasını
toplayır. Test serveri: `docs/TEST_SERVER.md`. Staging compose və production M20–M21-də (`docs/STEP_7B_PLAN.md`)
qurulacaq — bu sənəd onda tamamlanır. Hələlik yazılanlar: etibarlı proksi və sorğu limitləri (T-306, T-308, T-309).

## 1. Etibarlı proksi (`X-Forwarded-For`) — T-306, T-308

**Niyə vacibdir.** API sorğu limitlərini (anonim çağıran üçün dəqiqədə 120; giriş üçün dəqiqədə 10; cihaz qeydiyyatı
üçün saatda 10) çağıranın IP-sinə görə sayır. API proksinin arxasındadır, ona görə əlaqə həmişə proksidən gəlir. Real
IP proksinin göndərdiyi `X-Forwarded-For` başlığındadır. Bu başlığa **yalnız etibarlı proksidən** gələndə inanmaq olar.
Əks halda istənilən müştəri başlığı özü yazar və limitdən yayınar.

**Topologiya (test serveri, staging, production eyni):**

```
internet → Caddy (TLS) → admin nginx → API        (panel, /api, /hubs)
internet → Caddy (TLS) → API                      (/refund/* — ictimai refund səhifəsi)
```

- **Caddy** müştərinin göndərdiyi `X-Forwarded-For`-u atır (`trusted_proxies` təyin edilmədikdə), yerinə müştərinin real
  IP-sini yazır. **Caddy-də `trusted_proxies` təyin etməyin** — Caddy internetin qarşısındakı ilk proksidir.
- **nginx** (`admin/nginx.conf`) Caddy-nin dediyinə öz qonşusunun (Caddy-nin) ünvanını əlavə edir
  (`$proxy_add_x_forwarded_for`).
- **API** başlığı yalnız `ForwardedHeaders:KnownNetworks`-dakı şəbəkədən gələn əlaqədə oxuyur, sağdan ən çox
  `ForwardedHeaders:ForwardLimit` (default **2**: nginx və Caddy) addım geri gedir. `X-Real-IP` oxunmur.

**Konfiqurasiya (API-nin mühit dəyişənləri):**

| Dəyişən | Dəyər | İzah |
|---|---|---|
| `ForwardedHeaders__KnownNetworks` | `172.16.0.0/12,192.168.0.0/16` | Docker-in şəxsi şəbəkələri, compose-dakı Caddy və nginx burada olur. Production-da compose şəbəkəsinin subnet-i dəqiq bilinirsə, onu yazın (məs. `172.25.0.0/16`) |
| `ForwardedHeaders__ForwardLimit` | `2` | Caddy → nginx → API. Proksi zənciri dəyişsə (məs. önə CDN gəlsə) bu rəqəm və şəbəkələr birlikdə dəyişir |

**Təhlükəsiz davranış:**

- Dəyişən **boşdursa və ya yoxdursa** — yalnız loopback etibarlıdır, başqa heç kimin başlığına inanılmır. Bu
  təhlükəsizdir, amma hamı proksinin IP-si ilə sayılır: limitlər ortaq olur. **Production-da dəyişən mütləq olmalıdır.**
- Dəyər **səhv yazılıbsa** (məs. `172.16.0.0` — prefiks yoxdur) — API **başlamır** və nə üçün olduğunu loga yazır.
- **API-nin portu internetə açıq olmamalıdır** (test serverində `compose.override.yml`: `ports: !reset []`). Port açıq
  olsa, etibarlı şəbəkədəki hər hansı maşın başlıq yaza bilər.

**Yoxlama (hər deploy-dan sonra, ən azı ilk dəfə):**

1. **Saxta başlığa inanılmır** — öz kompüterinizdən (PowerShell və ya bash):
   ```bash
   for i in $(seq 1 130); do curl -s -o /dev/null -w '%{http_code}\n' -H "X-Forwarded-For: 203.0.113.$i" https://<host>/api/map/config; done | sort | uniq -c
   ```
   Gözlənilən: ~120 dəfə `200`, sonra `429` — hamısı sizin real IP-nizə sayılır (başlıq nəzərə alınmayıb).
2. **İki real IP ayrı sayılır** — GitHub-da **Actions → "Proxy check (test server)" → Run workflow**. Runner ikinci real
   IP-dir: öz kvotasını tükəndirir və bir dəqiqə rədd olunur; həmin dəqiqədə öz kompüterinizdən
   `curl -s -o /dev/null -w '%{http_code}\n' https://<host>/api/map/config` — `200` olmalıdır.
3. Test: `RateLimitTests` (`Only_a_configured_proxy_may_say_who_the_caller_is`,
   `Without_a_configured_proxy_nobody_s_header_is_believed`, `A_misspelt_proxy_network_stops_the_start`,
   `An_anonymous_caller_cannot_choose_the_address_it_is_counted_by`).

## 2. Sorğu limitləri harada saxlanılır — T-309

| Limit | Açar | Harada | Restart-da |
|---|---|---|---|
| Qlobal (dəqiqədə 120) | daxil olmuş: token növü + `sub`; anonim: IP | API prosesinin yaddaşı | sıfırlanır |
| Giriş (`auth`, dəqiqədə 10), refresh (dəqiqədə 60), anonim müraciət (saatda 10) | IP | API prosesinin yaddaşı | sıfırlanır |
| Kiosk qeydiyyatı (saatda 10 / token-lə saatda 5) | etibarlı enrollment token-i, yoxdursa IP | **Redis** (`neoprint:ratelimit:*`) | **qalır** |
| Kiosk girişi (`challenge`, `token`; dəqiqədə 10) | cihaz + IP (məlum cihaz), yoxdursa IP | **Redis** | **qalır** |

- Yaddaşdakı limitlər hər API nüsxəsinin özünə aiddir. Bir neçə nüsxə olsa (production-da scale), qlobal limit
  nüsxə sayına vurulur. Lazım olsa onlar da Redis-ə köçürülür (ayrıca tapşırıq).
- Redis əlçatmaz olanda kiosk limitləri müvəqqəti olaraq yaddaşda sayılır və log bunu xəbərdarlıq kimi yazır.
- Konfiqurasiya: `RateLimits__*` (`docs/API.md` — "Rate limit"). Production-da `RateLimits__Disabled` **heç vaxt**
  təyin edilmir.

## 3. Kiosk buraxılışları: hansı açar dəsti harada — T-316

| Mühit | Kiosk build-i | Paketlər |
|---|---|---|
| Development stack (`tools/dev-kiosk`, Linux konteyner kiosku) | `-p:ReleaseKeySet=staging` (skriptlər özü verir) | `*-staging` artefaktları, `release-sign sign-staging` |
| Test serveri (`docs/TEST_SERVER.md`) — ona qoşulan bütün kiosklar | staging | `*-staging` |
| Staging (M20) | staging | `*-staging` |
| **Production / pilot kioskları** | **production** (parametr verilməsə də budur) | `*-production`, yalnız oflayn imza (`docs/RELEASE_SIGNING.md` §4) |

- Production build staging açarını **daşımır** — CI-da `release-sign check-build` hər push-da və hər buraxılış paketində yoxlayır. Ona görə test serverindəki staging paketi pilot kioskuna yüklənsə belə `UnknownKey` ilə rədd olunur.
- Admin panelə (M16) paket yüklənəndə manifestdəki `keySet` görünür; production serverinə staging paketi yükləmək mənasızdır — kiosklar onu qəbul etmir.
- Pilot kiosku golden image-dən (M19) qurulmazdan əvvəl production açarları (T-315) `ReleaseKeys.production.cs`-də olmalıdır.

## 3a. Staging (M20, T-335)

Bütün stack **Production** rejimində, development-in yanında, eyni maşında ikinci compose layihəsi kimi
(`neoprint-staging`). Ayrıca compose faylı yoxdur: development-in `docker-compose.yml`-i
`deploy/staging/staging.env`-dəki `NEOPRINT_*` dəyərləri ilə işləyir — ikisi bir-birindən ayrıla bilməz.
Dəyərlər verilməyəndə hər şey development-dəki kimidir (`docker compose config` bayt-bayt eynidir).

| | Development | Staging |
|---|---|---|
| Rejim | `Development` | **`Production`** — məcburi secret yoxdursa API başlamır; Development seed-i və dev endpoint-ləri yoxdur |
| OpenBao | `neoprint-openbao`, `.openbao/` | **öz** `neoprint-staging-openbao`, `.openbao-staging/` (gitignore) |
| Seed faylı | `.env.openbao-seed` | `.env.staging-seed` — **təsadüfi** parollar, skript özü yaradır (gitignore, `600`) |
| Baza | dev məlumatı | **təmiz**: yalnız production seed-i — rollar, tənzimləmələr, hüquqi sənədlər, standart qiymət profili, ilk SuperAdmin (T-300) |
| Panel / API | `:8080` / `:5080` | `:28080` / `:25080` (digər portlar `staging.env`-də) |
| Kiosk paketləri | staging | staging (§3) |

```bash
bash tools/staging/staging.sh up            # ilk dəfə seed faylı; build, başlat, sağlam olana qədər gözlə
bash tools/staging/staging.sh check         # baza təmizdir: seed və başqa heç nə; Production; dev endpoint-ləri 404
bash tools/staging/staging.sh t169          # T-169: məcburi açar silinir → API başlamır və açarın adını deyir → geri
bash tools/staging/staging.sh down          # dayandır, məlumat qalır
bash tools/staging/staging.sh reset --yes   # məlumat, seed faylı və OpenBao qovluğu ilə birlikdə sil
```

İlk giriş: `.env.staging-seed`-dəki `NEOPRINT_SEED_ADMIN_EMAIL` / `NEOPRINT_SEED_ADMIN_PASSWORD` (skript parolu çap
etmir). Docker yenidən başlayanda OpenBao möhürlənir — `docker compose -p neoprint-staging --env-file
deploy/staging/staging.env up -d openbao-init` (development-də olduğu kimi).

**T-169 sübutu (2026-11-07):** staging-in öz OpenBao-sunda `neoprint/backend/core`-dan `NEOPRINT_JWT_SIGNING_KEY` silindi,
API yenidən başladıldı → sağlam olmadı, log: `MissingSecretsException: The following mandatory secrets are missing from
OpenBao: NEOPRINT_JWT_SIGNING_KEY` — açarın dəyəri log-da yoxdur; `kv rollback` ilə açar qaytarıldı → API yenidən
sağlam. Development-in paylaşılan OpenBao-suna toxunulmadı.

## 4. Hələ yazılmalı olanlar (M21)

Domen və TLS; SMTP (SPF / DKIM / DMARC); production serverinin özü (staging-in eyni `NEOPRINT_*` dəyişənləri ilə);
ehtiyat nüsxə və bərpa; monitorinq.
