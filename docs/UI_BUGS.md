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
