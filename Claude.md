\# NeoPrint — Claude Code qaydaları



\## Mənbə

\- Layihənin yeganə spesifikasiyası: `docs/NeoPrint-PLAN.md`. Hər sessiyanın əvvəlində ən azı Bölmə 0 (təlimat), 2.0 (kitabxana siyasəti), 12 (icra planı) və cari addıma aid bölmələri oxu.

\- Plana zidd iş görmə. Planda olmayan kiçik detal üçün ən sadə və təhlükəsiz variantı seç, `docs/DECISIONS.md`-ə bir sətirlə yaz (tarix, addım, qərar, səbəb) və davam et.

\- Plan açıq şəkildə səhv və ya ziddiyyətlidirsə dayan və izah et; özbaşına dəyişmə.



\## İş qaydası

\- Yalnız cari addım üzərində işlə. Faza 2 və Faza 3 (Addım 8–10) istifadəçi açıq göstəriş verməyincə başlanmır.

\- Addımı kiçik, məntiqi hissələrə böl; hər hissə üçün ayrıca commit (Conventional Commits: `feat(backend): ...`, `feat(kiosk): ...`, `feat(mobile): ...`, `feat(admin): ...`, `feat(operator): ...`, `fix:`, `test:`, `docs:`, `chore:`).

\- Kod, identifikatorlar, commit mesajları — İngilis dilində. UI mətnləri lokalizasiya fayllarında (az — default, en, ru).

\- Testləri kodla eyni vaxtda yaz; test yazılmayan funksionallıq "bitmiş" sayılmır.



\## Kitabxanalar (Bölmə 2.0 — məcburi)

\- Yalnız Bölmə 2.0-dakı təsdiqlənmiş siyahıdan paket əlavə et. Versiyaları pinlə.

\- MediatR 12.x, MassTransit 8.x, FluentAssertions 7.x — major versiyanı heç vaxt yüksəltmə.

\- Yeni asılılıq lazım olarsa: əvvəl öz kodunla həll etməyə çalış; mümkün deyilsə dayan və istifadəçidən təsdiq istə (ad, lisenziya, səbəb).

\- Hər asılılığı `docs/THIRD\_PARTY.md`-ə yaz.



\## Təhlükəsizlik

\- Heç bir real secret repo-ya yazılmır. Backend secret-ləri yalnız OpenBao-dan (Bölmə 2.13). `.env\*`, `.openbao/` — `.gitignore`-da.

\- Kiosk tətbiqi universal build-dir: kiosk-a aid kimlik, secret və ya konfiqurasiya build-ə, appsettings-ə və ya env-ə qoyulmur (Bölmə 7.11).

\- Real bank və fiskal API yoxdur — yalnız interface + Mock.



\## Addımın sonu (hər addımda məcburi)

1\. Addımın bütün "Definition of Done" bəndlərini bir-bir yoxla.

2\. Hamısı keçməlidir: build (backend, desktop — Windows və Linux, mobile, admin), bütün testlər, lint, `tools/license-check`.

3\. `docs/` sənədlərini yenilə (README, API, DECISIONS və s.).

4\. Commit → tag (Bölmə 13 cədvəli) → `git push origin main --tags`.

5\. İstifadəçiyə hesabat ver:

&#x20;  - görülən işlər (qısa);

&#x20;  - DoD cədvəli: bənd | nəticə | necə yoxlanıldı;

&#x20;  - `DECISIONS.md`-ə əlavə olunan qərarlar;

&#x20;  - əl ilə yoxlanmalı olanlar (məs. real printer, fiziki kiosk);

&#x20;  - məlum məhdudiyyətlər / risklər.

6\. Növbəti addıma keçmə — istifadəçinin təsdiqini gözlə.



\## Dayanmaq lazım olan hallar (yalnız bunlar)

\- Lazımi alət və ya giriş yoxdur (Docker, SDK, GitHub icazəsi və s.) — nəyin lazım olduğunu yaz.

\- Plana zidd və ya planla həll olunmayan ciddi arxitektura qərarı.

\- Bölmə 2.0-da olmayan kitabxana tələb olunur.

\- Testlər və ya build 3 cəhddən sonra da keçmir — səbəbi və variantları yaz.

