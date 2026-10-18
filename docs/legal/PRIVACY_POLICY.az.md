# Məxfilik siyasəti

> **QARALAMA — hüquqşünas təsdiqi tələb olunur.**
> Bu mətn Bölmə 14-ə uyğun olaraq sistemin texniki davranışını əks etdirir.
> Yekun hüquqi formulalar, operator qeydiyyatı və saxlama müddətləri hüquqşünasla
> dəqiqləşdirilməlidir. Dərc edilmiş versiya admin panelin "Hüquqi sənədlər"
> bölməsindən idarə olunur.

**Versiya:** 0.1-draft
**Qüvvəyə minmə tarixi:** dərc edildikdə

---

## 1. Kim məlumat toplayır

NeoPrint platforması (bundan sonra "biz") çoxfunksiyalı terminal (kiosk) printer
xidməti göstərir. Fərdi məlumatların operatoru barədə rekvizitlər yekun versiyada
göstəriləcək.

## 2. Hansı məlumatları toplayırıq

| Kateqoriya | Nə | Nə üçün |
|---|---|---|
| Hesab məlumatları | Email, ad (istəyə görə), şifrənin hash-i | Qeydiyyat, giriş, qəbzlərin göndərilməsi |
| Cihaz məlumatları | Cihaz identifikatoru, push bildiriş token-i | Təhlükəsizlik, bildirişlər, referral sui-istifadəsinin qarşısının alınması |
| Fayllar | Çap üçün yüklədiyiniz sənədlər və skanlar | Yalnız çap xidmətinin göstərilməsi |
| Ödəniş məlumatları | Məbləğ, metod, provayder istinadı, qəbz | Xidmətin ödənişi, qanuni uçot |
| Məkan | Yaxınlıqdakı kioskları tapmaq üçün cihazın məkanı | Yalnız siz icazə verdikdə və yalnız axtarış anında |
| Analitika | Ekran keçidləri, xəta hadisələri (anonim kiosk istifadəsində şəxsi identifikator olmadan) | Xidmətin yaxşılaşdırılması |

**Kart məlumatlarını saxlamırıq.** Kart ödənişləri ödəniş provayderi tərəfindən emal olunur.

## 3. Anonim kiosk istifadəsi

Flash kartdan çap edərkən hesab tələb olunmur. Sənədiniz **kioskda lokal, şifrəli
şəkildə** emal olunur və çapdan (və ya sessiyanın bitməsindən) sonra **dərhal silinir**.
Serverə yalnız metadata (səhifə sayı, format, məbləğ) göndərilir.

İstisna: NeoPrint mobil tətbiqi ilə ödəsəniz, sənəd və qəbz sizin hesabınıza əlavə olunur —
bu, ödəniş anında ekranda bildirilir.

## 4. Şəxsi sənəd skanları

Şəxsiyyət vəsiqəsi, sürücülük vəsiqəsi, pasport kimi sənədlərin skanları **həssas**
işarələnir. Bu sənədlər üçün:

- Məzmun **tətbiq səviyyəsində şifrələnir** (AES-256-GCM envelope encryption).
- Default olaraq **çapdan sonra avtomatik silinir**; bu davranışı hər fayl üçün dəyişə
  bilərsiniz (`Heç vaxt` / `Çapdan sonra` / `N gün sonra`).
- İlk skan zamanı bu barədə ayrıca məlumat verilir və razılığınız qeyd olunur.

## 5. Fayllarınıza kimin çıxışı var

- **Yalnız siz.** Fayl paylaşdığınız halda — əlavə olaraq paylaşdığınız istifadəçi.
- **Admin işçilərimizin fayl məzmununa çıxışı yoxdur.** Admin panelində yalnız metadata
  (ölçü, tip, tarix, maskalanmış ad) görünür; endirmə və ya baxış imkanı sistemdə
  **mövcud deyil**.
- Bütün obyektlər saxlama serverinə yazılmazdan əvvəl şifrələnir; birbaşa saxlama
  serverindən götürülən fayl oxunmur.

## 6. Nə qədər saxlayırıq

| Məlumat | Müddət |
|---|---|
| Anonim kiosk faylları | Çap bitən kimi silinir |
| İstifadəçi faylları | Siz silənə qədər, və ya seçdiyiniz avtomatik silinmə qaydasına görə |
| Çevrilmiş preview / çap PDF-ləri | 24 saat |
| Hesab məlumatları | Hesab aktiv olduqca |
| Maliyyə qeydləri (ödəniş, refund, fiskal çek) | Qanunla tələb olunan müddət — **anonimləşdirilmiş şəkildə** |
| Analitika hadisələri | 180 gün |

## 7. Hüquqlarınız

- **Məlumatlarımı yüklə** — profil, ödəniş tarixçəsi, qəbzlər və fayllar ZIP arxivi kimi.
- **Hesabımı sil** — hesab dərhal deaktiv olur; bütün fayllar, skanlar və versiyalar
  24 saat ərzində bütün saxlama serverlərindən **fiziki olaraq** silinir, profil
  anonimləşdirilir.
- **Düzəliş** — profil məlumatlarını tətbiqdən dəyişə bilərsiniz.

> **Diqqət:** hesab silinərkən **balansınız və bonus səhifələriniz itir və geri
> qaytarılmır** (İstifadə şərtləri, bənd "Balans").

## 8. Təhlükəsizlik

- Bütün saxlanılan obyektlər AES-256-GCM ilə şifrəlidir; açarlar ayrıca secret
  idarəetmə sistemində saxlanılır və dövri olaraq dəyişdirilir.
- Şəbəkə üzərindən ötürülmə HTTPS ilə qorunur.
- Log-larda şifrə, token, fayl məzmunu və şəxsi məlumat yazılmır.

## 9. Üçüncü tərəflər

- **Ödəniş provayderi** — ödənişin emalı üçün.
- **Fiskal (e-kassa) provayderi** — qanunla tələb olunan çeklərin verilməsi üçün.
- **Push bildiriş xidməti** — bildirişlərin çatdırılması üçün.
- **Xəritə xidməti (OpenFreeMap)** — kiosklar xəritəsini açanda xəritə hissələri (tile-lar) birbaşa
  `tiles.openfreemap.org`-dan yüklənir; bu zaman cihazınızın **IP ünvanı** və brauzer / tətbiq
  məlumatı OpenFreeMap-ə və onun CDN-i **Cloudflare**-ə çatır. Hesabınız, adınız və ya
  yeriniz xəritə xidmətinə göndərilmir. Xəritəni açmasanız, bu sorğu getmir. *(Qaralama qeydi,
  T-286: hüquqşünas yoxlayır — T-162.)*

Üçüncü tərəf analitika və reklam sistemlərindən **istifadə etmirik**.

## 10. Əlaqə

Fərdi məlumatlarla bağlı müraciətlər üçün əlaqə vasitələri yekun versiyada göstəriləcək.
