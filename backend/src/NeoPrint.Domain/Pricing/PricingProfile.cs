using NeoPrint.Domain.Common;
using NeoPrint.Pricing;

namespace NeoPrint.Domain.Pricing;

/// <summary>One step of the volume discount ladder (plan section 2.3).</summary>
public sealed class VolumeTier
{
    /// <summary>Sides from which this tier applies.</summary>
    public int MinSides { get; set; }

    /// <summary>Discount in percent. A percentage, not an amount.</summary>
    public decimal DiscountPercent { get; set; }
}

/// <summary>
/// A pricing profile (plan section 2.3, <c>pricing_profiles</c>).
/// </summary>
/// <remarks>
/// A kiosk points at a profile; kiosks without one fall back to the default
/// profile. There is always exactly one default. Prices are snapshotted onto a
/// job when it is created, so editing a profile never changes an existing
/// order.
/// </remarks>
public sealed class PricingProfile : Entity
{
    public const string DefaultProfileName = "Standart";

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    /// <summary>Price of one black and white side.</summary>
    public Money BwPricePerSide { get; set; } = Money.FromAzn(0.10m);

    /// <summary>Price of one colour side.</summary>
    public Money ColorPricePerSide { get; set; } = Money.FromAzn(0.30m);

    /// <summary>
    /// Multiplier per paper size, for example A4 = 1, A3 = 2.
    /// </summary>
    /// <remarks>
    /// A factor, not an amount, so it stays a decimal.
    /// </remarks>
    public Dictionary<string, decimal> PaperMultipliers { get; set; } = new(StringComparer.Ordinal)
    {
        ["A4"] = 1m,
        ["A3"] = 2m,
    };

    /// <summary>Minimum charge for an order.</summary>
    public Money MinOrder { get; set; } = Money.FromAzn(0.20m);

    public List<VolumeTier> VolumeTiers { get; set; } =
    [
        new() { MinSides = 50, DiscountPercent = 10m },
        new() { MinSides = 100, DiscountPercent = 15m },
    ];

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Change history, newest last.</summary>
    public List<PricingProfileChange> History { get; set; } = [];

    /// <summary>
    /// The rates as a value, for the calculator and for a snapshot.
    /// </summary>
    /// <remarks>
    /// New collections, not the profile's own: a snapshot that shared this
    /// profile's dictionary would change in memory the moment the profile was
    /// edited, before anything was even saved.
    /// </remarks>
    public PriceTable ToPriceTable() => new(
        BwPricePerSide.Amount,
        ColorPricePerSide.Amount,
        new Dictionary<string, decimal>(PaperMultipliers, StringComparer.Ordinal),
        MinOrder.Amount,
        [.. VolumeTiers.OrderBy(t => t.MinSides).Select(t => new PriceTier(t.MinSides, t.DiscountPercent))]);

    /// <summary>
    /// Checks the values an administrator entered.
    /// </summary>
    /// <returns>Problems by field; empty when the profile is valid.</returns>
    public IReadOnlyList<(string Field, string Message)> Validate()
    {
        var problems = new List<(string, string)>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add(("name", "Ad tələb olunur."));
        }

        if (BwPricePerSide.Amount < 0 || ColorPricePerSide.Amount < 0 || MinOrder.Amount < 0)
        {
            problems.Add(("prices", "Qiymətlər mənfi ola bilməz."));
        }

        if (!PaperMultipliers.ContainsKey("A4"))
        {
            // Every order has a paper size; without A4 the most common order
            // could not be priced at all.
            problems.Add(("paperMultipliers", "A4 əmsalı tələb olunur."));
        }

        if (PaperMultipliers.Values.Any(m => m <= 0))
        {
            problems.Add(("paperMultipliers", "Kağız əmsalı müsbət olmalıdır."));
        }

        if (VolumeTiers.Any(t => t.MinSides < 1))
        {
            problems.Add(("volumeTiers", "Pillənin minimum üz sayı ən azı 1 olmalıdır."));
        }

        if (VolumeTiers.Any(t => t.DiscountPercent <= 0 || t.DiscountPercent >= 100))
        {
            problems.Add(("volumeTiers", "Endirim faizi 0 ilə 100 arasında olmalıdır."));
        }

        if (VolumeTiers.GroupBy(t => t.MinSides).Any(g => g.Count() > 1))
        {
            problems.Add(("volumeTiers", "Eyni minimum üz sayı ilə iki pillə ola bilməz."));
        }

        return problems;
    }
}

/// <summary>One recorded change to a pricing profile.</summary>
public sealed class PricingProfileChange
{
    public DateTime At { get; set; } = DateTime.UtcNow;

    public Guid? By { get; set; }

    /// <summary>JSON snapshot of the profile before the change.</summary>
    public string? Before { get; set; }
}
