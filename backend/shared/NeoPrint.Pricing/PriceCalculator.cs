namespace NeoPrint.Pricing;

/// <summary>One step of the volume discount ladder.</summary>
/// <param name="MinSides">Sides from which the tier applies.</param>
/// <param name="DiscountPercent">The discount, in percent.</param>
public sealed record PriceTier(int MinSides, decimal DiscountPercent);

/// <summary>
/// The rates a price is computed from - a pricing profile's values, copied.
/// </summary>
/// <remarks>
/// Deliberately not the profile itself. A snapshot holds these by value, so a
/// later edit to the profile cannot reach back into an order that was already
/// priced (plan section 2.3).
/// </remarks>
public sealed record PriceTable(
    decimal BwPerSide,
    decimal ColorPerSide,
    IReadOnlyDictionary<string, decimal> PaperMultipliers,
    decimal MinOrder,
    IReadOnlyList<PriceTier> Tiers,
    string Currency = "AZN");

/// <summary>What is being printed.</summary>
/// <param name="SelectedPages">Pages chosen for printing, after any range.</param>
/// <param name="PagesPerSheet">Pages placed on one side (N-up). 1 for normal printing.</param>
/// <param name="Copies">Copies of the whole selection.</param>
/// <param name="Duplex">Both sides of the paper.</param>
/// <param name="Color">Colour rather than black and white.</param>
/// <param name="Paper">A4 or A3.</param>
public sealed record PrintSpec(
    int SelectedPages,
    int PagesPerSheet,
    int Copies,
    bool Duplex,
    bool Color,
    string Paper);

/// <summary>
/// A price and how it was arrived at (plan section 2.3, <c>PriceBreakdown</c>).
/// </summary>
/// <remarks>
/// Everything the price depends on is in here - the specification and the
/// rates - so the figure can be explained to a customer and recomputed for an
/// audit without consulting a profile that may have changed since.
/// Promotions and bonus pages are part of the shape now and applied in step 4,
/// where promo codes and bonus balances arrive.
/// </remarks>
public sealed record PriceBreakdown(
    int Pages,
    int Sides,
    int Sheets,
    int Copies,
    bool Color,
    bool Duplex,
    string Paper,
    decimal PricePerSide,
    decimal PaperMultiplier,
    decimal BasePrice,
    decimal DiscountPercent,
    decimal VolumeDiscount,
    decimal PromoDiscount,
    int BonusPages,
    bool MinimumApplied,
    decimal FinalPrice,
    string Currency);

/// <summary>A price could not be computed from the input.</summary>
public sealed class PricingException : Exception
{
    public PricingException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The price formula (plan section 2.3), shared by the backend and the kiosk
/// so that the price on the kiosk screen is exactly the one charged.
/// </summary>
/// <remarks>
/// <code>
/// sides  = ceil(selectedPages / pagesPerSheet) * copies
/// sheets = duplex ? ceil(sides / 2) : sides        // for the paper counter
/// base   = sides * pricePerSide(color) * paperMultiplier(paper)
/// tier   = the largest tier with sides >= MinSides
/// price  = max(MinOrder, base * (1 - tier.DiscountPercent / 100))
/// </code>
/// Rounding is to two decimals, half to even, at the points the plan names -
/// the same rule as the backend's Money type, which a test holds the two to.
/// The kiosk has no Money type, so this assembly cannot depend on it.
/// </remarks>
public static class PriceCalculator
{
    /// <summary>Upper bounds that keep a malformed request from producing nonsense.</summary>
    public const int MaxCopies = 999;

    public const int MaxPagesPerSheet = 16;

    public static PriceBreakdown Quote(PriceTable table, PrintSpec spec)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(spec);

        Validate(spec);

        if (!table.PaperMultipliers.TryGetValue(spec.Paper, out var multiplier))
        {
            throw new PricingException($"Bu profil '{spec.Paper}' kağızı üçün qiymət vermir.");
        }

        var sidesPerCopy = (spec.SelectedPages + spec.PagesPerSheet - 1) / spec.PagesPerSheet;
        var sides = checked(sidesPerCopy * spec.Copies);
        var sheets = spec.Duplex ? (sides + 1) / 2 : sides;

        var perSide = spec.Color ? table.ColorPerSide : table.BwPerSide;
        var basePrice = Round(sides * perSide * multiplier);

        // The largest tier the order qualifies for: tiers are not cumulative.
        var tier = table.Tiers
            .Where(t => sides >= t.MinSides)
            .OrderByDescending(t => t.MinSides)
            .FirstOrDefault();

        var discountPercent = tier?.DiscountPercent ?? 0m;
        var discount = Round(basePrice * discountPercent / 100m);
        var discounted = basePrice - discount;

        var minimumApplied = discounted < table.MinOrder;
        var final = minimumApplied ? table.MinOrder : discounted;

        return new PriceBreakdown(
            spec.SelectedPages,
            sides,
            sheets,
            spec.Copies,
            spec.Color,
            spec.Duplex,
            spec.Paper,
            perSide,
            multiplier,
            basePrice,
            discountPercent,
            discount,
            PromoDiscount: 0m,
            BonusPages: 0,
            minimumApplied,
            Round(final),
            table.Currency);
    }

    /// <summary>Two decimals, half to even - the backend's Money rule.</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);

    private static void Validate(PrintSpec spec)
    {
        if (spec.SelectedPages < 1)
        {
            throw new PricingException("Ən azı bir səhifə seçilməlidir.");
        }

        if (spec.Copies is < 1 or > MaxCopies)
        {
            throw new PricingException($"Nüsxə sayı 1 ilə {MaxCopies} arasında olmalıdır.");
        }

        if (spec.PagesPerSheet is < 1 or > MaxPagesPerSheet)
        {
            throw new PricingException($"Səhifədə səhifə sayı 1 ilə {MaxPagesPerSheet} arasında olmalıdır.");
        }

        if (string.IsNullOrWhiteSpace(spec.Paper))
        {
            throw new PricingException("Kağız ölçüsü göstərilməyib.");
        }
    }
}
