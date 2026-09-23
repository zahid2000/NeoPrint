using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Pricing;

/// <summary>One step of the volume discount ladder (plan section 2.3).</summary>
public sealed class VolumeTier
{
    /// <summary>Sides from which this tier applies.</summary>
    public int MinSides { get; set; }

    /// <summary>Discount in percent.</summary>
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
    public const string DefaultProfileName = "Default";

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    /// <summary>Price of one black and white side, in AZN.</summary>
    public decimal BwPricePerSide { get; set; } = 0.10m;

    /// <summary>Price of one colour side, in AZN.</summary>
    public decimal ColorPricePerSide { get; set; } = 0.30m;

    /// <summary>Multiplier per paper size, for example A4 = 1, A3 = 2.</summary>
    public Dictionary<string, decimal> PaperMultipliers { get; set; } = new(StringComparer.Ordinal)
    {
        ["A4"] = 1m,
        ["A3"] = 2m,
    };

    /// <summary>Minimum charge for an order, in AZN.</summary>
    public decimal MinOrder { get; set; } = 0.20m;

    public List<VolumeTier> VolumeTiers { get; set; } =
    [
        new() { MinSides = 50, DiscountPercent = 10m },
        new() { MinSides = 100, DiscountPercent = 15m },
    ];

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Change history, newest last.</summary>
    public List<PricingProfileChange> History { get; set; } = [];
}

/// <summary>One recorded change to a pricing profile.</summary>
public sealed class PricingProfileChange
{
    public DateTime At { get; set; } = DateTime.UtcNow;

    public Guid? By { get; set; }

    /// <summary>JSON snapshot of the profile before the change.</summary>
    public string? Before { get; set; }
}
