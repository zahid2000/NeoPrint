# Addım 7 — ekran görüntüləri (real stack, 2026-10-11 …)

Hamısı `docker compose up -d --build api converter` edilmiş stack-dən çəkilib.

- **Mobil tətbiq:** web build (`flutter build web --dart-define=NEOPRINT_API_URL=http://localhost:5080/`), Playwright + Chrome, 430×932, `dev-user`.
- **Converter yoxlaması:** `node tools/dev-converter/check.mjs --down` — nəticəsi `docs/TASKS.md` T-205-də.

| Qrup | Fayllar | Nə göstərir | Tapşırıq |
|---|---|---|---|
| 7A / M1 — fayl parse-ının tam izolyasiyası | `m1-01-preview-converter-down`, `m1-02-preview-after-retry` | Converter konteyneri dayandırılıb: faylın önizləməsi "Sənəd emalı xidməti hazırda işləmir… bir az sonra yenidən cəhd edin" cümləsini və "Yenidən cəhd et" düyməsini göstərir (`503 files.processingUnavailable`); converter qalxandan sonra eyni düymə ilə səhifə açılır — onu indi API yox, converter-dəki alət çəkir | T-205 (T-051) |
