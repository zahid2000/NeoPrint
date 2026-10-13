# NeoPrint — xırda UI problemləri

Layihə sahibi gözünə dəyən xırda UI problemini bura **bir sətirlə** yazır:
mətn sığmır, üst-üstə düşür, xam tərcümə açarı görünür, boş / xəta vəziyyəti
anlaşılmır, rəng və ya boşluq yerində deyil və s. Ayrıca tapşırıq yazmağa ehtiyac
yoxdur.

## Qaydalar

- Bir sətir — bir problem: **tətbiq | ekran | nə görünür | ekran görüntüsü yolu (varsa)**.
  Tətbiq: `admin`, `kiosk`, `mobil`, `operator`. Dil və ya ekran ölçüsü vacibdirsə
  "nə görünür" sütununda yazılır (məs. "ru, 360 dp").
- Ekran görüntüsü varsa `docs/screenshots/ui-bugs/` altına qoyulur və yolu yazılır;
  yoxdursa sütun boş qalır.
- Claude **hər M-dən sonra** bu faylı oxuyur, "Açıq" sətirləri düzəldir və statusu
  "Həll olundu (`commit`)" edir. Düzəliş böyükdürsə (yeni komponent, bir neçə ekran)
  `docs/TASKS.md`-də T-xxx açılır və nömrəsi status sütununa yazılır.
- Sətir silinmir; həll olunanlar aşağıdakı "Həll olunanlar" cədvəlinə köçürülür.
- Problem xırda deyilsə (axın işləmir, məlumat səhvdir) — bura yox, birbaşa
  tapşırıq kimi yazın.

## Açıq

| # | Tarix | Tətbiq | Ekran | Nə görünür | Ekran görüntüsü | Status |
|---|---|---|---|---|---|---|

## Həll olunanlar

| # | Tarix | Tətbiq | Ekran | Nə görünürdü | Status |
|---|---|---|---|---|---|
| 1 | 2026-10-13 | admin | Yeni kiosk (`/kiosks/new`) | Forma açılan kimi, heç nə yazılmamış üç qırmızı xəta görünürdü ("Kiosk adı tələb olunur", "Ünvan tələb olunur", "Kioskun yerini xəritədə seçin…"). Tapıldı: T-257 gəzintisi (`admin/e2e/walk.mjs`) | Həll olundu (`aba8202`): boş formada heç nə deyilmir, ilk yazılan sahədən sonra çatışmayanlar sadalanır; test `kiosk-editor.component.spec.ts` |
| 2 | 2026-10-13 | admin | Yeni kiosk (`/kiosks/new`) | "Kod" sahəsinin iki sətirlik izahının ikinci sətrini ("…avtomatik verilir") altındakı "Ünvan" sahəsi örtürdü (1366 px) | Həll olundu (`aba8202`): sahə izaha yer ayırır (`subscriptSizing="dynamic"`) |
| 4 | 2026-10-13 | admin | Kiosklar (siyahı, 1366 px) | Kiosk kodu defisdən iki sətrə bölünürdü ("KSK-" / "9002"). Tapıldı: T-256 ekran görüntüsü | Həll olundu (bu sətirlə eyni commit): kod bir sözdür — `.neo-nowrap` |
| 3 | 2026-10-13 | admin | İdarə paneli → Açıq incident-lər; Fiskal çeklər | Uğursuz qaytarma incident-inin mətni "Refund-lar səhifəsində təkrarlayın" deyirdi, menyuda isə səhifənin adı "Qaytarmalar"dır | Həll olundu (`aba8202`): mətn "Qaytarmalar" səhifəsini deyir (əvvəl yazılmış incident-lər açar ilə saxlanıldığı üçün onlar da yeni mətnlə göstərilir) |
