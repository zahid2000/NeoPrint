using NeoPrint.Domain.Users;

namespace NeoPrint.Infrastructure.Seeding;

/// <summary>
/// The legal document drafts seeded into an empty database.
/// </summary>
/// <remarks>
/// These mirror <c>docs/legal/PRIVACY_POLICY.az.md</c> and
/// <c>docs/legal/TERMS.az.md</c>. Both are drafts pending review by a lawyer
/// (plan section 14); the published text is managed from the admin panel, and
/// publishing a new version makes every user accept it again at their next
/// sign-in.
/// </remarks>
internal static class LegalDocumentDrafts
{
    /// <summary>
    /// The version of the seeded drafts. The suffix is deliberate: it is
    /// visible in the app and in the consent record, so nobody mistakes an
    /// unreviewed draft for the final text.
    /// </summary>
    public const string DraftVersion = "0.1-draft";

    public static string For(LegalDocumentType docType) => docType switch
    {
        LegalDocumentType.Privacy => Privacy,
        LegalDocumentType.Terms => Terms,
        LegalDocumentType.ScanNotice => ScanNotice,
        _ => throw new ArgumentOutOfRangeException(nameof(docType), docType, "No draft exists for this document type."),
    };

    private const string Privacy = """
        # Məxfilik siyasəti

        > **QARALAMA — hüquqşünas təsdiqi tələb olunur.**

        ## Hansı məlumatları toplayırıq
        Email və profil məlumatları, cihaz identifikatoru, çap üçün yüklədiyiniz fayllar,
        ödəniş qeydləri və (icazə versəniz) yaxınlıqdakı kioskları tapmaq üçün məkan.
        Kart məlumatlarını saxlamırıq.

        ## Anonim kiosk istifadəsi
        Flash kartdan çap edərkən hesab tələb olunmur. Sənədiniz kioskda lokal, şifrəli
        şəkildə emal olunur və çapdan sonra dərhal silinir. Serverə yalnız metadata gedir.
        NeoPrint mobil tətbiqi ilə ödəsəniz, sənəd və qəbz hesabınıza əlavə olunur.

        ## Şəxsi sənəd skanları
        Şəxsiyyət vəsiqəsi, pasport və oxşar sənədlərin skanları həssas işarələnir,
        şifrələnir və default olaraq çapdan sonra avtomatik silinir. Bu davranışı hər
        fayl üçün dəyişə bilərsiniz.

        ## Fayllarınıza kimin çıxışı var
        Yalnız siz və paylaşdığınız istifadəçi. Admin işçilərimizin fayl məzmununa
        çıxışı yoxdur — sistemdə belə bir imkan mövcud deyil. Bütün obyektlər saxlama
        serverinə yazılmazdan əvvəl şifrələnir.

        ## Hüquqlarınız
        Məlumatlarınızı ZIP arxivi kimi yükləyə və hesabınızı silə bilərsiniz. Hesab
        silindikdə bütün fayllar 24 saat ərzində fiziki olaraq silinir və profil
        anonimləşdirilir. **Balansınız və bonus səhifələriniz itir.**

        Tam mətn: docs/legal/PRIVACY_POLICY.az.md
        """;

    private const string Terms = """
        # İstifadə şərtləri

        > **QARALAMA — hüquqşünas təsdiqi tələb olunur.**

        ## Qiymətlər
        Qiymət çapdan əvvəl göstərilir və sifariş yaradılarkən sabitlənir. Sonrakı
        qiymət dəyişiklikləri mövcud sifarişə təsir etmir.

        ## Balans (Wallet)
        NeoPrint balansı **əvvəlcədən ödənilmiş xidmət kreditidir (avansdır)**.
        Balansa yüklənən məbləğ **heç bir halda geri qaytarılmır, nağdlaşdırılmır və
        karta çıxarılmır** — nə istifadəçi, nə də operator tərəfindən. Balansa düşən
        qaytarmalar da eyni qaydaya tabedir. Hesab silinərkən balans itir.

        ## Bonus səhifələr
        Bonus səhifələr pul deyil və nağdlaşdırıla bilməz. 1 bonus = 1 çap olunan üz.
        Hesab silinərkən bonus səhifələr itir.

        ## Qaytarma
        Sistem xətası baş verərsə məbləğ avtomatik qaytarılır: kioskda bank ilə
        ödənilibsə karta (bankdan asılı olaraq 1–3 iş günü), mobil tətbiqdə
        ödənilibsə balansa. Hissəvi çap olunmuş sifarişdə yalnız çap olunmamış
        hissənin dəyəri qaytarılır.

        ## Məzmun
        Çap etdiyiniz məzmunun qanuniliyinə siz cavabdehsiniz.

        Tam mətn: docs/legal/TERMS.az.md
        """;

    private const string ScanNotice = """
        # Sənəd skanı barədə məlumat

        > **QARALAMA — hüquqşünas təsdiqi tələb olunur.**

        Şəxsiyyət vəsiqəsi, sürücülük vəsiqəsi, pasport və bank kartı kimi sənədləri
        skan edirsinizsə:

        - Skan **həssas** işarələnir və cihazdan serverə şifrəli göndərilir.
        - Saxlama serverində **şifrəli** qalır; birbaşa serverdən götürülən fayl oxunmur.
        - Default olaraq **çapdan sonra avtomatik silinir**. Bunu hər fayl üçün
          dəyişə bilərsiniz: `Heç vaxt` / `Çapdan sonra` / `N gün sonra`.
        - Admin işçilərimizin bu fayla baxmaq imkanı **yoxdur**.

        Həssas faylı başqa istifadəçi ilə paylaşdıqda ayrıca xəbərdarlıq göstərilir.

        Ətraflı: Məxfilik siyasəti.
        """;
}
