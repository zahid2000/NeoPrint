# NeoPrint — Dizayn Sistemi

> Bu sənəd üç tətbiq (kiosk, mobil, admin/operator) üçün **məcburi** vizual və UX qaydasıdır.
> Yeni ekran yazarkən burada verilmiş token-lərdən istifadə et; hardcoded rəng, ölçü və ya şrift yazma.
> Ziddiyyət olarsa `NeoPrint-PLAN.md` funksional tələb üzrə, bu sənəd isə görünüş üzrə üstündür.

## 0. Prinsiplər

1. **Bir ailə, üç kontekst.** Kiosk, mobil və admin eyni rəng, şrift və ikon dilini işlədir; layout hər kontekstə uyğun dəyişir. İstifadəçi üçünü də görəndə eyni sistemin hissəsi olduğunu anlamalıdır.
2. **Hər ekranın bir əsas işi var.** Ekranda yalnız bir əsas (primary) düymə olur; qalanları ikinci dərəcəlidir.
3. **Geniş auditoriya.** Gənc, orta və yaşlı nəsil istifadə edir. Jargon yoxdur, gizli jest yoxdur, hər əsas funksiya birbaşa görünür.
4. **Heç bir hərəkət sirr deyil.** Sistem nə etdiyini yazır: yüklənir, ödəniş gözlənilir, çap olunur, xəta oldu və nə etmək lazımdır.
5. **Geri dönüş həmişə var.** Hər addımda "Geri" və ya "Ləğv et" görünür; dağıdıcı əməliyyat təsdiq istəyir.
6. **Rəng tək başına məna daşımır.** Status həmişə rəng + ikon + mətn ilə verilir (rəng görməyənlər və solğun ekranlar üçün).
7. **Boşluq da dizayndır.** Sıxışdırılmış ekran yaşlı istifadəçi üçün oxunmur; admin panelində belə sıxlıq nəfəs alan olmalıdır.

---

## 1. Rənglər

Token adları bütün platformalarda eynidir (CSS custom property, Dart `ThemeExtension`, Avalonia `DynamicResource`).

### Brend və neytral şkala

| Token | Light | Dark | İstifadə |
|---|---|---|---|
| `--brand-50` | `#EEF3FF` | `#111A2E` | Çox açıq fon, seçilmiş sətir |
| `--brand-100` | `#DCE6FF` | `#16243F` | Hover fon |
| `--brand-300` | `#9DB8FF` | `#3D67C7` | Kənarlar, ikinci dərəcəli vurğu |
| `--brand-500` | `#2B5FD9` | `#7BA4FF` | **Əsas brend rəngi** (düymə, link, aktiv vəziyyət) |
| `--brand-600` | `#1F4CBF` | `#5E8FF5` | Hover |
| `--brand-700` | `#173A95` | `#4A7AE0` | Basılmış vəziyyət |
| `--neutral-0` | `#FFFFFF` | `#0F1216` | Ən alt fon |
| `--neutral-50` | `#F7F8FA` | `#151A20` | Səhifə fonu |
| `--neutral-100` | `#EDEFF3` | `#1C222A` | Kart fonu / ayırıcı fon |
| `--neutral-200` | `#DFE3E9` | `#252C36` | Kənar (border) |
| `--neutral-400` | `#9AA3B0` | `#6B7684` | Passiv mətn, placeholder |
| `--neutral-600` | `#5B6473` | `#9AA3B0` | İkinci dərəcəli mətn |
| `--neutral-800` | `#2A303A` | `#DDE2E8` | Əsas mətn |
| `--neutral-900` | `#171B21` | `#F2F4F7` | Başlıq |

### Semantik rənglər

| Token | Light | Dark | Məna |
|---|---|---|---|
| `--success-500` / `--success-bg` | `#16A34A` / `#E8F7EE` | `#4ADE80` / `#12291C` | Uğur, Online, tamamlandı |
| `--warning-500` / `--warning-bg` | `#D97706` / `#FEF3E2` | `#FBBF24` / `#2C2110` | Xəbərdarlıq, az kağız, Paused |
| `--danger-500` / `--danger-bg` | `#DC2626` / `#FDECEC` | `#F87171` / `#2E1414` | Xəta, Offline, dağıdıcı əməliyyat |
| `--info-500` / `--info-bg` | `#0284C7` / `#E6F4FB` | `#38BDF8` / `#0E2430` | Məlumat, gözləmə |
| `--focus-ring` | `#2B5FD9` | `#7BA4FF` | Klaviatura fokusu (2px outline + 2px offset) |

### Status rəngləri (kiosk, job, ödəniş)

| Status | Rəng | İkon |
|---|---|---|
| Online / Completed / Succeeded | `--success-500` | dairə içində tick |
| Paused / Warning / Pending | `--warning-500` | nida işarəsi |
| Offline / Failed / Interrupted | `--danger-500` | bağlantı kəsik / üçbucaq |
| NotConfigured / Draft / Idle | `--neutral-400` | boş dairə |
| Printing / Processing | `--info-500` | dönən halqa |

### Qaydalar
- Kontrast: mətn üçün ən azı **WCAG AA** (adi mətn 4.5:1, iri mətn 3:1).
- **Kiosk yalnız light temadadır** (ictimai yerdə işıq dəyişkəndir), amma kontrastı bir pillə artırılmış variantla: mətn `--neutral-900`, fon `--neutral-0`, əsas düymə `--brand-600`.
- Mobil və admin **light + dark**: default sistem seçimi, əl ilə dəyişmə profil/tənzimləmələrdə. Seçim cihazda saxlanılır.

---

## 2. Tipoqrafiya

- Şrift: **Inter** (SIL OFL 1.1 — pulsuz, Azərbaycan hərfləri tam). Fallback: `system-ui, Segoe UI, Roboto, sans-serif`.
- Rəqəmlər cədvəllərdə və qiymətlərdə `font-variant-numeric: tabular-nums`.
- Şrift qalınlığı: 400 (mətn), 500 (vurğu), 600 (başlıq). 300 və nazik variantlar istifadə olunmur.

| Rol | Mobil / Admin | Kiosk (1920×1080) |
|---|---|---|
| Display (ekran başlığı) | 28 / 34 px, 600 | 56 px, 600 |
| H1 | 24 px, 600 | 44 px, 600 |
| H2 | 20 px, 600 | 34 px, 600 |
| Body (əsas mətn) | 16 px, 400, sətir hündürlüyü 1.5 | 26 px, 400, 1.5 |
| Body small | 14 px | 22 px |
| Caption / köməkçi | 12 px | 20 px |
| Düymə mətni | 16 px, 500 | 30 px, 600 |

Minimum: mobil və admin-də heç bir oxunmalı mətn 12 px-dən, kioskda 20 px-dən kiçik olmur.

---

## 3. Ölçü, radius, kölgə, hərəkət

- **Boşluq şkalası (4 əsaslı):** 4, 8, 12, 16, 24, 32, 48, 64. Kioskda bir pillə yuxarı (minimum 16).
- **Radius:** `--radius-sm 8px`, `--radius-md 12px`, `--radius-lg 16px`, `--radius-full 999px`. Kioskda: 16 / 20 / 28.
- **Kölgə:** `--shadow-1` (kart: `0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.08)`), `--shadow-2` (açılan menyu), `--shadow-3` (dialoq). Dark temada kölgə əvəzinə kənar (`--neutral-200`) istifadə olunur.
- **Toxunma sahəsi:** mobil ≥ 48×48 px, kiosk ≥ 64×64 px, admin ≥ 36×36 px (siçan).
- **Hərəkət:** 150 ms (kiçik), 250 ms (keçid), `ease-out`. Dekorativ animasiya yoxdur. Sistemdə "azaldılmış hərəkət" seçimi varsa animasiyalar söndürülür.
- **İkonlar:** Material Symbols Rounded (Apache-2.0). Ölçülər: mobil/admin 20–24 px, kiosk 40–48 px. Ayrı-ayrı ikon dəstləri qarışdırılmır.

---

## 4. Komponent qaydaları

**Düymələr.** Üç növ: `primary` (dolu, brend rəngi), `secondary` (kənarlı), `ghost` (yalnız mətn). Dağıdıcı əməliyyat üçün `danger` variantı. Ekranda bir primary. Yüklənmə zamanı düymə deaktiv olur və içində spinner göstərilir, mətn dəyişmir. Kioskda düymə hündürlüyü ≥ 72 px.

**Inputlar.** Yuxarıda etiket (placeholder etiket əvəzi deyil), altında köməkçi mətn. Xəta: qırmızı kənar + altında izah + ikon. Xəta yalnız sahə tərk ediləndə və ya göndərmə cəhdində göstərilir, hər hərfdə yox. Kioskda hər input fokuslananda ekran klaviaturası açılır.

**Kartlar.** `--neutral-0` fon, `--neutral-200` kənar, `--radius-md`, daxili boşluq 16 (kiosk 24).

**Cədvəllər (admin).** Sətir hündürlüyü 48 px, başlıq sabit (sticky), sıralama və filtr yuxarıda, pagination aşağıda. Status sütunu chip ilə. Sətirə klik → detal. Boş nəticə üçün boş vəziyyət bloku.

**Status chip.** Fon semantik `bg`, mətn semantik `500`, sol tərəfdə ikon, radius `full`, hündürlük 24 px (kiosk 40).

**Dialoqlar.** Başlıq (nə baş verir) + izah (nəticəsi nədir) + iki düymə (təsdiq sağda). Dağıdıcı əməliyyatda təsdiq düyməsi `danger` və səbəb sahəsi məcburidir.

**Toast / bildiriş.** Sağ yuxarı (admin), aşağı (mobil). Uğur 3 san, xəta əl ilə bağlanana qədər. Kritik incident-də səs + toast.

**Boş vəziyyət (empty state).** İkon + bir cümlə izah + əsas hərəkət düyməsi. "Məlumat yoxdur" tək başına yazılmır.

**Yüklənmə.** Siyahı və kartlar üçün skeleton, tam ekran spinner yalnız ilk açılışda. 10 saniyədən uzun əməliyyatda mərhələ mətni göstərilir.

**Xəritə (admin).** Leaflet + OpenStreetMap, sağ aşağıda atribusiya (`© OpenStreetMap`) həmişə görünür. Marker status tonunun rəngində dairədir (rəng token-dən oxunur) və yanında həmişə mətn olur: xəritənin altında status legendası (rəng + ikon + mətn) və marker-ə klikdə ad + kod + status chip. Xəritə siyahının alternatividir, onu əvəz etmir — eyni filtrlər hər ikisinə tətbiq olunur. Nöqtə seçimi (kiosk forması) klik ilə olur, seçilən koordinat input sahələrində də görünür və əl ilə yazıla bilir.

**Uzun əməliyyatın proqresi (admin).** Progress bar + faiz + "X / Y" sayğac (obyekt və ölçü) + sürət + təxmini qalan vaxt + mərhələ mətni ("Kopyalanır", "Yoxlanılır"). Yanında canlı göstərici və son yenilənmə vaxtı. İdarəetmə düymələri (Dayandır / Davam et / Ləğv et) kartın altında; ləğv təsdiq istəyir. Bitəndə kart nəticəni yazır (uğur / xəta + nə etmək olar).

**Xəta vəziyyəti.** Hər xəta üç şeyi deyir: nə baş verdi, nəyə təsir etdi, indi nə etmək olar. Texniki detal (stack trace, kod) istifadəçiyə göstərilmir; admin panelində detal "Ətraflı" altında gizlədilir və `CorrelationId` kopyalana bilir.

---

## 5. Tətbiqlərə görə layout

### 5.1 Kiosk (21", 1920×1080, landşaft, toxunma)
- Sabit karkas: yuxarıda header (loqo, kiosk adı, dil, saat), sağda queue paneli (280 px), aşağıda footer (kiosk QR + kod, qaynar xətt). Mərkəzdə dəyişən məzmun.
- Məzmun sahəsində eyni anda ≤ 7 seçim elementi. Uzun siyahılar səhifələnir, iri "Növbəti/Əvvəlki" düymələri ilə.
- Hər ekranda: başlıq (nə etməli), izah bir cümlə, əsas düymə sağ aşağıda, "Geri" sol aşağıda.
- Addım göstəricisi: Fayl → Baxış → Parametrlər → Ödəniş → Hazır.
- Geri sayımlar iri rəqəmlə və mətnlə ("Ödəniş üçün 4:32 vaxtınız var").
- Xəta və dayanma ekranları tam ekran, qısa mətn, böyük ikon, lazım olduqda QR (refund statusu, məxfilik siyasəti).
- Dil seçimi hər ekrandan əlçatandır, seçim 90 saniyə sonra default dilə qayıdır.

### 5.2 Mobil (Flutter)
- Aşağıda 5 bölməli naviqasiya. Ekran başlıqları böyük, kontent kartlarla.
- Ana səhifədə birinci kart: balans + əsas hərəkət ("Çap et"). Aktiv job varsa onun statusu ən yuxarıda.
- Formalar tək sütun, sahə başına bir sual; uzun formalar addımlara bölünür.
- Pul, tarix və status hər yerdə eyni formatda.
- Boş vəziyyətlər hər siyahı üçün (fayl yoxdur, qəbz yoxdur, planlanmış çap yoxdur).
- Dark tema tam dəstəklənir; şəkil və QR blokları dark temada ağ fon üzərində göstərilir (skan oluna bilməsi üçün).

### 5.3 Admin panel (Angular Material)
- Sol sidebar (icazəyə görə menyu, qruplaşdırılmış), yuxarıda axtarış + dil + bildiriş zəngi + profil.
- Səhifə şablonu: başlıq + qısa izah → filtr paneli → cədvəl → pagination. Detal səhifələri tab-larla.
- Dashboard kartları: rəqəm + izah + trend; kritik kartlar (incident, uğursuz refund) yuxarıda və semantik rənglə.
- Real-time yenilənən sahələrdə "canlı" göstəricisi və son yenilənmə vaxtı.

### 5.4 Operator paneli (mobil brauzer / PWA)
- Tək sütun, iri kartlar, baş barmaqla çatan zonada əsas düymələr.
- Hər kiosk kartı: status rəngi + kağız/toner göstəricisi + bir əsas hərəkət ("Kağız doldur").
- Ən çox 2 toxunuşla əsas əməliyyat tamamlanır.
- Zəif internetdə keşdən göstərilən məlumat "Son yenilənmə: HH:mm" etiketi ilə işarələnir.

---

## 6. Məzmun və format qaydaları

- **Pul:** `12,50 ₼` (vergül onluq ayırıcı, min ayırıcısı boşluq: `1 240,00 ₼`).
- **Tarix:** `dd.MM.yyyy`, **saat:** `HH:mm` (24 saat). Bu gün/dünən üçün nisbi yazı ("Bu gün 14:20").
- **Səhifə/vərəq:** "12 səhifə", "6 vərəq" — qısaltma işlədilmir.
- **Mətn tonu:** sadə, birbaşa, "siz" formasında. Texniki termin əvəzinə gündəlik söz ("əlaqə yoxdur" ≠ "connection timeout").
- **Xəta nümunəsi:** «Ödəniş tamamlanmadı. Məbləğ kartınızdan çıxılmayıb. Yenidən cəhd edin və ya başqa ödəniş üsulu seçin.»
- **Lokalizasiya:** az (default), en, ru. Layout mətnin 40% uzanmasına dözməlidir; düymələrdə mətn kəsilmir, sətir aşağı keçir. Heç bir mətn koda yazılmır.

---

## 7. Əlçatanlıq

- Klaviatura ilə tam naviqasiya (admin), görünən fokus halqası.
- Ekran oxuyucular üçün etiketlər (`aria-label`, Flutter `Semantics`).
- Toxunma sahələri minimumdan kiçik olmur.
- Animasiya azaldılması sistem seçiminə tabedir.
- Kioskda mətn ölçüsünü artırmaq üçün "A+" düyməsi (bir pillə, 125%).

---

## 8. Tətbiq qaydası (Claude Code üçün)

- Token-lər bir mənbədən: `admin/src/styles/tokens.css`, `mobile/lib/core/theme/tokens.dart`, `desktop/src/NeoPrint.Kiosk/Styles/Tokens.axaml`. Dəyər dəyişəndə üçü də yenilənir (mənbə: bu sənəd).
- Hardcoded rəng, şrift ölçüsü və boşluq qadağandır; kod review-da bu yoxlanılır.
- Angular Material teması token-lərdən qurulur (`mat.define-theme`), komponentlər ayrıca stillə əzilmir.
- Flutter-də `ThemeData` + `ThemeExtension<NeoPrintTokens>`; widget-lərdə birbaşa `Color(0x...)` yazılmır.
- Avalonia-da `Tokens.axaml` + `ControlTheme`; inline `Background="#..."` yazılmır.
- Yeni komponent yaradılanda əvvəl bu sənədə qayda əlavə olunur, sonra kod yazılır.
- Dizayn dəyişikliyi davranışı və mövcud testləri pozmamalıdır.
