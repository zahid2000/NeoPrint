# NeoPrint — Claude Code qaydaları

## Mənbələr və prioritet
1. `docs/NeoPrint-PLAN.md` — layihənin əsas spesifikasiyası.
2. `docs/ARCHITECTURE_RULES.md`
3. `docs/STATE_MACHINES.md`
4. Digər qayda sənədləri: `IDEMPOTENCY.md`, `PAYMENT_FLOWS.md`, `FAILURE_RECOVERY.md`, `OFFLINE_BEHAVIOR.md`, `SECURITY_THREAT_MODEL.md`, `DOMAIN_RULES.md`, `DATA_RETENTION.md`, `OBSERVABILITY.md`, `DESIGN_SYSTEM.md`
5. `docs/CLAUDE_CODE_EXECUTION_RULES.md`
6. Mövcud repo kodu

Ziddiyyət olarsa plan üstündür. Uyğunsuzluğu göstər, təhlükəsiz variantı seç, `docs/DECISIONS.md`-ə yaz. Arxitektura qərarı üçün `docs/adr/TEMPLATE.md` formatında ADR yarat.

**Hər sessiyanın birinci işi:** `CLAUDE.md` → `docs/TASKS.md` → `git status` / `git log`. `Açıq` və `İcrada` tapşırıqlar davam etdirilir.

Hər sessiyanın əvvəlində oxu: plandakı təlimat bölməsi, Bölmə 2.0 (kitabxana siyasəti), Bölmə 12 (icra planı) və cari addıma aid bölmələr + `docs/README.md`.

## Tapşırıq reyestri (`docs/TASKS.md`)
- Layihə sahibinin hər yeni tapşırığı işə başlamazdan **əvvəl** `docs/TASKS.md`-yə yazılır və commit olunur.
- Status dəyişəndə dərhal yenilənir; `Tamamlandı` yalnız sübutla (commit / test / fayl) və real stack-də yoxlandıqdan sonra.
- Addım yalnız ona aid bütün tapşırıqlar `Tamamlandı` (və ya əsaslandırılmış `Təxirə salındı`) olanda bağlanır.
- Addım hesabatında plan DoD-undan əlavə həmin addım zamanı verilən bütün tapşırıqlar ayrıca yoxlanılır.
- Tapşırıq heç vaxt siyahıdan silinmir.

## Lokal mühit dəyişiklikləri
- Compose-dan servis çıxarılanda, adı dəyişəndə və ya seed/konfiqurasiya strukturu dəyişəndə `docs/DEV_ENVIRONMENT.md`-də keçid addımları yazılır, addım hesabatında isə "Lokal mühitinizdə etməli olduğunuz" bölməsi olur.

## İş qaydası
- Yalnız cari addım. Faza 2 və 3 (Addım 8–10) istifadəçi göstərişi olmadan başlanmır; gələcək işləri "hazır olsun" deyə əvvəlcədən yazma.
- MVP scope-unu daraltma, plandakı qərarları özbaşına dəyişmə.
- Addımı kiçik hissələrə böl; hər hissə: repo analizi → qısa dəyişiklik planı → kod → test → build → `git diff` yoxlaması → commit.
- Conventional Commits: `feat(backend|kiosk|mobile|admin|operator): ...`, `fix:`, `test:`, `docs:`, `chore:`.
- Kod və commit İngilis dilində; UI mətnləri lokalizasiya fayllarında (az default, en, ru).
- **Ünsiyyət dili Azərbaycan dilidir:** bütün hesabatlar, suallar, izahlar, DoD cədvəlləri, tapılan baqların təsviri və risk xəbərdarlıqları Azərbaycan dilində yazılır. Yalnız kod, identifikatorlar, kod şərhləri, commit mesajları və sənəd fayllarının texniki adları (`docs/API.md`, `docs/DECISIONS.md`) İngilis dilində qalır. `docs/` altındaki sənədlərin məzmunu da Azərbaycan dilindədir.
- Task üçün lazım olmayan faylı dəyişmə, böyük refactor etmə.

## Arxitektura
- Modular monolith: Domain (texnologiyadan asılı deyil) → Application (use-case) → Infrastructure → API/Host. Controller-də business logic yoxdur.
- MongoDB = source of truth. Redis = keş/queue/lock. RabbitMQ = asinxron çatdırılma. Hangfire = background icra. Redis itsə state Mongo-dan bərpa olunur.
- Modul başqa modulun kolleksiyasını birbaşa dəyişmir. External provider həmişə interface arxasında.
- Biznes state dəyişiklikləri yalnız explicit domain transition metodları ilə (`job.Complete(...)`), raw property assignment ilə yox.

## Məcburi kross-kəsən qaydalar (ilk addımdan)
- **Idempotency:** ödəniş, callback/webhook, refund, job yaratma/ləğv, wallet, fiskal, voucher, referral, aktivləşdirmə, bütün consumer-lər. `Idempotency-Key` + `provider_events` unikal indeksi. Double charge / double refund / double wallet credit / double fiscal / double print qadağandır.
- **CorrelationId** bütün zəncir boyunca; structured JSON log (`docs/OBSERVABILITY.md` sahələri).
- **Log-a yazılmır:** token, şifrə, açar, fayl məzmunu, ödəniş secret-ləri, artıq PII.
- **Money** value object + mərkəzi yuvarlaqlaşdırma; qiymət snapshot-u sonradan yenidən hesablanmır.
- **Wallet ledger** həqiqət mənbəyidir, `Balance` yalnız materialized dəyərdir.
- Background job: retry-safe, idempotent, observable, cancellation-aware.

## Kitabxanalar (Plan Bölmə 2.0)
- Yalnız təsdiqlənmiş siyahıdan, versiyalar pinli. MediatR 12.x, MassTransit 8.x, FluentAssertions 7.x — major yüksəltmə yoxdur.
- Yeni paket lazımdırsa: əvvəl öz kodunla həll et; mümkün deyilsə dayan və təsdiq istə (ad, lisenziya, səbəb). Hər asılılıq `docs/THIRD_PARTY.md`-də.

## Dizayn (`docs/DESIGN_SYSTEM.md` məcburidir)
- **Hardcoded rəng, şrift ölçüsü və boşluq qadağandır.** Hər dəyər token faylından gəlir: `admin/src/styles/tokens.css`, `mobile/lib/core/theme/tokens.dart`, `desktop/src/NeoPrint.Kiosk/Styles/Tokens.axaml`. Token dəyişəndə üçü də yenilənir.
- Hər yeni ekran və komponent `DESIGN_SYSTEM.md`-ə uyğun qurulur — sonradan "gözəlləşdirmə" mərhələsi yoxdur.
- Yeni komponent yaradılanda əvvəl `DESIGN_SYSTEM.md`-ə qayda əlavə olunur, sonra kod yazılır.
- Status həmişə rəng + ikon + mətn ilə verilir; ekranda bir primary düymə olur; boş siyahı üçün empty state yazılır; hər xəta nə baş verdiyini, nəyə təsir etdiyini və indi nə etmək lazım olduğunu deyir.
- Mətn koda yazılmır — lokalizasiya fayllarında (az default, en, ru).

## Təhlükəsizlik
- Real secret repo-ya yazılmır; backend secret-ləri OpenBao-dan. `.env*`, `.openbao/` gitignore-dadır.
- Kiosk untrusted edge device: secret göndərilmir, private key cihazdan çıxmır, spool şifrəlidir, universal build (kiosk-a aid heç nə build-ə qoyulmur).
- İstifadəçi faylları untrusted input: konvertasiya ayrıca prosesdə, şəbəkəsiz, timeout və resurs limiti ilə.
- Təhlükəsizlik nəzarətlərini rahatlıq üçün zəiflətmə; belə dəyişikliklər test tələb edir.

## Task "done" sayılır
Kod compile olur; testlər keçir; error handling var; idempotency/retry nəzərə alınıb; təhlükəsizlik qaydaları pozulmayıb; `git diff` təmiz və scope daxilindədir.

## Addımın sonu (məcburi)
1. Addımın bütün "Definition of Done" bəndlərini bir-bir yoxla.
2. Keçməlidir: build (backend, desktop win+linux, mobile, admin), bütün testlər, lint, `tools/license-check`.
3. Sənədləri yenilə (README, API, DECISIONS, THIRD_PARTY, ADR-lər).
4. Commit → tag (Plan Bölmə 13) → `git push origin main --tags`.
5. Hesabat ver: görülən işlər; DoD cədvəli (bənd | nəticə | necə yoxlanıldı); addım zamanı verilmiş əlavə tapşırıqların cədvəli (`docs/TASKS.md`-dən); yeni qərarlar; əl ilə yoxlanmalı olanlar; "Lokal mühitinizdə etməli olduğunuz" (varsa); risklər.
6. Növbəti addıma keçmə — təsdiq gözlə.

## Dayan və soruş
- Alət/giriş yoxdur (Docker, SDK, GitHub icazəsi).
- Planla ziddiyyət və ya planda olmayan ciddi arxitektura qərarı.
- Siyahıda olmayan kitabxana tələb olunur.
- Build/test 3 cəhddən sonra da keçmir (səbəb + variantlar).
Root cause tapılmadan failure-i suppress etmə.
