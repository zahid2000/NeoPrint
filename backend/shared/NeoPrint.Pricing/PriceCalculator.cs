namespace NeoPrint.Pricing;

/// <summary>One step of the volume ladder.</summary>
/// <param name="MinSides">Sides from which the tier applies.</param>
/// <param name="DiscountPercent">The discount, in percent; 0 for a tier that names its prices.</param>
/// <param name="BwPerSide">
/// A tier that names its own price for a black and white side instead of a
/// discount (T-436) - "from 51 sides, 13 qəpik". Set together with
/// <paramref name="ColorPerSide"/>; null for a percentage tier.
/// </param>
/// <param name="ColorPerSide">The same for a colour side.</param>
public sealed record PriceTier(int MinSides, decimal DiscountPercent, decimal? BwPerSide = null, decimal? ColorPerSide = null)
{
    /// <summary>The tier names its prices rather than a discount.</summary>
    /// <remarks>A method, not a property: a stored snapshot maps the record by its constructor.</remarks>
    public bool NamesPrices() => BwPerSide is not null && ColorPerSide is not null;
}

/// <summary>How the volume ladder is read (T-436), chosen per profile.</summary>
public enum TierMode
{
    /// <summary>The whole order at the tier its side count reaches - 120 sides all at the "from 101" rate.</summary>
    WholeOrder = 0,

    /// <summary>Each band at its own rate - the first 50 at one, the next 50 at the next, the rest at the last.</summary>
    Graduated = 1,
}

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
    string Currency = "AZN",
    TierMode TierMode = TierMode.WholeOrder,
    decimal CampaignPercent = 0m,
    string? CampaignName = null);

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
    string Currency,
    decimal CampaignPercent = 0m,
    decimal CampaignDiscount = 0m,
    string? CampaignName = null);

/// <summary>A price could not be computed from the input.</summary>
/// <remarks>
/// Carries a message key and its values, not text: the backend and the kiosk
/// each write it out in their reader's language from their own catalogs.
/// </remarks>
public sealed class PricingException : Exception
{
    public PricingException(string code, params (string Name, object? Value)[] arguments)
        : base(code)
    {
        Code = code;
        Arguments = arguments;
    }

    /// <summary>The message key, <c>pricing.*</c>.</summary>
    public string Code { get; }

    /// <summary>The values for the message's placeholders.</summary>
    public (string Name, object? Value)[] Arguments { get; }
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
/// rate(t)= t names prices ? t.price(color) : pricePerSide(color) * (1 - t.DiscountPercent / 100)
/// whole  : the largest tier with sides >= MinSides, all sides at its rate
/// graded : each band of sides at its own rate - below the first tier at pricePerSide
/// tiered = sides at those rates * paperMultiplier
/// price  = max(MinOrder, tiered * (1 - CampaignPercent / 100))
/// </code>
/// A whole-order percentage tier keeps the original arithmetic exactly -
/// base less the rounded discount - so no price already quoted moves (T-436).
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
            throw new PricingException("pricing.paperNotPriced", ("paper", spec.Paper));
        }

        var sidesPerCopy = (spec.SelectedPages + spec.PagesPerSheet - 1) / spec.PagesPerSheet;
        var sides = checked(sidesPerCopy * spec.Copies);
        var sheets = spec.Duplex ? (sides + 1) / 2 : sides;

        var perSide = spec.Color ? table.ColorPerSide : table.BwPerSide;
        var basePrice = Round(sides * perSide * multiplier);

        var tiers = table.Tiers.OrderBy(t => t.MinSides).ToList();
        decimal tiered;
        decimal discountPercent;

        if (table.TierMode == TierMode.Graduated)
        {
            // Each band at its own rate: sides 1 .. (first tier - 1) at the
            // plain rate, then each tier up to the next one's start.
            var exact = 0m;
            var from = 1;
            var rate = perSide;

            foreach (var tier in tiers.Where(t => t.MinSides <= sides))
            {
                exact += Math.Max(0, tier.MinSides - from) * rate;
                from = Math.Max(from, tier.MinSides);
                rate = Rate(tier, spec.Color, perSide);
            }

            exact += (sides - from + 1) * rate;
            tiered = Round(exact * multiplier);
            discountPercent = basePrice > 0 ? Round((basePrice - tiered) * 100m / basePrice) : 0m;
        }
        else
        {
            // The largest tier the order qualifies for: tiers are not cumulative.
            var tier = tiers.LastOrDefault(t => sides >= t.MinSides);

            if (tier is not null && tier.NamesPrices())
            {
                tiered = Round(sides * Rate(tier, spec.Color, perSide) * multiplier);
                discountPercent = basePrice > 0 ? Round((basePrice - tiered) * 100m / basePrice) : 0m;
            }
            else
            {
                discountPercent = tier?.DiscountPercent ?? 0m;
                tiered = basePrice - Round(basePrice * discountPercent / 100m);
            }
        }

        var discount = basePrice - tiered;

        // A campaign's percentage (T-436), on what the ladder left.
        var campaignDiscount = Round(tiered * table.CampaignPercent / 100m);
        var discounted = tiered - campaignDiscount;

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
            table.Currency,
            table.CampaignPercent,
            campaignDiscount,
            table.CampaignName);
    }

    /// <summary>A tier's rate for one side.</summary>
    private static decimal Rate(PriceTier tier, bool color, decimal perSide)
        => tier.NamesPrices()
            ? (color ? tier.ColorPerSide!.Value : tier.BwPerSide!.Value)
            : perSide * (1m - tier.DiscountPercent / 100m);

    /// <summary>Two decimals, half to even - the backend's Money rule.</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);

    private static void Validate(PrintSpec spec)
    {
        if (spec.SelectedPages < 1)
        {
            throw new PricingException("pricing.noPages");
        }

        if (spec.Copies is < 1 or > MaxCopies)
        {
            throw new PricingException("pricing.copiesRange", ("max", MaxCopies));
        }

        if (spec.PagesPerSheet is < 1 or > MaxPagesPerSheet)
        {
            throw new PricingException("pricing.pagesPerSheetRange", ("max", MaxPagesPerSheet));
        }

        if (string.IsNullOrWhiteSpace(spec.Paper))
        {
            throw new PricingException("pricing.paperRequired");
        }
    }
}
