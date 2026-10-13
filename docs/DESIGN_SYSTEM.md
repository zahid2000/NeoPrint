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

**Inputlar.** Admin paneldə bütün form sahələri eyni görünüşdədir (Material `outline`), mərkəzi olaraq `MAT_FORM_FIELD_DEFAULT_OPTIONS`-da təyin olunur — şablonda `appearance` yazılmır. Sahə ilə yanındakı düymə bir sətirdədirsə, hər ikisi `--control-height-lg` hündürlüyündədir. Yuxarıda etiket (placeholder etiket əvəzi deyil), altında köməkçi mətn. Xəta: qırmızı kənar + altında izah + ikon. Xəta yalnız sahə tərk ediləndə və ya göndərmə cəhdində göstərilir, hər hərfdə yox. Kioskda hər input fokuslananda ekran klaviaturası açılır.

**Tarix sahəsi (admin).** Tarix yalnız Material datepicker ilə daxil edilir; brauzerin öz `type="date"` sahəsi istifadə olunmur, çünki onun formatı brauzerin dilindən asılıdır ("mm/dd/yyyy"). Yazılan və göstərilən format hər dildə `dd.MM.yyyy`-dir (bölmə 6); format sahənin altında köməkçi mətn kimi, filtr sətrində isə (orada köməkçi mətn sətri yoxdur) placeholder kimi göstərilir: az "gg.aa.iiii", en "dd.mm.yyyy", ru "дд.мм.гггг". Təqvimdə ay və gün adları panelin dilindədir, həftə bazar ertəsindən başlayır, təqvim düymələrinin adları (növbəti ay və s.) tərcümə olunur. Dövr filtri iki ayrı sahə yox, bir "Tarix aralığı" sahəsidir (başlanğıc – son). Formatca səhv tarix sahə tərk ediləndə xəta kimi göstərilir və filtrə düşmür.

**Seçim düymələri (kiosk).** Bir-birini istisna edən seçimlər (rəngli / ağ-qara, A4 / A3, vərəqdə 1 / 2 / 4 səhifə) açılan siyahı ilə yox, yan-yana iri düymələrlə verilir (`Button.choice`). Seçilmiş düymə brend rəngli kənar + fon + qalın mətnlə fərqlənir (yalnız rənglə yox). Printerin bacarmadığı seçim (rəng, ikitərəfli, A3) deaktiv düymə kimi qalır və yanında səbəbi yazılır — bu, "Hazır olmayan funksiya" deyil, kioskun real imkanıdır: müştəri rəngli çapın niyə olmadığını görür.

**Ekran klaviaturası (kiosk).** Kioskda fiziki klaviatura yoxdur (lockdown onu bağlayır), ona görə mətn və ya rəqəm istəyən hər ekranın öz ekran klaviaturası var: rəqəm üçün 3×4 klaviatura (`Button.key`, düymə ≥ `TouchTarget`), mətn üçün seçilmiş dilin hərfləri ilə tam klaviatura (az: ə, ğ, ı, ö, ş, ü, ç; ru: kiril). Klaviatura sahənin altında daim görünür; "Sil" düyməsi həmişə var. Rəqəmdən kiçik görünən simvollar (`,`, `-`) düymədə iri və qalın yazılır ki, rəqəmlər qədər oxunsun. Düymələr bir-birinə toxunmur: hər düymənin sağında və altında `KeyGap` (16) boşluğu var; "Sil" klaviaturanın yanındadır, ekranın kənarında yox. Yazılma zamanı ekran yerindən oynamır: natamam giriş (məs. "1-") xəta deyil, tamamlanmış səhv giriş isə banner-də yox, sahənin altında (ikon + mətn) göstərilir — yuxarıda açılan banner klaviaturanı barmağın altından sürüşdürür.

**Yazma paneli (kiosk).** Tam hərf klaviaturası (10 düymə × (`TouchTarget` + `KeyGap`) ≈ 800 px) ekranın yan sütununa (`PayMethodWidth`, 560) sığmır, ona görə ekranın kənarında qısa mətn (promokod, qəbz üçün email) yazılanda klaviatura sütunun içində yox, **yazma panelində** açılır: panel axının məzmun sahəsini tam tutur (addım göstəricisi yerində qalır), aşağıdakı düymə sətri panel açıq olanda gizlənir. Panelin tərkibi yuxarıdan aşağı: başlıq (`h1`); sahə (`card`, en ≥ 560) + yanında panelin **yeganə əsas düyməsi** ("Tətbiq et" / "Göndər") və həmin sətirdə, düymənin sağında xəta (ikon + mətn) — altında yox, ki klaviatura barmağın altından sürüşməsin; klaviatura, "Sil" isə klaviaturanın sağında; ən aşağıda solda "Bağla" (secondary) — paneli bağlayır, yazılanı silmir. Panel toxunma ilə açılır (ekrandakı sahə və ya "Promokod" düyməsi) və əməliyyat uğurlu olanda özü bağlanır. Klaviatura 1920×1080 ekranda kəsilmədən, sürüşdürmədən görünür.

**Gizli admin menyusu (kiosk).** Müştəri üçün görünən heç bir element yoxdur: başlıqdakı loqoya 3 saniyə ərzində 5 toxunuş menyunu açır. Əvvəl PIN ekranı — başlıq, bir cümlə izah, PIN nöqtələrlə (rəqəmlər göstərilmir), rəqəm klaviaturası (`Button.key`), əsas düymə "Daxil ol", ikinci düymə "Ləğv et". Səhv PIN — qırmızı xəta bloku: nə oldu + neçə cəhd qalıb; 5 səhvdən sonra 5 dəqiqəlik kilid ekranı (ikon + mətn + nə vaxt açılacaq). Menyu: iri düymələr sütunu, hər əməliyyatın nəticəsi düymənin mətnindədir ("Tətbiqi yenidən başlat"); yalnız mümkün olan əməliyyat göstərilir (təhlükəsiz rejim yoxdursa "Təhlükəsiz rejimdən çıx" görünmür).

**Addım göstəricisi (kiosk).** Sifariş axınının yuxarısında: nömrəli dairə + addımın adı. Keçilmiş addım — yaşıl ✓ ikonu + ad; cari addım — brend rəngli dairə + qalın ad; gələcək addım — neytral dairə + neytral ad (status rəng + ikon/nömrə + mətnlə verilir). Addımlar: Fayl → Baxış → Parametrlər → Ödəniş → Hazır; ödəniş hələ yoxdursa (Addım 4-ə qədər) "Ödəniş" addımı göstərilmir. Ləğv ekranında göstərici yoxdur.

**Ödəniş ekranları (kiosk).** Sifariş verildikdən sonra addım göstəricisində "Ödəniş" cari addımdır.

- *Ödəniş üsulu:* iki iri seçim kartı yan-yana (`Button.pay-method`, hündürlük ≥ 240): ikon + ad ("Bank kartı və ya bank tətbiqi (QR)", "NeoPrint tətbiqi") + bir sətir izah. Kartın özü düymədir; seçim dərhal növbəti ekrana keçir, ayrıca "Davam et" yoxdur. Yuxarıda ödəniləcək məbləğ iri (`display`) yazılır; promokod tətbiq olunubsa altında "Promokod X: −Y ₼".
- *QR ilə ödəniş (bank və ya tətbiq):* solda QR (ağ fon + quiet zone, `QrBackground`/`QrQuietZone`, en ≥ 480), sağda məbləğ (`display`), bir cümlə təlimat ("Bank tətbiqinizlə QR-ı oxudun"), **geri sayım iri rəqəmlə və mətnlə** ("Ödəniş üçün 4:32 vaxtınız var"); son 60 saniyədə rəqəm `--warning-500` + xəbərdarlıq ikonu. Gözləmə göstəricisi (dönən halqa + "Ödəniş gözlənilir"). Əsas düymə yoxdur (ödəniş telefondadır); ikinci dərəcəli düymələr: "Başqa üsul" və "Ləğv et". Promokod tətbiq olunmadısa səbəbi ekranın yuxarısında `warning` blokunda (ikon + mətn) ödənişdən **əvvəl** görünür.
- *Vaxt bitdi:* warning ikonlu tam blok — nə oldu ("Ödəniş vaxtı bitdi, məbləğ tutulmayıb"), nə etmək olar; əsas düymə "Yenidən cəhd et", ikinci "Ləğv et".
- *Ödəniş uğurlu (qəbz):* yaşıl ✓ dairə + "Ödəniş uğurlu", sifariş kodu iri kod qutusunda, növbədəki yer; sağda fiskal çek QR-ı (hazırdırsa; hazır deyilsə "Fiskal çek hazırlanır" mətni + dönən halqa, boş kvadrat yox); "Qəbzi email-ə göndər" kartı — başlıq + "Email ünvanını yazın" düyməsi (secondary); toxunanda yazma paneli açılır: email sahəsi + "Göndər" + tam hərf klaviaturası (`@`, `.`, `-`, `_` düymələri ilə). Göndərildikdən sonra panel bağlanır, kartda uğur sətri görünür (ikon + "Göndərildi: a***@..."). Əsas düymə "Bitir" (panel bağlı olanda).
- *Refund arayışı (RefundInfo):* printer xətasından və ya ödənilmiş sifarişin ləğvindən sonra: info ikonlu başlıq ("Çap tamamlanmadı" / "Sifariş ləğv edildi"), məbləğ iri, hara qaytarıldığı ("X ₼ kartınıza qaytarılır. Bankınızdan asılı olaraq 1–3 iş günü çəkə bilər."), status səhifəsinin QR-ı + altında qısa izah ("Statusu telefonunuzda izləyin"). Əsas düymə "Bitir".
  - **Harada göstərilir (T-133):** müştəri həmin sifarişin qəbz ekranındadırsa (ekran 9), arayış orada göstərilir:
    - "Ödəniş uğurlu" başlığı "Çap tamamlanmadı"-ya dəyişir;
    - uğur ikonu xəbərdarlıq ikonuna dəyişir (`WarningBg` / `Warning500`);
    - növbədəki yer və "kodu saxlayın" mətni gizlənir;
    - sifariş kodunun altında refund kartı çıxır (məbləğ və hara qaytarıldığı, QR). QR `RefundQrSize` (200) ölçüsündədir, `ReceiptQrSize` deyil: kart ekrana sığmalıdır.

    Sağ sütun (fiskal çek, email) qalır. Müştəri artıq ekrandan çıxıbsa (başqa ekran və ya başqa sifariş), arayış ana ekranda 3 dəqiqə göstərilir. Başqa müştərinin sifarişi üçün arayış heç vaxt qəbz ekranına düşmür.

**Telefon qoşulub (kiosk, Addım 5).** Mobil tətbiq bu kioskda sessiya açanda (plan 9.3) başlıqda, kiosk adının yanında kiçik nişan: `info` fonunda telefon ikonu + mətn "Telefon qoşulub" (bir neçə sessiya varsa "2 telefon qoşulub"), `Info500` rəngində. Kimin qoşulduğu heç vaxt göstərilmir — ekran ictimaidir. Nişan düymə deyil, axını bloklamır və heç bir ekranı dəyişmir; son sessiya bağlananda və ya vaxtı bitəndə gizlənir (hub mesajı itsə də, bitmə vaxtı keçəndə özü gizlənir). Tətbiqdən gələn sifariş növbə panelində adi iş kimi görünür.

**Promokod sahəsi (kiosk).** Parametrlər ekranında, qiymət kartının altında: "Promokod" düyməsi (secondary) basılanda yazma paneli açılır: kod sahəsi + "Tətbiq et" (panelin əsas düyməsi), hərf-rəqəm klaviaturası (böyük hərflər, rəqəmlər, `-`) və "Sil", "Bağla"; kod qəbul olunanda panel bağlanır. Uğurlu kod qiymət kartında ayrıca sətir kimi görünür ("Promokod X −Y ₼", `--success-500` ikonu ilə) və yekun məbləğ yenilənir; uyğun olmayan kod sahənin altında səbəbi ilə (ikon + mətn) göstərilir, sifarişi bloklamır — müştəri kodu silib davam edə bilər.

**Real ölçü kilidi (kiosk və mobil çap parametrləri, Addım 6).** Skan və tərtibat faylı (`realSizePaper` doludur, plan 2.11) real ölçüdə çap olunur: parametrlər ekranının yuxarısında `info` blok — `info` ikonu + mətn "Bu sənəd real ölçüdə (100%) {kağız} kağızında, vərəqdə 1 səhifə çap olunur — kartın ölçüsü dəyişməsin deyə bu seçimlər dəyişdirilmir." Kağız və "vərəqdə səhifə" sətirləri görünür qalır, kilidli dəyər seçili göstərilir, düymələr deaktivdir (gizlədilmir — müştəri nəyin niyə sabit olduğunu görür). Nüsxə, rəng, ikitərəfli və səhifə aralığı dəyişdirilə bilər. Kiosk A3 dəstəkləmirsə A3 tərtibatı üçün blok `warning` olur ("Bu kioskda A3 kağızı yoxdur — sənədi A3 olan kioskda çap edin") və əsas düymə deaktivdir.

**Səhifədə yerləşdir (kiosk, ekran 4a, Addım 6, plan 2.11).** Önizləmədə yalnız şəkil faylı üçün ikinci dərəcəli "Səhifədə yerləşdir" düyməsi ("Redaktə et"-in yanında). Ekran: solda kətan (kağız `Neutral0` + `Neutral200` kənar, kənar boşluğu `Neutral400` kəsik xətt, şəkil, seçim `Brand500` 3 px çərçivə; printer kənarına düşəndə çərçivə `Warning500`), üstündə bir sətir ipucu ("Şəkli barmağınızla sürüşdürün, iki barmaqla böyüdün və ya kiçildin"). Sağda alət sütunu (iri toxunma düymələri, `secondary`): **Ölçü** qrupu — "Vəsiqə 85,6 × 54 mm", "Foto 3×4", "Foto 10×15", "A6", "A5", "Kağıza sığdır" (seçilmiş şəklə o real ölçünü verir, şəklin tutuluş istiqaməti saxlanır); **Şablon** qrupu — "2 × 2", "8 kart", "Foto 3×4 × 6", "Mərkəzdə" (flash-dan tək şəkil gəldiyi üçün "Vəsiqə ön + arxa" yoxdur); **Əməliyyat** — "Fırlat 90°", "Böyüt", "Kiçilt", "Ortala", "Dublikat", "Sil" (bir şəkil qalanda yoxdur); **Kağız** — A4 / A3 (A3 printer dəstəkləmirsə yoxdur). Toxunuş barmağın altındakı ən üstdəki şəkli seçir, boş yerə toxunuş seçimi götürür; seçim yoxdursa Ölçü və Əməliyyat düymələri deaktivdir. Seçilmiş şəklin ölçüsü kətanın altında ("85,6 × 53,98 mm") — bu sətir seçim olmayanda da öz yerini tutur. Xəbərdarlıq: printer kənarına düşəndə `warning` blok, mərkəzi kağızdan çıxanda `danger` blok və "Hazır" deaktiv; bloklar alət sütununun altında, scroll-dan kənardadır. **Kətanın ölçüsü ekranda heç nə görünəndə və ya itəndə dəyişmir** — dəyişsə, barmaq altında miqyas dəyişər və sürüşdürülən şəkil qaçar (real kioskda tapıldı). Alt sətir: "İmtina" (`common.cancel`, secondary, önizləməyə qayıdır, heç nə dəyişmir) və əsas düymə "Hazır" — tərtibat kioskda PDF-ə çevrilir (backend-in eyni renderer-i), önizləməyə qayıdır və çap seçimləri "Real ölçü kilidi" ilə kilidlənir; bundan sonra "Redaktə et" göstərilmir (şəkil redaktoru tərtibatı silərdi), "Səhifədə yerləşdir" isə tərtibatı olduğu kimi yenidən açır. Anonim istifadəçinin tərtibatı saxlanılmır (plan 2.11).

**Mobil: ana səhifə, ödəniş və balans (Addım 4).**

- *Balans kartı (ana səhifədə birinci):* "Balans" etiketi, məbləğ `display` ölçüsündə (tabular rəqəm), altında "N bonus səhifə" (0-dırsa yazılmır); kartın özü Balans ekranını açır (sağda `chevron`). Əsas düymə kartın altındadır: kamera ilə oxuya bilən cihazda "QR oxut" (primary), "Fayllarım" ikinci dərəcəli olur; web-də skaner yoxdur, ona görə "QR oxut" göstərilmir və "Fayllarım" primary qalır.
- *Sifariş kartı:* fayl adı (yoxdursa sifariş kodu) başlıq, altında kiosk adı + kodu, "N səhifə × M nüsxə", məbləğ və tarix; sağ yuxarıda status chip-i (rəng + ikon + mətn): Ödəniş gözləyir — warning, Növbədə — info (saat ikonu), Çap olunur — info (dönən), Tamamlandı — success, Dayandı — danger, Nəticə dəqiqləşdirilir (`Suspected`) — warning (qum saatı), Ləğv edildi / Vaxtı bitdi — neutral. `Suspected` kartında chip-dən əlavə info bloku: "Çapınızın nəticəsi dəqiqləşdirilir. Pulunuz itməyib — nəticə bilinən kimi sizə bildiriləcək." Dayanma/ləğv səbəbi varsa kartda yazılır. **Dayanmış sifariş (plan 9.6):** çap yarımçıq qalıbsa (`Interrupted`) və ya sifariş printeri işləməyən kioskda gözləyirsə (`kioskStopped`), kartda warning bloku (ikon + mətn: nə oldu — "Kioskda printer işləmir" / "Çap yarımçıq qaldı"; nə etmək olar — "Gözləyə bilərsiniz… və ya sifarişi ləğv edib məbləği geri ala bilərsiniz") və altında tam enli secondary düymə "Ləğv et və məbləği qaytar". Kiosk işləyirsə, növbədəki sifarişdə yalnız ghost düymə "Sifarişi ləğv et". Hər ikisi təsdiq dialoqu açır: başlıq "Sifariş ləğv edilsin?", mətn — qaytarılan məbləğ və hara qayıtdığı ("0,20 ₼ NeoPrint balansınıza qaytarılacaq. Ləğv edilən sifariş çap olunmayacaq."), düymələr "Xeyr, gözləyirəm" (ghost) və "Bəli, ləğv et" (danger). Nəticə snackbar-da; rədd olunarsa səbəb oxunan dildə. Nəticəsi olan sifarişdə "Problem barədə bildir" (ghost düymə) — mətn sahəsi (1–1000 simvol, sayğac ilə) olan alt panel açır, panelin bir primary düyməsi "Göndər".
- *Ana səhifənin düymələri:* tək sütunda, hər biri tam enli: bir primary ("QR oxut"; skaner olmayan cihazda "Fayllarım"), altında secondary-lər ("Fayllarım", "Mənim QR kodum", "Hazırladıqlarım"). Düymələr yan-yana qoyulmur — 360 dp enində yarım sətrə sığmayan söz ortasından sınır ("Hazırladıqları / m", T-239), rus mətnləri isə daha uzundur.
- *Ana səhifənin siyahıları:* yuxarıda "Aktiv sifarişlər" (ödəniş gözləyən, növbədə, çap olunan, dayanmış — printer qayıdanda çap davam edir —, dəqiqləşdirilən), altında "Son sifarişlər". Heç bir sifariş yoxdursa empty state: ikon + "Hələ sifarişiniz yoxdur" + izah ("Kioskda QR oxudub ödədiyiniz sifarişlər burada görünəcək").
- *QR skaner:* tam ekran kamera, ortada kvadrat çərçivə, altında bir cümlə təlimat ("Kioskdakı “NeoPrint tətbiqi” QR kodunu çərçivəyə salın"). Bank QR-ı və ya başqa kod oxunanda çərçivənin altında warning sətri (ikon + "Bu QR NeoPrint ödənişi deyil. Bank QR-ını bank tətbiqinizlə oxudun."), skaner işləməyə davam edir. Kameraya icazə yoxdursa nə olduğu və necə açılacağı yazılır.
- *Ödəniş təsdiqi:* yuxarıda məbləğ `display`, altında geri sayım mətni ("Ödəniş üçün 4:32 vaxtınız var"; son 60 saniyədə warning rəngi + ikon). Kart: kiosk (ad + kod), fayl, "N səhifə × M nüsxə", rəngli / ağ-qara, kağız. Bonus səhifə varsa keçid (switch) — "N bonus səhifə istifadə et (−X ₼)". Promokod tətbiq olunursa yaşıl ✓ sətir "Promokod X: −Y ₼"; tətbiq olunmursa səbəbi warning blokunda. Kioskda kod verilməyibsə "Promokodunuz var?" (ghost düymə) — basılanda kod sahəsi və altında "Tətbiq et" (secondary, tam en; formalar tək sütundur), uyğun olmayan kod sahənin altında səbəbi ilə (ikon + mətn); tətbiq olunan kod yaşıl sətirdə, yanında "Promokodu sil" (×). Ödəniş mənbəyi iki seçim kartı (radio): "Balansdan" (cari balans yazılır; çatmırsa "Balans çatmır" warning mətni və seçim deaktiv deyil — kart seçilir) və "Bank kartı". Bir primary düymə: "X ₼ ödə" — ekranın **altında sabitdir** (siyahı onun üstündə sürüşür), ödəniş xətası düymənin dərhal üstündə göstərilir ki, sürüşdürmədən görünsün. Uğurda yaşıl ✓ + "Ödəniş uğurlu" + sifariş kodu + "Kioskda çap başlayır" + "Ana səhifəyə qayıt"; vaxt bitibsə warning blok + "Kioskda yenidən ödəniş açın"; kart rədd etdisə xəta blokunda nə oldu + "məbləğ tutulmayıb" + başqa mənbə tövsiyəsi; cavab itibsə (`payments.outcomeUnknown`) "yenidən ödəməyin" xəbərdarlığı.
- *Balans ekranı:* balans kartı (məbləğ `display` + bonus səhifə), altında **daim görünən** warning bloku: "Balansa yüklənən məbləğ geri qaytarılmır və karta çıxarıla bilməz." Bir primary düymə "Balansı artır" — alt paneldə hazır məbləğ seçimləri (2, 5, 10, 20 ₼) + "başqa məbləğ" sahəsi (1–500 ₼) və təsdiq düyməsi "X ₼ kartla ödə". "Voucher kodu" ikinci dərəcəli düymə — kod sahəsi olan dialoq. Hərəkətlər siyahısı: növ (ikon + mətn), qeyd, tarix, sağda işarəli məbləğ (gələn `--success-500` "+", çıxan neytral "−") və sonrakı balans; boşdursa empty state.
- *Qəbzlər:* sətir — nömrə (`NP-2026-000001`), tarix, nə üçün (çap / balans artırılması), məbləğ, fiskal chip (verildi — success, hazırlanır — warning); hər sətirdə "Email-ə göndər" (ghost). Boşdursa empty state.
- *Canlı bildirişlər (`/hubs/user`):* aşağıda snackbar — sifariş `Suspected` olanda "Çapınızın nəticəsi dəqiqləşdirilir"; qaytarma uğurlu olanda Addım 5-dən snackbar yox, bildiriş banneri göstərilir ("Mobil: bildirişlər və push"); siyahılar özü yenilənir, istifadəçi "yenilə" basmır.

**Mobil: kiosklar (Addım 5).**

- *Kiosklar bölməsi:* yuxarıda axtarış sahəsi (bir sətir, ipucu "Ad, kod və ya ünvan", sağda təmizləmə ×), altında bir sətirdə görünüş seçimi `SegmentedButton` ("Siyahı" | "Xəritə", ikon + mətn) və "Yaxınımda" filter chip-i. Yaxınımda seçiləndə radius seçimi (1 / 3 / 10 km, chip-lər) görünür; məkan icazəsi verilməyibsə chip-in altında warning sətri (ikon + nə oldu + necə açılır) və siyahı məkansız qalır. Siyahı və xəritə **eyni nəticəni** göstərir — filtr hər ikisinə tətbiq olunur. Axtarış hər hərfdə yox, yazı dayananda (≈ 400 ms) gedir.
- *Kiosk sətri (kart):* başlıq — kiosk adı, altında kod + ünvan (`bodySmall`, neytral); sağda status chip-i, məsafə məlumdursa chip-in altında "0,5 km" (tabular). Kartın özü detalı açır. Status chip-i: İşləyir (`Online`) — success, Fasilədə (`Paused`) — warning, Servisdə (`Maintenance`) — warning (alət ikonu), Əlaqə yoxdur (`Offline`) — danger.
- *Xəritə:* OpenStreetMap (`flutter_map`), sağ aşağıda atribusiya (`© OpenStreetMap`) həmişə görünür. Marker status tonunun rəngində dairədir, içində status ikonu (rəng tək başına deyil); xəritənin altında status legendası (rəng + ikon + mətn). İstifadəçinin yeri məlumdursa brend rəngli nöqtə. Marker-ə toxunanda aşağıda kart açılır: ad + kod + status chip-i + məsafə + "Ətraflı" (secondary). Tile ünvanı konfiqurasiyadandır (`NEOPRINT_MAP_TILE_URL`); ictimai OSM serveri yalnız development-də (T-061).
- *Kiosk detalı:* başlıq — ad; altında kod, ünvan, status chip-i (+ məsafə). Kartlar: **İmkanlar** (Rəngli çap, İkitərəfli çap, A3 — hər sətir ikon + mətn: var — success ✓, yox — neytral "—" ikonu ilə "yoxdur" mətni); **Qiymətlər** (A4 ağ-qara bir üz, A4 rəngli bir üz, A3 əmsalı, minimum sifariş, həcm endirimləri "50 üzdən −10%"; pul §6 formatında); **Növbə** ("Növbədə 2 sifariş" / "Növbə boşdur"); **Kağız** — hər ölçü üçün status sətri: kifayətdir — success, "A4 kağızı azdır" — warning, "A4 kağızı qurtarıb" — danger + nəyə təsir etdiyi ("Bu kioskda hazırda A4 çap etmək olmur"); sayğacı olmayan ölçü göstərilmir. Əsas düymə "Sessiya aç" — sessiya ilə çap hazır olanda (Addım 5 / M2); o vaxta qədər göstərilmir.
- *Boş və xəta vəziyyətləri:* axtarışa uyğun kiosk yoxdur — ikon + "Axtarışa uyğun kiosk tapılmadı" + "Axtarışı təmizlə"; yaxınlıqda yoxdur — "N km radiusda kiosk yoxdur" + "Radiusu böyüt"; yüklənmədi — nə oldu + "Yenidən cəhd et". Gizli kiosk (aktivləşdirilməmiş, söndürülmüş) heç yerdə görünmür; detal linki ilə açılanda "Kiosk tapılmadı" empty state-i.

**Mobil: sessiya ilə çap (Addım 5, plan 9.3).**

- *Sessiya aç:* kiosk detalının altında sabit əsas düymə "Sessiya aç" — yalnız `İşləyir` statusunda. Başqa statusda düymə yerinə info sətri (ikon + "Bu kiosk hazırda sifariş qəbul etmir"). Sessiya bu kioskdadırsa əsas düymə "Fayl seç" olur, altında ghost düymə "Sessiyanı bağla". Kioskun QR-ı (`neoprint://kiosk/{kod}`) tətbiqin QR skanerində oxunanda həmin kioskun detalı açılır.
- *Aktiv sessiya banneri:* tabların hamısında, aşağı naviqasiya panelinin dərhal üstündə (hər tabın öz başlığı var, banner onlarla toqquşmur) `info` blok: kiosk ikonu + "{kod} · {ad}" + qalan vaxt ("9 dəq qalıb", tabular; son 2 dəqiqədə `warning` rəngi + ikon). Sağda iki ghost düymə: "Fayl seç" və bağlama (× ikon, ekran oxuyucu üçün "Sessiyanı bağla"). Banner əsas düymə daşımır — ekranın öz əsas düyməsi qalır.
- *Fayl seçimi:* "Faylı seçin" ekranı — yalnız hazır (`Ready`) fayllar, kart: ad, format, səhifə sayı; toxunuş seçir. Hazır fayl yoxdursa empty state: ikon + "Çap üçün hazır faylınız yoxdur" + "Fayl yüklə" (Fayllarım tabına aparır). Hazırlanan fayllar göstərilmir (deaktiv kart yoxdur).
- *Çap parametrləri:* başlıq — fayl adı, altında "{kiosk kodu} · N səhifə". Forma tək sütun: Nüsxə (− / + düymələri, ortada rəqəm), Rəng (`SegmentedButton`: Ağ-qara | Rəngli), İkitərəfli (switch), Kağız (A4 | A3), Səhifələr (Hamısı | Aralıq; aralıqda sahə "1-3, 5", xəta sahənin altında), Vərəqdə səhifə (1 | 2 | 4). Kioskun bacarmadığı seçim (rəng, ikitərəfli, A3) deaktiv seqment / switch kimi qalır və altında səbəbi yazılır ("Bu kiosk rəngli çap etmir") — bu, kioskun real imkanıdır (kiosk qaydası ilə eyni). Qiymət kartı: yekun məbləğ `display`, altında "N üz · M vərəq", həcm endirimi varsa yaşıl sətir; parametr dəyişəndə yenilənir. Kağız çatmırsa warning blok (nə oldu + "başqa kiosk seçin və ya nüsxəni azaldın"). Əsas düymə ekranın altında sabit: "Sifariş et — X ₼"; sifariş xətası düymənin dərhal üstündə. Sifarişdən sonra mövcud ödəniş ekranı açılır (balans / kart / bonus / promokod).
- *Sessiya bitdi:* hub `SessionClosed` (`Expired`) gələndə aşağıda snackbar "Sessiya bitdi. Çap üçün kioskda yenidən açın"; banner yox olur. Bağlama / əvəzlənmə üçün snackbar yoxdur (istifadəçinin öz hərəkətidir).

**Mobil: şəxsi QR və "Kiosk üçün hazırla" (Addım 5, plan 9.4).**

- *Mənim QR kodum:* ayrıca ekran, başlıq "Mənim QR kodum". Mərkəzdə ağ fonlu kartda QR şəkli (backend PNG, ekran eninin ~70%-i, quiet zone ağ — skaner kontrast tələb edir, dark mode-da da ağ), altında bir cümlə "Kioskdakı QR oxuyucuya yaxınlaşdırın" və kiçik `caption` "Kod {N} saniyədən bir yenilənir". Kod vaxtı bitməzdən 5 saniyə əvvəl özü yenilənir; yenilənmə alınmasa QR gizlənir, yerinə xəta bloku + "Yenidən cəhd et" (köhnə kod göstərilmir — kiosk onu qəbul etməyəcək). Ekranda ödəniş/ləğv düyməsi yoxdur; əsas düymə — "Hazırladıqlarım" siyahısına keçid deyil, yalnız "Bağla" (geri).
- *Kiosk üçün hazırla:* faylın menyusundan (yalnız `Ready`). Başlıq — fayl adı, altında "N səhifə". Hər parametr seqmentli düymədir və **ilk seqmenti "Kioskda"**dır (seçilməyib — kioskda seçiləcək): Rəng (Kioskda / Ağ-qara / Rəngli), İkitərəfli (Kioskda / Bir üz / İki üz), Kağız (Kioskda / A4 / A3), Vərəqdə səhifə (Kioskda / 1 / 2 / 4), Səhifələr (Kioskda / Hamısı / Aralıq), Nüsxə (Kioskda / sayğac). Aşağıda "Kiosk (istəyə görə)" sətri: seçilməyibsə "İstənilən kioskda çap etmək olar", seçiləndə kiosk kartı (kod · ad, status chip) + "Dəyiş" / "Sil". Kiosk seçiləndə "Kioskda" seqmentləri itir, hər parametr dəyər alır (əvvəlcədən ödəniş üçün qiymət tam seçim tələb edir) və həmin kioskun edə bilmədiyi seçim boz + səbəb; qiymət kartı (server qiyməti, cari profil). Əsas düymə: kiosk seçilməyibsə "Hazırla" (ödənişsiz saxlanır), seçilibsə "Əvvəlcədən ödə — X ₼"; ikinci dərəcəli mətn düyməsi "Ödənişsiz saxla". Server `priceChanged` qaytaranda dialoq: "Qiymət dəyişib" + köhnə və yeni məbləğ + "Yeni qiymətlə ödə" / "İmtina".
- *Hazırladıqlarım:* siyahı ekranı; hər kart: fayl adı, sətirdə "N səhifə · seçilənlər" (seçilməyənlər yazılmır, heç biri seçilməyibsə "Parametrlər kioskda seçiləcək"), status chip (rəng + ikon + mətn): Hazır (info), Çap olunur (info), Çap olunub (success), Fayl əlçatan deyil (warning, izahla), Ödənilib (success) — ödənilmişdə həmçinin "KSK-… kioskunda · X ₼" və "{tarix} tarixinədək götürün, sonra pul balansa qayıdır". Kartın menyusu: ödənişsiz — "Siyahıdan çıxar"; ödənilmiş — "Ləğv et" (`danger` təsdiq dialoqu: "X ₼ balansınıza qaytarılacaq"). Əsas düymə "Mənim QR kodum". Boş siyahı: ikon + "Kiosk üçün hazırladığınız fayl yoxdur" + "Faylın menyusundan "Kiosk üçün hazırla"nı seçin".
- *Ödəniş uğurlu (əvvəlcədən ödənilmiş):* başlıq "Ödəniş uğurlu", mətn "Sifariş hazırdır. {KSK} kioskunda QR kodunuzu oxudun — çap o zaman başlayacaq." (növbəyə düşdü demir).

**Mobil: planlanmış çap (Addım 5, plan 9.5).**

- *Tab "Planlanmış":* siyahı ekranı, yuxarıda əsas düymə "Yeni planlama". Kartlar ən yaxın vaxt əvvəl deyil, ən yenisi əvvəl (server sırası) gəlir. Hər kartda: fayl adı, "KSK-… · {tarix saat}" sətri, "N səhifə · seçilənlər · X ₼" sətri və status chip (rəng + ikon + mətn): Ödəniş gözlənilir (warning), Planlanıb (info), Vaxtı çatıb (warning, ikon `notifications_active`), Çapa verilib (success; job çap olunubsa "Çap olunub"), Ləğv edilib (neutral), Vaxtı keçdi (neutral, altında "Pul balansa qaytarıldı"). "Vaxtı çatıb" kartında altda "Çapa ver" (primary) + "Vaxtı dəyiş" (mətn düyməsi) və "{tarix saat}-dək cavab verməsəniz ləğv olunacaq, pul balansa qayıdacaq" `caption`-u. Kartın menyusu (yalnız aktivdə): "Çapa ver", "Vaxtı dəyiş", "Ləğv et" (`danger` təsdiq: "X ₼ balansınıza qaytarılacaq"); ödəniş gözləyəndə "Ödə" + "Ləğv et". Boş siyahı: ikon + "Planlanmış çapınız yoxdur" + "Faylı seçin, kiosku və vaxtı göstərin — çap vaxtında hazır olsun" + "Yeni planlama".
- *Yeni planlama:* başlıq "Çapı planla". Ardıcıl bölmələr: **Fayl** (seçilməyibsə "Fayl seç" — hazır faylların alt vərəqi; faylın menyusundan "Planla" ilə gəlibsə fayl hazır seçilib), **Kiosk** ("Kiosk seç" — axtarışın alt vərəqi, seçiləndə kod · ad + status chip + "Dəyiş"), **Parametrlər** (hazırlıq ekranının seqmentləri, "Kioskda" seqmenti yoxdur; kioskun edə bilmədiyi boz + səbəb), **Vaxt** (tarix və saat seçiciləri — sistem Material dialoqları; tarix seçicisində həftə günləri qısa adla, ARB-dən — `weekdayShort*`, az: B.e., Ç.a., Ç., C.a., C., Ş., B.; CLDR-in az rəqəmləri 1–7 göstərilmir, T-156; altında `caption` "Ən tez 15 dəqiqə, ən gec {N} gün sonra"), **Qiymət kartı** (server qiyməti). Əsas düymə "Ödə və planla — X ₼" (kiosk, fayl və vaxt seçilməyibsə deaktiv deyil, yerinə nəyin çatmadığını deyən mətn göstərilir: "Kiosk seçin" və s.). `priceChanged`-də hazırlıq ekranındakı "Qiymət dəyişib" dialoqu.
- *Vaxt çatanda (`ScheduleDue`):* tətbiq açıqdırsa dialoq — başlıq "Planlanmış çapın vaxtı çatıb", mətn "{fayl} · KSK-… kioskunda. Kioskun yanındasınızsa çapa verin.", düymələr "Vaxtı dəyiş" (sol, mətn) və "Çapa ver" (sağ, primary). "Çapa ver" uğursuz olanda (kiosk işləmir, kağız yoxdur) xəta nə baş verdiyini deyir və eyni dialoqda "Vaxtı dəyiş" təklif olunur. **Bir neçə plan eyni anda (T-159):** vaxtı çatan bir neçə planlanmış çap üst-üstə dialoq açmır — bir dialoq, başlıq "Planlanmış çapların vaxtı çatıb", altında siyahı: hər sətirdə fayl adı + "KSK-… · {saat}" və həmin planın öz düymələri "Vaxtı dəyiş" (mətn) və "Çapa ver" (doldurulmuş). Cavablanan sətir siyahıdan çıxır; xəta həmin sətrin altında görünür; siyahı boşalanda dialoq bağlanır. Dialoq açıqkən yeni plan vaxtı çatarsa siyahıya əlavə olunur. Tək plan olanda dialoq əvvəlki kimidir.
- *Ana səhifə (T-158):* planlanmış çapın sifarişi vaxtı gözləyərkən (`Paid`) "Aktiv çaplar"-da "Ödənildi" yox, status chip-i "Planlanıb · {tarix saat}" (`info`, `schedule` ikonu) ilə göstərilir — istifadəçi onu kioskda gözləyən sifariş sanmır.
- *Ödəniş uğurlu (planlanmış):* başlıq "Ödəniş uğurlu", mətn "Çap {tarix saat} üçün planlandı. Vaxt çatanda tətbiq soruşacaq: çapa verək, ya vaxtı dəyişək." Əsas düymə "Planlanmış çaplar".

**Mobil: bildirişlər və push (Addım 5, plan 8.2 / 8.4).**

- *Zəng:* ana səhifənin başlığında sağda zəng ikonu (`notifications_outlined`); oxunmamış varsa üstündə `Badge` — say (99-dan çox olanda "99+"), fon `danger`. Ekran oxuyucu üçün "Bildirişlər, 3 oxunmamış". Zəng bildirişlər ekranını açır.
- *Bildirişlər ekranı:* başlıq "Bildirişlər", ən yenisi yuxarıda. Sətir: solda tip ikonu tonlu dairədə (çap hazır — `print`, success; planlanmış çapın vaxtı — `alarm`, info; planlanmış çap ləğv olundu — `event_busy`, warning; pul qaytarıldı — `undo`, success; fayl avtomatik silindi — `auto_delete_outlined`, neytral; digərləri — `notifications`, neytral), başlıq (`titleSmall`; oxunmamışda qalın + sağda brend rəngli 8 px nöqtə), mətn (`bodyMedium`, ən çox 3 sətir), altında vaxt (`bodySmall`, neytral; §6 tarix formatı). Sətrə toxunuş oxundu edir və bildirişin ekranını açır (link yoxdursa yalnız oxundu). Siyahının sonuna çatanda köhnələr özü yüklənir. Boş: ikon + "Hələ bildiriş yoxdur" + "Çap hazır olanda, planlanmış çapın vaxtı çatanda və pul qaytarılanda burada görəcəksiniz". Yüklənmədi: nə oldu + "Yenidən cəhd et".
- *Tətbiq daxili banner:* tətbiq açıq ikən yeni bildiriş gələndə ekranın yuxarısında `MaterialBanner`: tip ikonu + başlıq + mətn (2 sətir), iki ghost düymə — "Aç" (link varsa) və "Bağla"; 6 saniyədən sonra özü bağlanır. Eyni bildiriş bir dəfə göstərilir. Planlanmış çapın vaxtı üçün banner yoxdur — onu "Çapa ver / Vaxtı dəyiş" dialoqu deyir.
- *Push icazəsi:* sistem dialoqu yalnız girişdən sonra soruşulur. İmtina olunubsa tətbiq təkid etmir; bildirişlər siyahısı və banner yenə işləyir.
- *Push-dan açılış:* bildirişə toxunanda tətbiq onun ekranında açılır (çap hazır → ana səhifə, planlanmış çap → "Planlanmışlar", pul → Balans) və bildiriş oxundu olur.

**Mobil: məxfilik (Addım 5, plan 2.13; `DECISIONS.md` T-147).**

- *Avtomatik silinmə (fayl):* faylın menyusunda "Avtomatik silinmə" (`auto_delete_outlined`). Alt vərəq: başlıq "Avtomatik silinmə", bir cümlə izah ("Fayl bütün nüsxələri ilə birdəfəlik silinir, silinəndə bildiriş gəlir"), radio siyahı — Heç vaxt / Çapdan sonra (altında `bodySmall`: "Bu faylla bütün sifarişlər çap olunandan sonra") / Yükləmədən 1 gün sonra / 7 gün sonra / 30 gün sonra. Seçim toxunanda saxlanır və vərəq bağlanır (ayrıca düymə yoxdur); xəta olsa snackbar. Qayda "Heç vaxt" deyilsə fayl kartında neytral chip: `auto_delete_outlined` + "Çapdan sonra silinəcək" / "1 gündən sonra silinəcək"…
- *Profil → "Məxfilik" bölməsi:* iki sətir — "Məlumatlarımı yüklə" (`download_outlined`) və "Hesabımı sil" (`person_remove_outlined`, mətn və ikon `danger`). Hər biri öz ekranını açır.
- *Məlumatlarımı yüklə:* izah kartı (nə daxildir: profil, ödənişlər, qəbzlər PDF, fayllar — orijinal və versiyalar; ölçü həddi keçəndə faylların siyahısı). Vəziyyətə görə bir əsas düymə: yoxdursa / vaxtı keçibsə "Arxiv hazırla"; hazırlanırsa düymə yox, `info` sətri ("Hazırlanır… Bitəndə bildiriş gələcək") + kiçik progress; hazırdırsa "Yüklə" (link brauzerdə açılır) və altında "{vaxt}-a qədər keçərlidir"; alınmadısa `danger` sətri (nə oldu + "Yenidən hazırla"). Hazır olanda bildiriş `ExportReady` bu ekranı açır.
- *Hesabımı sil:* başlıq "Hesabımı sil". Yuxarıda `danger` blok: "Bu geri qaytarılmır" + nəyin silindiyi (fayllar bütün nüsxələri ilə, profil; maliyyə qeydləri qanuna görə adsız qalır) + **balans**: "Balansınızdakı X ₼ və N bonus səhifə itəcək, geri qaytarılmır" (0-dırsa sətir yoxdur). Mane olan nə varsa (aktiv çap, planlanmış çap, kioskda götürülməmiş sifariş, gözləyən refund) `warning` siyahı — hər sətirdə nə və hara getmək ("Planlanmışlar"-a keçid); bu halda forma deaktiv deyil, gizlidir. Mane yoxdursa: şifrə sahəsi + "Təsdiq üçün SİL yazın" sahəsi, sonra `danger` əsas düymə "Hesabımı sil" — hər iki sahə dolana qədər deaktiv. Uğurdan sonra çıxış olur, giriş ekranında snackbar "Hesabınız silindi".

**Mobil: dil və hüquqi sənədlər (Addım 5, plan 8.2 "profile"; `DECISIONS.md` T-152).**

- *Profil → "Dil"* (`language`): sətrin altında hazırkı seçim ("Telefonun dili · Azərbaycan" və ya dilin adı). Toxunanda alt vərəq: başlıq "Dil", bir cümlə izah ("Tətbiq, email-lər və bildirişlər bu dildə olacaq — bütün cihazlarınızda."), radio siyahı: "Telefonun dili" (altında telefonun hazırkı dili), "Azərbaycan", "English", "Русский" — dil adları həmişə öz dilində, tərcümə olunmur. Seçim dərhal tətbiq olunur (ekran dərhal həmin dilə keçir), vərəq bağlanır; serverə yazıla bilməsə seçim geri qaytarılır və snackbar xətanı üç hissə ilə deyir.
- *Profil → "Hüquqi sənədlər"* (`description_outlined`) → siyahı ekranı: "Məxfilik siyasəti", "İstifadə şərtləri" (hər sətirdə ikon + ad + `chevron_right`).
- *Sənəd ekranı* (giriş etmədən də açılır): app bar-da sənədin adı; yuxarıda kiçik sətir "Versiya {v} · {tarix}" (`neutral600`, `bodySmall`); sənəd tətbiqin dilində yoxdursa altında `info` banner "Bu sənəd hələ yalnız Azərbaycan dilindədir." Mətn öz sadə markdown göstəricimizlə (paket yoxdur): `#` başlıq `titleLarge`, `##` `titleMedium`, `>` qeyd bloku `warning` tonlu banner kimi, `- ` siyahı nöqtəli sətirlər, `**qalın**`, cədvəl sətirləri xanaları " · " ilə ayrılmış mətn; abzaslar arasında `Spacing.x3`, kənarlarda `Spacing.x4`. Yüklənmə — mərkəzdə spinner; xəta — boş vəziyyət (nə oldu + "Yenidən cəhd et").
- *Qeydiyyat və razılıq ekranı:* hər razılıq qutusunun altında/yanında "Oxu" mətn düyməsi (`TextButton`, ikon yox) sənəd ekranını açır; qayıdanda qutunun vəziyyəti dəyişmir.

**Mobil: sənəd skanı (Addım 6, plan 2.11; `DECISIONS.md` 2026-10-05).**

- *Giriş:* "Fayllarım" → yükləmə alt vərəqində üçüncü sətir "Sənəd skanı" (`document_scanner_outlined`, alt mətn "Vəsiqə, pasport, sənəd — kənarlar özü tapılır"). Web build-də sətir yoxdur (skaner yalnız telefonda). Boş "Fayllarım"-da da həmin alt vərəq açılır.
- *Skan razılığı* (`ScanNotice`, ilk skandan əvvəl və yeni versiya dərc olunanda): tam ekran alt vərəq, başlıq "Şəxsi sənədlərin skanı", sənəd mətni (öz markdown göstəricimiz, sürüşür), altında sabit iki düymə: "Qəbul edirəm" (primary) və "İndi yox" (mətn düyməsi — skan açılmır, ekran bağlanır). Mətn yüklənmədisə vərəqdə xəta bloku (nə oldu + "Yenidən cəhd et"); qəbul göndərilmədisə düymənin üstündə `danger` sətri.
- *Skanı hazırla ekranı* (skanerdən qayıdanda): app bar "Skanı hazırla". Yuxarıdan aşağı: (1) **Səhifələr** — üfüqi sıra, hər kiçik şəklin altında "Səhifə N", toxunanda səhifə redaktoru, uzun basanda/menyuda "Sil"; sıranın sonunda "+" kart ("Səhifə əlavə et" — skaneri yenidən açır); (2) **Sənəd tipi** — `ChoiceChip`-lər: Şəxsiyyət vəsiqəsi / bank kartı (ID-1), ID-2, Pasport, Foto 3×4, Foto 10×15, A6, A5, A4, Xüsusi; hər chip-də ölçü ("85,6 × 54 mm") ikinci sətirdə; "Xüsusi"də en / hündürlük (mm) sahələri; (3) **Həssas sənəd** — `SwitchListTile`, alt mətn "Şifrəli saxlanılır, çapdan sonra silinir"; vəsiqə / ID-2 / pasport seçiləndə açıq və deaktiv, alt mətn "Şəxsi sənəd — həmişə həssas"; (4) **Kağızda yerləşmə** — şablon chip-ləri (hər səhifə ayrıca, Vəsiqə ön + arxa, 2 × 2, 8 kart, Foto 3×4 × 6, Pasport mərkəzdə; sənəd tipinə uyğun olanlar), altında A4 önizləməsi (ağ kağız, `neutral200` kənar xətti, kənar boşluğu kəsik xətlə, şəkillər real ölçüdə yerləşmiş halda, bir neçə səhifə üfüqi sürüşür); (5) **Ad** sahəsi (default sənəd tipinin adı); (6) `info` sətri "Saxlanandan sonra skan dəyişdirilmir — yalnız çap olunur". Sabit əsas düymə "Yadda saxla"; işləyəndə düymədə spinner + "Hazırlanır…" və ekran bloklanır. Xəta — düymənin üstündə `danger` blok (nə oldu + təsir: "skan saxlanılmadı" + nə etmək).
- *Səhifə redaktoru:* qara fon, şəkil ortada, üstündə kəsmə çərçivəsi (ağ 2 px xətt, 4 küncdə 24 px tutacaq, çərçivədən kənar 50% qaranlıq); altda alət sırası: "Fırlat" (`rotate_right`), filtr `SegmentedButton` (Orijinal / Boz / Ağ-qara / Kontrast), "Parlaqlıq" slider (−100…100, ortada sıfır işarəsi), "Sıfırla" mətn düyməsi; əsas düymə "Hazır". Filtr və parlaqlıq önizləmədə dərhal görünür; son emal saxlananda edilir.
- *Uğur:* "Fayllarım"-a qayıdış, snackbar "Skan saxlanıldı", siyahıda yeni fayl `Skan` chip-i, həssasdırsa `Həssas` və "Çapdan sonra silinəcək" chip-ləri ilə. Saxlanılmış skanın menyusunda "Redaktə et" / "Tərtibatı redaktə et" yoxdur.
- *Skaner açılmadı:* telefonda skaner yoxdursa (Google Play xidmətləri yoxdur / köhnədir) `danger` deyil `warning` blok: "Bu telefonda sənəd skaneri işləmir. Sənədin şəklini "Şəkil çək" ilə yükləyə bilərsiniz." + "Şəkil çək" düyməsi.

**Mobil: səhifə tərtibatı redaktoru (Addım 6, plan 2.11).**

- *Girişlər:* (1) skan ekranında önizləmənin altında "Düzəliş et" (`tune`, ikinci dərəcəli düymə) — redaktor skanın yerləşməsini açır, "Hazır" ilə qayıdır, yerləşmə chip-ləri "Öz yerləşməm" olur; (2) şəkil faylının menyusunda "Səhifədə yerləşdir" (`dashboard_customize_outlined`); (3) yükləmə alt vərəqində "Yeni tərtibat" (`dashboard_customize_outlined`, alt mətn "Şəkilləri və PDF səhifələrini bir vərəqdə yerləşdirin") — hazır, kilidsiz faylların çoxseçimli siyahısı (PDF üçün səhifə nömrəsi seçilir), "Davam et"; (4) tərtibat faylının menyusunda "Tərtibatı redaktə et" (`edit_outlined`). Saxlanılmış skan heç bir girişdə siyahıya düşmür.
- *Ekran:* app bar-da başlıq ("Tərtibat" və ya faylın adı), sağda "Şablon" (`grid_view`) menyusu (Vəsiqə ön + arxa, 2 × 2, 8 kart, Foto 3×4 × 6, Pasport mərkəzdə) və "Kağız" (`description_outlined`) alt vərəqi (A4 / A3 / A5, portret / landşaft, kənar boşluq 0–30 mm slider). Ortada kətan: `neutral100` fon, ağ kağız `neutral200` kənar xətti ilə, yuxarıda və solda mm xətkeşləri (10 mm-də rəqəm, 5 mm-də uzun, 1 mm-də qısa xətt; `bodySmall`, `neutral600`), kağızda 5 mm grid (`neutral100` xətlər, yalnız sürüşdürmə zamanı), kənar boşluğu kəsik xətt (`neutral400`). Bir neçə səhifə varsa (skanın hər səhifəsi ayrıca) kətanın altında "‹ Səhifə N / M ›" keçidi; boş səhifə yaradılmır (backend `layout.emptyPage`).
- *Element:* toxunuşla seçilir — `brand500` 2 px çərçivə, künclərdə 20 px dairəvi tutacaqlar, yuxarıda fırlatma tutacağı. Bir barmaq — sürüşdürmə; iki barmaq — ölçü (nisbət kilidi açıqdırsa nisbət saxlanır) və sərbəst fırlatma; tutacaq — ölçü. Seçilmiş elementin yanında ölçü yazısı ("85,6 × 54 mm", tabular). Boş yerə toxunuş seçimi götürür.
- *Snap:* kağız kənarına / kənar boşluğuna / mərkəzə və digər elementlərin kənarlarına / mərkəzinə 2 mm-dən yaxın olanda yapışır; yapışma xətti `brand500` nazik kəsik xətt kimi görünür. Fırlatma 0/90/180/270°-yə 3°-dən yaxın olanda yapışır.
- *Alət sırası* (seçim varsa, kətanın altında, üfüqi sürüşən `IconButton` + yazı): "90°" (`rotate_right`), "Real ölçü" (`straighten`; real ölçüsü bilinməyən şəkildə yoxdur), "Nisbət" (`link` / `link_off`, toggle), "Dublikat" (`content_copy`), "Ön plan" (`flip_to_front`), "Arxa plan" (`flip_to_back`), "Ortala" (`center_focus_strong`; yatay + şaquli), "Sil" (`delete_outline`, `danger`). Seçim yoxdursa sırada "Şəkil əlavə et" (yalnız fayl tərtibatında).
- *Xəbərdarlıq:* element çap sahəsindən (printer kənarı 4 mm) çıxanda onun çərçivəsi `warning500` olur və kətanın üstündə `warning` sətri: "Şəklin bir hissəsi printerin çatmadığı kənara düşür — çapda kəsilə bilər." Mərkəzi kağızdan kənara çıxan element saxlanmağa imkan vermir (backend `layout.offPage` qaydası): `danger` sətri və əsas düymə deaktiv.
- *Əsas düymə:* skan sessiyasında "Hazır" (yerləşmə skana qayıdır), fayl tərtibatında "Yadda saxla" — yeni tərtibatda əvvəl ad dialoqu ("Tərtibatın adı"), sonra saxlanılır; redaktədə dərhal yeni versiya. Saxlananda "Hazırlanır…"; uğur — snackbar "Tərtibat saxlanıldı" və Fayllarım; xəta — `danger` blok (nə oldu + "tərtibat saxlanılmadı" + nə etmək). Saxlanmamış dəyişikliklə geri çıxanda dialoq "Dəyişikliklər saxlanılmayıb" — "Çıx" / "Qal".

**Mobil: fayl paylaşımı (Addım 6, plan 8.2; `DECISIONS.md` 2026-10-05).**

- *Girişlər:* (1) faylın menyusunda "Paylaş" (`share_outlined`; yalnız `Ready` fayl, `file_sharing` flag-i açıqdırsa); (2) "Fayllarım" app bar-ında "Paylaşılanlar" (`move_to_inbox_outlined`), saxlanmamış alınan fayl varsa üstündə say badge-i; (3) `FileShared` bildirişi və push → "Paylaşılanlar", "Alınanlar" tab-ı; (4) `neoprint://share/NP-XXXXXX` linki (telefon kamerası ilə oxunmuş QR) → "Faylı paylaş", alıcı doldurulmuş, fayl seçilməmiş.
- *"Faylı paylaş" ekranı:* başlıq "Faylı paylaş". Yuxarıda fayl kartı (ad, "N səhifə", həssasdırsa "Şəxsi" chip-i); fayl yoxdursa kartın yerində "Fayl seç" (hazır faylların alt vərəqi). "Alıcının ID-si" sahəsi — böyük hərflə yazılır, `hint` "NP-7KQ2MX", altında `bodySmall` "Alıcı ID-sini Profil → ID bölməsində tapır"; sahənin sonunda QR ikonu (`qr_code_scanner`, yalnız telefonda) — skaner ekranı açılır və oxunan ID sahəyə yazılır, başqa QR-da skanerin "Bu NeoPrint ID kodu deyil" sətri. "Qeyd (istəyə görə)" sahəsi — bir sətir, sayğac `0/200`. Həssas faylda əsas düymənin üstündə `warning` blok: "Bu şəxsi sənəddir. Alıcı onu öz fayllarına saxlayıb çap edə bilər; göndərdikdən sonra geri almaq olmur." Əsas düymə "Göndər" (fayl və ID yoxdursa deaktiv deyil — sahənin altında nəyin çatmadığı yazılır). Həssas faylda əvvəl təsdiq dialoqu: başlıq "Şəxsi sənədi göndərək?", mətn alıcının ID-si ilə, düymələr "Göndər" / "İmtina". Uğur — snackbar "Fayl NP-… istifadəçisinə göndərildi", ekran bağlanır. Serverin ID xətaları (format, tapılmadı, özünüz) sahənin altında `error` mətni kimi, digərləri ekranın üstündə xəta bloku.
- *"Paylaşılanlar" ekranı:* iki tab — "Alınanlar" (default) və "Göndərilənlər". "Alınanlar"-ın başında kart "Sizin ID-niz": `NP-XXXXXX` (`titleMedium`, tabular), iki mətn düyməsi "Kopyala" (`copy_outlined`) və "QR kodum" (`qr_code_2`) — alt vərəqdə serverin QR şəkli (ağ fonda), altında "Fayl göndərmək istəyən bu kodu NeoPrint tətbiqində oxusun". Sətir: ikon (həssasdırsa `lock_outline`, yoxsa `description_outlined`), başlıq fayl adı, alt sətir "NP-… · tarix · N səhifə", qeyd varsa altında `bodySmall` dırnaqda. Status chip-i (rəng + ikon + mətn): *Alınanlar* — `Available` info `fiber_new_outlined` "Yeni", `Saved` success `check_circle_outline` "Saxlanılıb", `Gone` neutral `block` "Fayl artıq yoxdur", `Changed` warning `history` "Dəyişdirilib"; *Göndərilənlər* — `Available` neutral `schedule` "Gözləyir", `Saved` success "Alıcı saxlayıb", `Gone` neutral "Siz silmisiniz", `Changed` warning "Siz dəyişmisiniz". Boş siyahı: "Alınanlar" — "Sizə hələ fayl göndərilməyib" + "Fayl almaq üçün ID-nizi və ya QR kodunuzu göndərənə verin"; "Göndərilənlər" — "Hələ fayl göndərməmisiniz" + "Faylın menyusunda \"Paylaş\" seçin". Sürüşdürüb yeniləmə var.
- *Alınan faylın ekranı* (sətrə toxunanda): app bar-da fayl adı; yuxarıda göndərən, tarix və qeyd; ortada səhifə önizləməsi ("Fayllarım" baxışı kimi — sürüşdürülən səhifələr, "1 / N"); həssasdırsa `info` sətri "Şəxsi sənəd — saxlanılan nüsxə çapdan sonra silinir". Əsas düymə: `Available` — "Fayllarıma saxla" (saxlananda "Saxlanılır…"), uğurda snackbar "Fayllarınıza saxlanıldı" + "Aç"; `Saved` — "Fayllarımda aç". `Gone` / `Changed` — önizləmə yoxdur, xəta bloku nə olduğunu və nə etmək lazım olduğunu deyir ("Göndərəndən yenidən göndərməsini istəyin"), əsas düymə yoxdur. Kvota dolubsa serverin mesajı xəta blokunda.

**Mobil: dostunu dəvət et (referral, Addım 6, plan 2.6, 8.2).**

- *Girişlər:* Profil → "Dostunu dəvət et" (`card_giftcard_outlined`; `referral` flag-i açıqdırsa); `ReferralRewarded` bildirişi və push (`neoprint://referral`).
- *Ekran:* başlıq "Dostunu dəvət et". Yuxarıda izah kartı (`info` ton, `card_giftcard_outlined`): "Dostunuz kodunuzla qeydiyyatdan keçib ilk ödənişli çapını edəndə siz {N} bonus səhifə qazanırsınız." — dostun bonusu > 0 olanda davamı: "Dostunuz da {M} bonus səhifə alır." Altında `bodySmall`: "1 bonus səhifə = 1 çap olunan üz."
- *Kod kartı:* "Dəvət kodunuz", kod `headlineSmall` (tabular), yanında "Kopyala" (`copy_outlined`, snackbar "Kopyalandı"); altında link (`bodyMedium`, bir sətir, ellipsis) və "Linki kopyala"; sonra serverin QR şəkli (ağ fonda, ekran eninin 55%-i) və `bodySmall` "Dostunuz bu QR-ı telefonun kamerası ilə oxuya bilər". QR yüklənmədisə onun yerində xəta sətri + "Yenidən cəhd et" — kod və link yenə görünür.
- *Əsas düymə:* telefonda "Paylaş" (`share_outlined`) — sistemin paylaşım vərəqi (öz platform channel-ımız: Android `ACTION_SEND`, iOS `UIActivityViewController`), mətn: "NeoPrint ilə sənədlərini kioskda çap et. Qeydiyyatda dəvət kodumu yaz: {kod} — {link}". Web build-də sistem vərəqi yoxdur: əsas düymə "Linki kopyala"dır (kod kartındakı ikinci "Linki kopyala" göstərilmir).
- *Statistika kartı* ("Dəvətləriniz"): üç sətir, hər biri ikon + ad + sağda rəqəm (tabular) — "Qeydiyyatdan keçib" (`person_add_alt`), "İlk çapını edib, bonus yazılıb" (`check_circle_outline`, success), "İlk ödənişli çapı gözlənilir" (`schedule`); altında ayırıcı və "Qazandığınız bonus: {N} səhifə" (`titleMedium`). Heç kim qeydiyyatdan keçməyibsə sətirlərin yerində bir cümlə: "Hələ heç kim kodunuzla qeydiyyatdan keçməyib — kodu paylaşın."
- *Vəziyyətlər:* yüklənərkən mərkəzdə indikator; yüklənmədisə xəta bloku (nə oldu + "Yenidən cəhd et"); sürüşdürüb yeniləmə var.

**Admin: pul səhifələri (Addım 4).**

- *Siyahılar* (Ödənişlər, Refund-lar, Fiskal çeklər, Qəbzlər, Voucher-lər, Promokodlar, İstifadəçilər, Baxış gözləyən sifarişlər): standart səhifə şablonu (başlıq + izah → filtr sətri → cədvəl → səhifələmə), məbləğ sütunu sağa düzülür və tabular rəqəmlə yazılır, status həmişə chip ilə. Boş siyahı və "filtrə uyğun heç nə yoxdur" ayrı empty state-dir (ikincisində "Filtrləri təmizlə").
- *Tab-lı siyahı:* "Refund-lar" səhifəsində "Hamısı" və "Uğursuzlar" tab-ları; uğursuzlar tab-ının başlığında say (badge) var, sıfırdırsa badge göstərilmir.
- *Ödəniş detalı:* yuxarıda məbləğ (`H1`) + status chip + əsas əməliyyat ("Qaytar", `danger` deyil — `primary`, çünki pul müştəriyə qayıdır); altında kartlar: **Məbləğ bölgüsü** (sətir-sətir: qiymət, həcm endirimi, promokod, bonus səhifələr, balansdan, kart/bankdan — sıfır olan sətir göstərilmir; cəm qalın), **Sifariş** (kod, status, fayl, parametrlər — sifariş səhifəsinə link), **Müştəri** (anonimdirsə "Anonim müştəri" mətni), **Qəbz və fiskal çek** (nömrə, fiskal status chip), **Refund-lar** (hər biri: məbləğ, istiqamət, status chip, xəta mətni; uğursuzda "Təkrarla" və istiqamət seçimi), **Tarixçə** (timeline: vaxt sol sütunda tabular, hadisə adı tərcümə olunur, izah boz).
- *"Qaytar" dialoqu:* başlıq "Ödənişi qaytar"; məbləğ — "Tam (X ₼)" / "Hissəvi" radio, hissəvidə məbləğ sahəsi (maksimum qaytarıla bilən göstərilir); istiqamət radio-ları yalnız mümkün olanlardır (hesab varsa "Balansa" / "Karta", anonimdə "Karta" / "Voucher"); "Sifarişi də ləğv et" checkbox-u yalnız sifariş ləğv oluna bilirsə; səbəb sahəsi məcburi. Təsdiq düyməsi "X ₼ qaytar".
- *Bloklama dialoqu (İstifadəçilər):* `danger` təsdiq düyməsi "Blokla", səbəb məcburi, izah: "Hesab dərhal çıxış edir, balans saxlanılır."
- *Gəlir qrafiki* (dashboard kartı və hesabat): sütun qrafiki (chart.js), rənglər token-lərdən oxunur (`--brand-500` satış, `--danger-500` qaytarma; heç bir hex kodda yazılmır), oxlarda pul formatı (§6), tooltip-də gün + məbləğ. Qrafikin altında eyni rəqəmlər cədvəl kimi də var (rəng tək başına məna daşımır, ekran oxuyucu cədvəli oxuyur). Məlumat yoxdursa qrafik yerinə empty state.
- *Gəlir hesabatı:* yuxarıda 4 rəqəm kartı (Satış, Qaytarma, Net, Balans artırılması — sonuncunun altında "gəlir deyil" izahı), sonra qrafik, sonra "Üsullara görə" və "Kiosklara görə" cədvəlləri; "CSV yüklə" ikinci dərəcəli düymə.

**Admin: referral və istifadəçinin faylları (Addım 6, plan 8A.3).**

- *"Referral" səhifəsi* (`referrals.view`; naviqasiyada "Promokodlar"dan sonra, ikon `card_giftcard`): standart səhifə şablonu. Başlıq + izah ("Dəvət proqramı: kim kimi dəvət edib və bonus yazılıbmı"). **Statistika sətri** — dörd kart (`money__stats`, rəqəm tabular): "Qeydiyyatdan keçib", "Mükafatlandırılıb" (altında `caption` "Verilən bonus: N səhifə"), "İlk çapı gözləyir", "Bonus yazılmayıb" (altında `caption` "eyni cihaz, aylıq limit və ya bloklanmış dəvət edən"). **"Ən çox dəvət edənlər"** kartı — cədvəl: İstifadəçi (PublicId; `users.view` varsa istifadəçi detalına link), Dəvət, Mükafat, Bonus səhifə (rəqəmlər sağa düzülür); boşdursa bir cümlə. **Tarixçə** — tab-lar "Hamısı" / "Gözləyir" / "Mükafatlandırılıb" / "Bonus yazılmayıb" (tab-lı siyahı qaydası), cədvəl: Tarix, Dəvət edən, Dəvət olunan, Vəziyyət (status chip: `Rewarded` success "Mükafatlandırılıb"; `Registered` + həll olunmayıb → neutral "İlk çapı gözləyir"; `Registered` + səbəb → warning "Bonus yazılmayıb", altında `caption` səbəb: "Eyni cihaz" / "Aylıq limit" / "Dəvət edən aktiv deyil"), Bonus (dəvət edən + dəvət olunan səhifələr, yoxdursa "—" deyil, boş), səhifələmə. Boş tarixçə üçün empty state: "Hələ heç kim referral kodu ilə qeydiyyatdan keçməyib". İstifadəçilər burada PublicId ilə göstərilir — email yalnız istifadəçi detalındadır.
- *İstifadəçi detalı — "Referral" kartı:* "Dəvət edib: PublicId" (link) və ya "Dəvətsiz qeydiyyatdan keçib"; sonra onun dəvətlərinin sayğacı (qeydiyyat / mükafat / gözləyir, qazanılan bonus) və son 20 dəvət (dəvət olunanın PublicId-si link, tarix, vəziyyət chip-i). Dəvəti yoxdursa "Hələ heç kimi dəvət etməyib".
- *İstifadəçi detalı — "Fayllar" kartı:* yalnız metadata, **ad maskalanmış gəlir** (ilk iki hərf + `•••` + uzantı; həssas faylın adı tam gizli — `•••.pdf`), məzmuna və önizləməyə keçid yoxdur. Sətir: maskalanmış ad, altında `caption` "tip · ölçü · N səhifə · tarix", sağda mənbə chip-i (neutral: Yüklənib / Skan / Tərtibat / USB / Paylaşılıb), həssasdırsa warning chip "Həssas" (`lock`), hazır deyilsə status chip-i. Kartın başlığının altında `caption`: "Adlar qismən gizlədilir, faylın məzmunu paneldə göstərilmir." Son 50 fayl; daha çoxdursa altda "Son 50 fayl göstərilir (cəmi N)". Fayl yoxdursa "Faylı yoxdur".

**Kartlar.** `--neutral-0` fon, `--neutral-200` kənar, `--radius-md`, daxili boşluq 16 (kiosk 24).

**Cədvəllər (admin).** Sətir hündürlüyü 48 px, başlıq sabit (sticky), sıralama və filtr yuxarıda, pagination aşağıda. Status sütunu chip ilə. Sətirə klik → detal. Boş nəticə üçün boş vəziyyət bloku.

**Status chip.** Fon semantik `bg`, mətn semantik `500`, sol tərəfdə ikon, radius `full`, hündürlük 24 px (kiosk 40).

**Dialoqlar.** Başlıq (nə baş verir) + izah (nəticəsi nədir) + iki düymə (təsdiq sağda). Dağıdıcı əməliyyatda təsdiq düyməsi `danger` və səbəb sahəsi məcburidir.

**Toast / bildiriş.** Sağ yuxarı (admin), aşağı (mobil). Uğur 3 san, xəta əl ilə bağlanana qədər. Kritik incident-də səs + toast.

**Boş vəziyyət (empty state).** İkon + bir cümlə izah + əsas hərəkət düyməsi. "Məlumat yoxdur" tək başına yazılmır.

**Hazır olmayan funksiya.** Backend-i hələ olmayan (sonrakı addımda gələn) funksiyanın elementi ekranda **göstərilmir**: dəyər yerinə "—", deaktiv düymə, deaktiv menyu sətri, "tezliklə" tabı və ya boş kart yoxdur — istifadəçi yarımçıq və ya "xarab kimi görünən" element görmür. Element ya tamamilə gizlədilir (mobil: `lib/core/features/readiness.dart`, kiosk: `IsVisible`), ya da feature flag ilə bağlanır; funksiyanı gətirən addımda açılır. Real dəyər məlumdursa (məs. yeni hesabın balansı) o göstərilə bilər. Yoxlama: mobil `test/readiness_test.dart` (ekranda "—", deaktiv düymə və deaktiv sətir yoxdur).

**Söndürülmüş funksiya (mobil feature flag, T-148).** `GET api/features`-in söndürdüyü funksiyanın **yeni istifadə** giriş nöqtəsi "Hazır olmayan funksiya" kimi gizlənir (deaktiv düymə / "bağlıdır" yazısı yoxdur): fayl menyusunda "Kiosk üçün hazırla" / "Planla", kiosk kartında "Bu kioskda çap et", Planlanmışlar-da "Yeni planlama", ödəniş ekranında promokod sahəsi, profildə "Məlumatlarımı yüklə". İstifadəçinin artıq ödədikləri **görünməyə davam edir**: Planlanmışlar tabı aktiv planlama varsa, ana səhifədə "Mənim QR-ım" / "Hazırladıqlarım" siyahıda qeyd varsa qalır (QR ödənilmiş sifarişi götürmək üçündür); boşalanda gizlənir. Məlum olmayan açar və hələ yüklənməmiş siyahı "açıq" sayılır (backend-də olduğu kimi). Server `404 features.unavailable` qaytaranda tətbiq flag-ları yeniləyir və xəta üç hissəli mətnlə deyilir (nə oldu — funksiya bağlıdır; təsiri — heç nə yaradılmadı, pul tutulmadı; nə etmək — başqa yolla davam etmək), kioskun "tətbiqlə ödə" QR-ı üçün isə konkret yol (kioskda bank QR-ı ilə ödəmək). Flag-lar girişdə, tətbiq önə gələndə və belə xətadan sonra yenilənir. Yoxlama: mobil `test/feature_flags_test.dart`. **Kiosk (T-160):** konfiqurasiyanın `features`-i ilə eyni qayda — `mobile_payment_at_kiosk` sönəndə ekran 6-da "Tətbiqlə ödə" kartı yoxdur (yalnız bank QR-ı), `promo_codes` sönəndə seçimlər ekranında "Promokod" düyməsi yoxdur (artıq tətbiq olunmuş kod qalır və silinə bilər). Admin flag-ı saxlayanda bütün kiosklara `KioskConfigChanged` gedir, kiosk konfiqurasiyanı yeniləyir və açıq ekran dərhal dəyişir; açar yoxdursa "açıq". Şəxsi QR skanerlə işləyir (düymə yoxdur), kiosk QR-ı (plan 2.9) həmişə qalır.

**Yüklənmə.** Siyahı və kartlar üçün skeleton, tam ekran spinner yalnız ilk açılışda. 10 saniyədən uzun əməliyyatda mərhələ mətni göstərilir.

**Xəritə (admin).** Leaflet + OpenStreetMap, sağ aşağıda atribusiya (`© OpenStreetMap`) həmişə görünür. Marker status tonunun rəngində dairədir (rəng token-dən oxunur) və yanında həmişə mətn olur: xəritənin altında status legendası (rəng + ikon + mətn) və marker-ə klikdə ad + kod + status chip. Xəritə siyahının alternatividir, onu əvəz etmir — eyni filtrlər hər ikisinə tətbiq olunur. Nöqtə seçimi (kiosk forması) klik ilə olur, seçilən koordinat input sahələrində də görünür və əl ilə yazıla bilir.

**Uzun əməliyyatın proqresi (admin).** Progress bar + faiz + "X / Y" sayğac (obyekt və ölçü) + sürət + təxmini qalan vaxt + mərhələ mətni ("Kopyalanır", "Yoxlanılır"). Yanında canlı göstərici və son yenilənmə vaxtı. İdarəetmə düymələri (Dayandır / Davam et / Ləğv et) kartın altında; ləğv təsdiq istəyir. Bitəndə kart nəticəni yazır (uğur / xəta + nə etmək olar).

**Sərf səviyyəsi (admin, operator).** Hər lotok üçün kart: ad ("A4 kağızı") + iri mətnlə "X / Y vərəq" + nazik səviyyə zolağı (`--space-2` hündürlük, `--radius-sm`, fon `--neutral-100`). Zolaq `--success-500`, tutumun 20%-dən az qalanda `--warning-500`; say heç vaxt yalnız zolaqla verilmir — rəqəm həmişə yazılır. Kağız 0-dırsa zolağın altında xəta sətri (ikon + "nəyə təsir edir"). Tutumu 0 olan lotok üçün zolaq yox, izah cümləsi göstərilir (harada qurulur). Doldurma forması kartların altındadır: kağız ölçüsü və üsul (Əlavə et / Tam doldur / Dəqiq say) yan-yana seçim düymələri ilə, üsulun izahı altında, say sahəsi "Tam doldur"da gizlənir.

**Diqqət siyahısı (admin dashboard).** "Kioskların vəziyyəti" kartı dashboard-da birincidir (kritik kart). Yuxarıda status üzrə saylar (status chip + "Status: N"), altında diqqət tələb edən kiosklar: hər sətirdə kiosk kodu və adı (detala link) + **bir** səbəb chip-i — ən təcili olanı (təhlükəsiz rejim → əlaqə yoxdur → printer seçilməyib → kağız qurtarıb → servisdə → dayandırılıb → kağız azdır). Təcili səbəblər `danger`, qalanları `warning` tonundadır. Diqqət tələb edən yoxdursa uğur chip-i ("Bütün kiosklar qaydasındadır") göstərilir; söndürülmüş və aktivləşdirilməmiş kiosklar siyahıya düşmür.

**Xəta vəziyyəti.** Hər xəta üç şeyi deyir: nə baş verdi, nəyə təsir etdi, indi nə etmək olar. Texniki detal (stack trace, kod) istifadəçiyə göstərilmir; admin panelində detal "Ətraflı" altında gizlədilir və `CorrelationId` kopyalana bilir.

---

## 5. Tətbiqlərə görə layout

### 5.1 Kiosk (21", 1920×1080, landşaft, toxunma)
- Sabit karkas: yuxarıda header (loqo, kiosk adı, dil, saat), sağda queue paneli (280 px), aşağıda footer (kiosk QR + kod, qaynar xətt). Mərkəzdə dəyişən məzmun.
- Queue panelində hər iş bir kartdır: solda sıra (`#1`, brend rəngi), yanında bir sütunda yuxarıdan aşağı — sifariş kodu (qalın), səhifə sayı (`caption`), status (ikon + mətn bir sətirdə: çap olunur — printer ikonu, növbədə — saat ikonu). Status kodun yanına qoyulmur: 280 px-lik paneldə yan-yana sığmır və mətnlər üst-üstə düşür (T-122).
- **Dizayn səthi 1920×1080-dir.** Ekran başqa ölçüdə və ya Windows miqyası ilə (125%, 150%) işləyirsə, bütöv səth mütənasib kiçildilir və ya böyüdülür (`Viewbox`, `Uniform`), tərəflərin nisbəti fərqlidirsə kənarlarda fon qalır. Heç bir element kəsilmir və heç bir ekran sürüşdürmə tələb etmir — toxunma ekranında müştəri sürüşdürməni bilmir (T-097).
- Məzmun sahəsində eyni anda ≤ 7 seçim elementi. Uzun siyahılar səhifələnir, iri "Növbəti/Əvvəlki" düymələri ilə.
- Hər ekranda: başlıq (nə etməli), izah bir cümlə, əsas düymə sağ aşağıda, "Geri" sol aşağıda.
- Addım göstəricisi: Fayl → Baxış → Parametrlər → Ödəniş → Hazır.
- Geri sayımlar iri rəqəmlə və mətnlə ("Ödəniş üçün 4:32 vaxtınız var").
- **Etiket sütunu sabit rəqəmlə verilmir.** Ya sütun `Auto`-dur (ən uzun etiket qədər), ya da eni tokendəndir (`OptionLabelWidth`) və etiket `TextWrapping="Wrap"`-dir: ən uzun mətn rus dilindədir ("Страниц на листе", "Обнаруженные принтеры") və o da sığmalıdır.
- **Yan-yana kartlar eyni hündürlükdədir**, məzmun yuxarıdan başlayır (ödəniş üsulu kartları): bir dildə başlıq iki sətrə düşəndə kart qonşusundan uzun və aşağı dayanmır.
- **Ekran testi** (`ScreenRenderTests`, hər iki OS-də CI-da): hər ekran və sifariş axınının hər addımı 1920×1080-də az / en / ru dillərində çəkilir (33 ekran × 3 dil); heç bir mətn xam açar deyil, öz yerindən enli və ya hündür deyil, pəncərədən kənarda deyil. Şəkillər test nəticələri ilə birlikdə saxlanılır (`kiosk-screens`). Yeni ekran və ya addım əlavə olunanda bu testin siyahısına da yazılır.
- Xəta və dayanma ekranları tam ekran, qısa mətn, böyük ikon, lazım olduqda QR (refund statusu, məxfilik siyasəti).
- Dil seçimi hər ekrandan əlçatandır, seçim 90 saniyə sonra default dilə qayıdır.
- **Şəxsi QR ilə gələn fayllar (UserQrFiles, plan 9.4):** QR skaneri `NPU:` oxuyanda ana ekrandan açılır (ödəniş gedən ekranda oxuma nəzərə alınmır). Başlıq "Hazırladığınız fayllar", izah bir cümlə. Hər element bir kartdır: solda fayl ikonu, fayl adı (qalın) və bir sətirdə səhifə sayı + tətbiqdə seçilənlər (məs. "2 səhifə · ağ-qara"); ödənilmiş sifarişdə məbləğ və "Ödənilib" nişanı (success rəngi + ikon + mətn). Sağda kartın öz düyməsi: ödənişsiz — "Seç" (parametrlərə keçir, tətbiqdə seçilənlər qoyulur, qalanı kioskda seçilir), bu kiosk üçün ödənilmiş — "Çapa ver" (birbaşa növbəyə). Başqa kiosk üçün ödənilmiş kart boz, düyməsiz, səbəb mətni ilə ("KSK-0001 kioskunda ödənilib — yalnız orada çap olunur"). Siyahıda ≤ 5 kart, çox olarsa səhifələnir. Boş siyahı: ikon + "Kiosk üçün hazırlanmış faylınız yoxdur" + tətbiqdə nə etməli. Tətbiqdə seçilən imkanı bu kiosk vermirsə (rəng, A3, ikitərəfli), parametrlər ekranında səbəb göstərilir və kioskun edə bildiyi qoyulur. Ekran 60 saniyə toxunulmasa bağlanır, fayl adları ekranda qalmır. Addım göstəricisində "Fayl" bu ekrandır; "Baxış" addımı keçilmiş sayılır (server faylının önizləməsi kioskda yoxdur).

### 5.2 Mobil (Flutter)
- Aşağıda 5 bölməli naviqasiya. Ekran başlıqları böyük, kontent kartlarla. Tab etiketi bir sətirdir və 360 dp enində üç dildə də kəsilmədən sığır (təxminən 10 simvola qədər); bölmənin adı uzundursa tab üçün ayrıca qısa etiket yazılır (ekran başlığı tam ad qalır: "Planlanmışlar" → tabda "Planlı", "Запланированные" → "По плану").
- Ana səhifədə birinci kart: balans + əsas hərəkət ("Çap et"). Aktiv job varsa onun statusu ən yuxarıda.
- Formalar tək sütun, sahə başına bir sual; uzun formalar addımlara bölünür.
- Pul, tarix və status hər yerdə eyni formatda.
- Boş vəziyyətlər hər siyahı üçün (fayl yoxdur, qəbz yoxdur, planlanmış çap yoxdur).
- **Sətirdə (`Row`) mətn həmişə elastikdir** (`Expanded` / `Flexible`): dar ekranda və ya uzun dildə ikinci sətrə keçir və ya qısalır, ekrandan çıxmır. Status chip-i öz yerindən böyük mətni üç nöqtə ilə qısaldır; kartda chip sətrin ən çox 60%-ni tutur, qalanı ad üçündür.
- **Bir neçə idarəetmə elementi (seqment, filtr chip-i) sola düzlənir** və sığmayan ikinci sətrə keçir; blok ortalanmır (ikinci sətir birincinin altında "asılı" qalmasın).
- **Hər widget testi 360 × 800 dp ekranda çəkilir** (`test/flutter_test_config.dart`). Flutter daşmanı xəta kimi verir, test çərçivəsi də onu uğursuzluğa çevirir — yəni testdə çəkilən hər ekran dar telefonda sığmalıdır. Testin şrifti real şriftdən təxminən 1,8 dəfə enlidir, ona görə 360 dp-də keçən ekran 40% uzun (rus) mətnə də dözür. Başqa ölçü lazım olan test onu özü qoyur.
- Dark tema tam dəstəklənir; şəkil və QR blokları dark temada ağ fon üzərində göstərilir (skan oluna bilməsi üçün).

### 5.3 Admin panel (Angular Material)
- Sol sidebar (icazəyə görə menyu, qruplaşdırılmış), yuxarıda axtarış + dil + bildiriş zəngi + profil.
- Səhifə şablonu: başlıq + qısa izah → filtr paneli → cədvəl → pagination. Detal səhifələri tab-larla.
- *Cədvəl sətrində qeyd* (`.neo-row-note`): xananın əsas məzmununun (məs. status nişanının) altında ikinci sətir — sətir haqqında statusun özü olmayan məlumat, məs. kiosklar siyahısında "Sayğac təyin edilməyib" (T-264). İkon + mətn, `--font-size-caption`, ikon `--icon-size`; xəbərdarlıq variantı (`.neo-row-note--warning`) `--warning-500` rəngində, ikon `warning`. Bir sətirdə ən çox bir qeyd; düymə və keçid daşımır — əməliyyat detal səhifəsindədir.
- Dashboard kartları: rəqəm + izah + trend; kritik kartlar (incident, uğursuz refund) yuxarıda və semantik rənglə.
- Real-time yenilənən sahələrdə "canlı" göstəricisi və son yenilənmə vaxtı. Admin paneldə göstərici başlıqdadır (`app-live-indicator`: Canlı / Yenidən qoşulur / Əlaqə yoxdur). Serverin özü dəyişdiyi məlumatı göstərən hər səhifə `liveRefresh` ilə qurulur: hadisə gələndə, bağlantı qayıdanda və canlı bağlantı olmadıqda hər 30 saniyədən bir məlumatı **səssiz** təzələyir — cədvəl boşalmır, skeleton görünmür, uğursuz cəhd xəta banneri göstərmir (bağlantının vəziyyətini göstərici deyir). "Yenilə" düyməsi tələb edən canlı səhifə qüsur sayılır (T-248). Bir neçə səhifənin eyni anda göstərdiyi canlı məlumat — kioskun statusu, açıq incident-lərin sayı — səhifədə saxlanmır, store-dan oxunur (`KioskLiveStore`, `IncidentLiveStore`): siyahı, dashboard və detal başlığı eyni anda eyni şeyi deyir (T-254). Hub hadisəsinin adı səhifədə sətir kimi yazılmır — `HubEvents`-dən götürülür (T-252).
- **Canlı səhifədə saxlanmamış dəyişiklik ola bilərsə** (məs. "Funksiya açarları") səhifə özü təzələnmir: başqası dəyişəndə `.neo-notice` göstərilir — `info` fonu, `info` ikonu, bir cümlə ("başqa yerdə dəyişdirilib; yeniləsəniz saxlamadığınız dəyişikliklər qalır") və bir ikinci dərəcəli "Yenilə" düyməsi. "Yenilə" serverin dəyərlərini gətirir, istifadəçinin saxlamadığı dəyişiklikləri saxlayır. `.neo-notice` xəta deyil: xəta üçün `app-error-banner`, sahə xətası üçün `.neo-field-error` qalır. Eyni blok düyməsiz də işlənir — bilinməli, amma xəta olmayan vəziyyət üçün (məs. "Sayğac təyin edilməyib", T-258).
- **Hər səhifənin başlığı `app-page-header`-dir** (idarə paneli də): başlıq ölçüsü, izah və altındakı boşluq bir yerdən gəlir. Səhifənin kökü `.neo-page`-dir (və ya onun bərabəri — sütun, `--space-6` aralıq).
- **Detal səhifəsində "siyahıya qayıt" keçidi** səhifənin ən üstündə, solda dayanır (`.neo-back`), başlıqdan əvvəl. Mərkəzdə və ya başlığın altında olmur.
- **Yan menyu:** sığmayan ad ikinci sətrə keçir, üç nöqtə ilə kəsilmir ("Baxış gözləyən sifarişlər", "Переключатели функций").
- **Cədvəldə bölünməyən dəyərlər:** kod (`KSK-9002`, sifariş kodu), tarix + saat və məbləğ bir sətirdə qalır (`.neo-nowrap`); yer azdırsa ad və ünvan sütunu daralır, onlar yox.
- **Serverin daxili dəyəri ekrana çıxmır:** `MobileApp`, `Mixed`, `none`, `az` kimi dəyərlər həmişə lüğətdəki adı ilə göstərilir ("NeoPrint tətbiqi", "Qarışıq", "Azərbaycan"); heç nə demirsə ("none") göstərilmir.
- **Səhifəyə aid açıq incident-lər** (məs. Fiskal çeklər, T-129): başlığın dərhal altında, səhifənin başqa kartlarından (parametrlər, filtr) əvvəl ayrıca kart. Kartın sol kənarı ciddiliyin rəngindədir (Critical/High — `danger`, Medium — `warning`). Başlıqda "Açıq incident-lər" və say göstərilir. Hər sətirdə:
  - ciddilik chip-i (rəng + ikon + mətn);
  - nə baş verib və nə etmək lazımdır (incident mətni);
  - nə vaxtdan;
  - sağda bir hərəkət düyməsi (`mat-stroked-button`, məs. "Təkrar cəhd"). Səhifənin primary düyməsi olmadığı üçün `stroked`-dur.

  Açıq incident yoxdursa kart göstərilmir: aşağıdakı siyahının öz empty state-i var. Kartı yalnız incident-ləri görmək icazəsi olan görür. Hərəkət düyməsi yalnız müvafiq icazə ilə görünür.

### 5.4 Operator paneli (mobil brauzer / PWA)
- Tək sütun, iri kartlar, baş barmaqla çatan zonada əsas düymələr.
- Hər kiosk kartı: status rəngi + kağız/toner göstəricisi + bir əsas hərəkət ("Kağız doldur").
- Ən çox 2 toxunuşla əsas əməliyyat tamamlanır.
- Zəif internetdə keşdən göstərilən məlumat "Son yenilənmə: HH:mm" etiketi ilə işarələnir.
- **Çərçivə (`/operator`, T-211):** yuxarıda zolaq — səhifənin adı, canlı göstərici (`app-live-indicator`), hesab menyusu (dil, tam panelə keçid — icazə varsa, çıxış). Məzmun tək sütundur, eni ən çox 640 px, kənar boşluq `--space-4`. Aşağıda dörd təyinatlı naviqasiya: Kiosklar, Tapşırıqlar, Incident-lər, Anbar — hər biri ikon + mətn, seçilən `primary`; gözləyən iş varsa ikonun üstündə say nişanı. Naviqasiya ekranın altına yapışır və məzmunu örtmür (məzmunun alt boşluğu naviqasiyanın hündürlüyü qədərdir).
- **Kiosk kartı:** `.neo-card`; birinci sətir kod + ad və sağda status nişanı; altında göstərici sətirləri — hər biri ikon + mətn (printer, A4 / A3 kağız zolağı, toner faizləri, növbə, son əlaqə). Diqqət tələb edən səbəb (alert, incident, təyin edilməmiş sayğac) kartın yuxarısında ton rəngi + ikon + mətnlə yazılır — rəng tək başına işlənmir. Kartın özü detalı açır; kartda yalnız bir əsas düymə olur ("Kağız doldur").
- **Düymələr:** toxunma hədəfi ≥ 48 px (`--touch-target`, dar ekranda özü böyüyür). Səhifənin əsas düyməsi tam enlidir; ikinci dərəcəli əməliyyatlar onun altında iki sütunlu şəbəkədə `stroked` düymələrdir. Geri qaytarılmayan əməliyyat (yenidən başlatma, incident-in bağlanması) təsdiq dialoqu ilə gedir.
- **Dialoqlar:** telefonda tam enli (kənardan `--space-4`), bir sual — bir dialoq; sahələr tək sütunda; düymələr aşağıda sağda: ləğv (mətn) + əsas (dolu).
- **Oflayn:** səhifə son uğurlu cavabı dərhal göstərir və altında "Son yenilənmə: HH:mm" yazır. Yeni cavab alınmayanda `.neo-notice` — "Əlaqə yoxdur. Göstərilən məlumat HH:mm-dəkidir; əməliyyatlar əlaqə qayıdanda mümkün olacaq" — və əməliyyat düymələri deaktiv olur. Keş hesaba bağlıdır və çıxışda silinir.
- **Bildirişlər:** yeni tapşırıq, `Critical` alert və incident üçün qısa səs + brauzer bildirişi — yalnız istifadəçi icazə verəndən sonra. İcazə soruşulmayıbsa "Tapşırıqlar" səhifəsinin yuxarısında `.neo-notice` + "Bildirişləri aç" düyməsi; imtina olunubsa panel təkid etmir.

### 5.5 Qəbz PDF-i (backend, QuestPDF)
- **Token mənbəyi:** `backend/src/NeoPrint.Infrastructure/Receipts/ReceiptTokens.cs` — bu sənədin **light** dəyərləri (kağız həmişə ağdır): mətn `--neutral-900`, ikinci dərəcəli mətn `--neutral-600`, ayırıcı `--neutral-200`, sətir fonu `--neutral-50`, vurğu (başlıq xətti, cəm) `--brand-600`, "Fiskal çek hazırlanır" `--warning-500`. Renderer rəngi, ölçünü, boşluğu yalnız oradan götürür.
- **Şrift:** Inter (mobil tətbiqin eyni faylı, resurs kimi daxil edilir). Ölçülər çap üçün pt ilə: başlıq 16, bölmə başlığı 11 (600), mətn 10, köməkçi 8 (heç bir mətn 8 pt-dən kiçik deyil). Rəqəmlər sağa düzülür.
- **Səhifə:** A4, kənarlar 32 pt; boşluqlar 4 əsaslı şkala (4, 8, 12, 16, 24, 32).
- **Quruluş (yuxarıdan aşağı):** loqo-mətn "NeoPrint" + "Qəbz" başlığı və nömrəsi → tarix, kiosk, sifariş kodu, fayl → xidmət sətirləri (izah + məbləğ) → **Cəm** (vurğu rəngi) → ödəniş üsulları (bonus səhifələr "0 AZN fiskal" izahı ilə) → fiskal blok: verilibsə nömrə, fiskal ID, terminal və yoxlama QR-ı (ağ fon, quiet zone), verilməyibsə xəbərdarlıq rəngində "Fiskal çek hazırlanır" → alt qeyd "Bu sənəd fiskal çek deyil".
- Status yalnız rənglə verilmir: "Fiskal çek hazırlanır" mətn + işarə (`!`) ilə yazılır.
- Pul formatı §6-dakı kimidir (dilə görə ayırıcı, `₼`). Mətnlər server kataloqundadır (`receipt.*` açarları), PDF qəbzin dilində yazılır (hesabın dili və ya kioskun standart dili).

### 5.6 İctimai refund status səhifəsi (backend, `{site}/refund/{id}`)
- Kiosk ekranındakı refund QR-ı bu səhifəni açır; müştəri telefondadır — **mobil-birinci**, tək sütun, maks. en 480 px, kənar boşluq 16.
- **Token mənbəyi:** `backend/src/NeoPrint.Api/PublicPages/PublicPageTokens.cs` — bu sənədin light dəyərləri CSS custom property kimi (`--neutral-*`, `--brand-*`, `--success-*`, `--warning-*`, `--danger-*`, `--info-*`); səhifə şablonunda literal rəng/ölçü yoxdur. Şrift: `system-ui` yığını (Inter fallback-i, §2) — ictimai səhifə xarici şrift yükləmir.
- **Quruluş:** "NeoPrint" + başlıq → **status kartı** (ikon + status mətni + semantik rəngli sol xətt; rəng tək başına deyil) → məbləğ (iri, tabular rəqəm), hara qaytarılır, tarixlər → statusa görə bir cümlə izah ("Bankınızdan asılı olaraq 1–3 iş günü" / voucher kodu iri və seçilə bilən) → "Problem barədə bildir" forması (mətn + istəyə görə əlaqə; bir əsas düymə) → dil keçidi (az / en / ru).
- Şəxsi məlumat göstərilmir (kart nömrəsi, ad, email yoxdur). Səhifə keşlənmir. Mətnlər server kataloqundadır (`page.refund.*`), dil `?lang=` və ya brauzerin dilindən.
- Xəta vəziyyətləri: tapılmayan refund — "Belə qaytarma tapılmadı" + QR-ı yenidən oxutmaq tövsiyəsi; form göndərilə bilmədisə nə baş verdiyi və nə etmək lazım olduğu yazılır.

---

## 6. Məzmun və format qaydaları

- **Pul:** `12,50 ₼` (vergül onluq ayırıcı, min ayırıcısı boşluq: `1 240,00 ₼`).
- **Dilə görə format** — hər tətbiqdə bir mərkəzi helper (admin `shared/format.pipes.ts`, mobil `core/format/formats.dart`, kiosk `Localization/Formats.cs`); şablon/widget öz formatını yazmır:

  | | az | en | ru |
  |---|---|---|---|
  | Tarix + saat | `27.09.2026 18:33` | `27.09.2026 18:33` | `27.09.2026 18:33` |
  | Bu gün / dünən | `Bu gün 14:20` / `Dünən 14:20` | `Today 14:20` / `Yesterday 14:20` | `Сегодня 14:20` / `Вчера 14:20` |
  | Pul | `1 240,00 ₼` | `1,240.00 ₼` | `1 240,00 ₼` |
  | Ölçü, say | `1,5 MB`, `12 480` | `1.5 MB`, `12,480` | `1,5 MB`, `12 480` |

  Tarix hər dildə eyni rəqəm formatındadır (ay/gün qarışıqlığı olmasın, 24 saat); dilə görə yalnız sözlər və onluq/min ayırıcısı dəyişir.
- **Tarix:** `dd.MM.yyyy`, **saat:** `HH:mm` (24 saat). Bu gün/dünən üçün nisbi yazı ("Bu gün 14:20").
- **Səhifə/vərəq:** "12 səhifə", "6 vərəq" — qısaltma işlədilmir.
- **Mətn tonu:** sadə, birbaşa, "siz" formasında. Texniki termin əvəzinə gündəlik söz ("əlaqə yoxdur" ≠ "connection timeout").
- **Xəta nümunəsi:** «Ödəniş tamamlanmadı. Məbləğ kartınızdan çıxılmayıb. Yenidən cəhd edin və ya başqa ödəniş üsulu seçin.»
- **Lokalizasiya:** az (default), en, ru. Layout mətnin 40% uzanmasına dözməlidir; düymələrdə mətn kəsilmir, sətir aşağı keçir. Heç bir mətn koda yazılmır.
- **Token yoxlaması** (`tools/design-lint`, CI-da "design rules" job-u): admin SCSS-də `padding`/`margin`/`gap`/`font-size`-da literal uzunluq (`px`/`rem`/`em`) və rəng literalı; mobil Dart-da rəqəmli `EdgeInsets`, boşluq kimi `SizedBox(height|width: N)`, `fontSize: N`, `Color(0x…)`/`Colors.*`; kiosk AXAML-da rəqəmli `Padding`/`Margin`/`Spacing`, `FontSize`, `#rəng` və adlı rəng. Token faylları istisnadır. Əsaslı istisna eyni sətirdə `design-lint: allow <səbəb>` ilə yazılır. Ölçülər (en, hündürlük, ikon qutusu, border qalınlığı) boşluq deyil və yoxlanmır.
- **Kiosk Thickness token-ləri:** Avalonia `Margin`/`Padding`-də `Thickness` tələb edir, ona görə `Space*` şkalasından hazır formalar var: `Inset4/6`, `InsetX4/X8` (sol-sağ), `GapStart4/6/8/12` (öncəki boşluq), `GapBelow4`, `CodeBoxPadding` (aktivləşdirmə kodu). Kioskda ən kiçik boşluq 16-dır — 4/8/12 dəyərləri `Space4`-ə qaldırıldı. Aktivləşdirmə kodunun ölçüsü `FontSizeCode` (72); QR-ın ağ fonu və quiet zone funksional token-lərdir (`QrBackground`, `QrQuietZone`) — mövzudan asılı olmayaraq ağdır, çünki skaner kontrast tələb edir.
- **Tərcümə yoxlaması** (hər üç tətbiqin test dəstində, CI-da): hər açar üç dildə var və boş deyil; placeholder-lər eynidir; ru mətnində kiril hərfi var; en mətnində Azərbaycan hərfi və kiril yoxdur; az mətni en ilə eyni deyil (brend/qısaltma istisnası: NeoPrint, Email, PDF, QR, PIN, USB, Kiosk, Printer, Status…); az mətnində ingilis texniki sözləri yoxdur — `token`, `flag`, `feature`, `enrollment`, `dashboard`, `default`, `override`, `rollout`, `provisioning`, `endpoint`, `mirror`, `primary`, `secondary`, `hash`, `job`, `queue`, `upload`, `download`, `error`, `network`. Qarşılıqlar: qeydiyyat açarı, funksiya açarı, idarə paneli, standart, fərdi ayar, açılma faizi, quraşdırma, server ünvanı, güzgü, əsas / ehtiyat, sifariş, növbə. Yoxlama: admin `translations.spec.ts`, mobil `test/l10n_test.dart`, kiosk `StringsQualityTests`, backend `MessagesTests` (server mesajları kataloqu).
- **Xam açar yoxlaması** (admin, `node scripts/i18n-check.mjs`, CI-da "Translation keys" addımı): şablonda və komponentdə yazılmış hər açar lüğətdə var; serverdən gələn dəyərdən yığılan hər açar ailəsi (`'jobs.status.' + job.status`) skriptin `SERVER_VALUES` cədvəlində mənbəyi ilə yazılıb (backend enum-u, ayarlar, funksiya açarları) və o mənbənin **hər** dəyərinin tərcüməsi var. Backend-ə yeni enum üzvü və ya yeni ayar əlavə olunanda panel onu adlandıra bilməyənə qədər CI qırmızıdır.
- **Render testi** (admin, `npm run e2e`, CI-da "Rendered pages"): yığılmış panel real brauzerdə açılır, API cavabları `e2e/fixtures/api.json`-dan oxunur (backend lazım deyil). Hər səhifə az / en / ru dillərində: ekranda xam açar yoxdur, başlıq var, "yüklənmədi" banneri yoxdur, 1366 px-də yana sürüşmə yoxdur; hər səhifənin şəkli saxlanılır (CI-da `admin-pages` artefaktı). Səhifə API-dən yeni bir şey istəyəndə test bunu deyir və cavablar yenidən yazılır: `npm run e2e:record` (lokal stack lazımdır).
- **Server mesajları** (xəta, validasiya, fayl emalı səbəbi, email) koda yazılmır: `backend/src/NeoPrint.Application/Localization/messages.{az,en,ru}.json`. Kod yalnız açar + arqument verir (`new ValidationError("pages", "files.pageMissing", ("count", n), ("page", p))`), mətni sorğunun `Accept-Language` dili seçir. Test koddakı hər açarın kataloqda olduğunu və kodda Azərbaycan dilində cümlə qalmadığını yoxlayır. Admin paneli seed məlumatını (tənzimləmə izahı, funksiya açarı adı, icazə qrupu) öz tərcümələri ilə göstərir.

---

## 7. Əlçatanlıq

- Klaviatura ilə tam naviqasiya (admin), görünən fokus halqası.
- Ekran oxuyucular üçün etiketlər (`aria-label`, Flutter `Semantics`).
- Toxunma sahələri minimumdan kiçik olmur.
- Animasiya azaldılması sistem seçiminə tabedir.
- Kioskda mətn ölçüsünü artırmaq üçün "A+" düyməsi (bir pillə, 125%).

---

## 8. Tətbiq qaydası (Claude Code üçün)

- Token-lər bir mənbədən: `admin/src/styles/tokens.css`, `mobile/lib/core/theme/tokens.dart`, `desktop/src/NeoPrint.Kiosk/Styles/Tokens.axaml`. Dəyər dəyişəndə üçü də yenilənir (mənbə: bu sənəd). Qəbz PDF-i light dəyərləri `backend/src/NeoPrint.Infrastructure/Receipts/ReceiptTokens.cs`-dən götürür (§5.5) — light rəng dəyişəndə o da yenilənir.
- Hardcoded rəng, şrift ölçüsü və boşluq qadağandır; kod review-da bu yoxlanılır.
- Angular Material teması token-lərdən qurulur (`mat.define-theme`), komponentlər ayrıca stillə əzilmir.
- Flutter-də `ThemeData` + `ThemeExtension<NeoPrintTokens>`; widget-lərdə birbaşa `Color(0x...)` yazılmır.
- Avalonia-da `Tokens.axaml` + `ControlTheme`; inline `Background="#..."` yazılmır.
- Yeni komponent yaradılanda əvvəl bu sənədə qayda əlavə olunur, sonra kod yazılır.
- Dizayn dəyişikliyi davranışı və mövcud testləri pozmamalıdır.
