# NeoPrint — Pulun qaytarılması (refund) siyasəti

> **QARALAMA — hüquqşünas təsdiqi tələb olunur.**
>
> Bu mətn sistemin **texniki davranışını** sadə dillə izah edir və hüquqi
> baxımdan yoxlanılmayıb. Xüsusən aşağıdakılar hüquqşünasla dəqiqləşdirilməlidir
> (plan Bölmə 14):
>
> - istehlakçı hüquqları qanunvericiliyinin tələb etdiyi **maksimum cavab və
>   qaytarma müddətləri** — buradakı 3 və 10 iş günü yalnız təklifdir;
> - çapın olub-olmaması naməlum qaldıqda **sübut yükünün kimdə olduğu**;
> - voucher-in qanuni pul əvəzedicisi sayılıb-sayılmaması;
> - balansın geri qaytarılmaması bəndinin formulası.
>
> Texniki tərəf: plan Bölmə 2.5 və `docs/FAILURE_RECOVERY.md`.

---

## 1. Qısa xülasə

| Nə baş verib | Nə edirik |
|---|---|
| Printer çap edə bilmədi (kağız, toner, sıxışma, yarımçıq çap) | **Pulu özümüz qaytarırıq**, siz heç nə etmirsiniz |
| Terminal işləmədi (elektrik kəsildi, şəbəkə itdi, avadanlıq sıradan çıxdı) və çapın olub-olmadığı bilinmir | **Müraciətinizə baxırıq** və nəticəyə görə qərar veririk |
| Siz sifarişi özünüz ləğv etdiniz | Pul qaytarılır |
| Balansı (hesabınızdakı pulu) geri istəyirsiniz | Balans nağd və ya karta **qaytarılmır** |

---

## 2. Printer xətası — pul avtomatik qaytarılır

Terminal işləyirdi, bizim sistemə xətanı özü bildirdi: kağız qurtardı, kağız
sıxışdı, toner bitdi, çap yarımçıq qaldı və ya printer cavab vermədi.

Bu halda **heç bir müraciət lazım deyil**:

- **Kartla və ya bank QR-ı ilə ödəmisinizsə:** məbləğ avtomatik olaraq həmin
  karta qaytarılır. Bankınızdan asılı olaraq hesabınızda görünməsi adətən
  **1–3 iş günü** çəkir.
- **NeoPrint balansı ilə ödəmisinizsə:** məbləğ dərhal balansınıza qayıdır.
- **Bonus səhifə istifadə etmisinizsə:** bonus səhifələr geri qaytarılır.
- **Yalnız bir hissəsi çap olunubsa:** çap olunmamış hissənin dəyəri qaytarılır.

Terminal ekranında qaytarılan məbləği və qaytarma vəziyyətini izləmək üçün bir
**QR kod** göstərilir. Eyni QR qəbzinizdə də olur. İstəsəniz email ünvanınızı
daxil edib qəbzi və qaytarma məlumatını poçtla ala bilərsiniz.

Əgər bankla texniki problem səbəbindən pul karta qayıtmasa, sizinlə əlaqə
saxlayırıq və məbləği ya NeoPrint balansınıza yazırıq, ya da eyni məbləğdə
**voucher** (kod) veririk. Voucher-in etibarlılıq müddəti **90 gündür**.

---

## 3. Texniki fasilə — müraciət əsasında araşdırma

Bəzən terminal ilə bizim sistem arasındakı əlaqə kəsilir: elektrik gedir,
internet itir və ya kompüter sıradan çıxır. Belə hallarda **sənədinizin çap
olunub-olunmadığını sistem bilmir**.

Bu vəziyyətdə pulu avtomatik qaytarmırıq. Səbəbini açıq yazırıq: çap əslində
baş tutubsa avtomatik qaytarma haqsız olardı, baş tutmayıbsa qaytarmamaq
haqsız olardı. Ona görə **hər halı ayrıca araşdırırıq**.

Nə baş verir:

1. Sifarişiniz "baxış gözləyən" siyahısına düşür və işçilərimizə bildiriş gedir.
2. Terminal yenidən işə düşəndə öz qeydlərini bizə göndərir. Əgər qeydlərdən
   çapın **baş tutduğu** görünürsə sifariş bağlanır; **baş tutmadığı** görünürsə
   pul yuxarıdakı qaydada **avtomatik** qaytarılır. Bu, adətən terminal bərpa
   olunan kimi, bir neçə dəqiqə ərzində baş verir.
3. Terminal bərpa olunmazsa, müraciətinizə əsasən əl ilə araşdırırıq: printerin
   sayğacına, çap cəhdlərinin qeydlərinə və həmin vaxt terminalda baş vermiş
   digər hadisələrə baxırıq.

**Cavab müddəti:** müraciətinizi aldıqdan sonra **3 iş günü** ərzində cavab
veririk. Qaytarma qərarı verilərsə məbləğ **10 iş günü** ərzində qaytarılır
(kart ödənişlərində bankın öz müddəti də buna daxildir).

Araşdırmanın nəticəsi dörd variantdan biri olur: pul tam qaytarılır, bir
hissəsi qaytarılır, qaytarılmır (çap baş tutubsa), və ya sifariş **əlavə ödəniş
olmadan yenidən çapa göndərilir**.

---

## 4. Necə müraciət etmək olar

| Kanal | Necə |
|---|---|
| **QR kod** | Qəbzinizdəki və ya terminal ekranındakı qaytarma QR kodunu telefonla oxudun — açılan səhifədə sifariş artıq seçilmiş olur, yalnız qısa izah yazırsınız |
| **Mobil tətbiq** | NeoPrint tətbiqində sifarişi açın → **"Problem barədə bildir"** → qısa mətn |
| **Qaynar xətt** | Qəbzdəki və terminal ekranının aşağı hissəsindəki nömrə. Sifariş kodunu (məsələn `JOB-2K4M`) hazır saxlayın |

Anonim ödəniş etmisinizsə (hesab açmadan, bank QR-ı ilə), sifarişi
müəyyənləşdirmək üçün **qəbzdəki QR kod və ya sifariş kodu kifayətdir** —
şəxsiyyət sənədi tələb olunmur.

---

## 5. Balans (hesabınızdakı pul) geri qaytarılmır

NeoPrint balansına yüklədiyiniz pul yalnız çap xidmətlərinə xərclənə bilər.
**Nağd və ya karta geri çıxarıla bilməz.** Balansı artırarkən bu barədə
xəbərdarlıq göstərilir və razılığınız alınır.

Bu, konkret bir sifarişin pulunun qaytarılmasına aid deyil: kartla ödədiyiniz
sifarişin pulu ləğv və ya xəta halında **həmin karta** qaytarıla bilər. Qadağa
yalnız balansa artıq yüklənmiş pula aiddir.

Bonus səhifələr pul deyil, xidmət kreditidir; nağdlaşdırıla bilməz və istifadə
edilməyibsə ləğv halında bonus balansına qayıdır.

---

## 6. Anonim ödənişlərdə voucher

Hesabınız yoxdursa və bank kartınıza qaytarma texniki səbəbdən mümkün olmursa
(məsələn ödəniş sistemi əməliyyatı qəbul etmir), sizə eyni məbləğdə **voucher**
veririk: bir kod və QR. Onu istənilən NeoPrint terminalında ödəniş kimi
istifadə edə bilərsiniz.

- Voucher **90 gün** etibarlıdır.
- Kod terminal ekranında göstərilir, email versəniz poçtla da göndərilir.
- Voucher nağdlaşdırıla bilməz.
- Voucher yalnız kart qaytarması alınmadıqda verilir — ilk seçim həmişə pulun
  kartınıza qaytarılmasıdır.

---

## 7. Əlaqə

Qaytarma ilə bağlı bütün müraciətlər qeydə alınır və cavablandırılır. Cavabdan
razı qalmasanız müraciətinizi yenidən baxılmaq üçün göndərə bilərsiniz.

*(Şirkət adı, hüquqi ünvan, qaynar xətt nömrəsi və email ünvanı hüquqşünas
təsdiqindən sonra əlavə olunacaq.)*
