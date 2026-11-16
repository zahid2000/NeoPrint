using NeoPrint.Domain.Common;
using NeoPrint.Pricing;

namespace NeoPrint.Domain.Pricing;

/// <summary>What a campaign changes (T-436).</summary>
public enum CampaignKind
{
    /// <summary>A percentage off what the kiosk's profile and its ladder come to.</summary>
    Percent = 0,

    /// <summary>Its own prices a side, and its own ladder, in place of the profile's.</summary>
    Price = 1,
}

/// <summary>
/// A pricing campaign (T-436, <c>pricing_campaigns</c>): for a while or until
/// it is switched off, at every kiosk or at the ones chosen, either a
/// percentage off or prices of its own.
/// </summary>
/// <remarks>
/// <para>
/// No code is typed: while it runs, every order at its kiosks is priced with
/// it - unlike a promo code (plan section 2.7), which still applies last, on
/// top of it.
/// </para>
/// <para>
/// One campaign at a time prices a kiosk (<see cref="Pick"/>): one aimed at
/// the kiosk beats one for every kiosk, then the one that started last. The
/// minimum order and the paper multipliers stay the profile's.
/// </para>
/// <para>
/// Like a profile's prices, it is copied into the order's snapshot when the
/// order is placed: editing or ending it afterwards changes no order.
/// </para>
/// </remarks>
public sealed class PricingCampaign : Entity
{
    public const int MaxNameLength = 80;

    /// <summary>Shown to customers with the price ("Noyabr kampaniyası").</summary>
    public string Name { get; set; } = string.Empty;

    public CampaignKind Kind { get; set; }

    /// <summary>The percentage off, for <see cref="CampaignKind.Percent"/>.</summary>
    public decimal Percent { get; set; }

    /// <summary>A black and white side's price, for <see cref="CampaignKind.Price"/>.</summary>
    public decimal? BwPricePerSide { get; set; }

    /// <summary>A colour side's price, for <see cref="CampaignKind.Price"/>.</summary>
    public decimal? ColorPricePerSide { get; set; }

    /// <summary>The campaign's own ladder, for <see cref="CampaignKind.Price"/>; empty for none.</summary>
    public List<VolumeTier> VolumeTiers { get; set; } = [];

    public TierMode TierMode { get; set; } = TierMode.WholeOrder;

    /// <summary>From when; null for "from now".</summary>
    public DateTime? StartsAt { get; set; }

    /// <summary>Until when, not included; null for "until switched off".</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>The kiosks it prices; empty for every kiosk.</summary>
    public List<Guid> KioskIds { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Switched on, and <paramref name="now"/> inside its dates.</summary>
    public bool IsRunning(DateTime now)
        => IsActive && (StartsAt is not { } from || from <= now) && (EndsAt is not { } until || now < until);

    /// <summary>Prices the kiosk: every kiosk, or it is one of those chosen.</summary>
    public bool Covers(Guid kioskId) => KioskIds.Count == 0 || KioskIds.Contains(kioskId);

    /// <summary>
    /// The campaign that prices <paramref name="kioskId"/> now, if any: one
    /// aimed at the kiosk before one for all, then the latest start, then the
    /// newest.
    /// </summary>
    public static PricingCampaign? Pick(IEnumerable<PricingCampaign> campaigns, Guid kioskId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        return campaigns
            .Where(c => c.IsRunning(now) && c.Covers(kioskId))
            .OrderByDescending(c => c.KioskIds.Count > 0)
            .ThenByDescending(c => c.StartsAt ?? DateTime.MinValue)
            .ThenByDescending(c => c.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>A profile's rates with this campaign on them.</summary>
    public PriceTable Apply(PriceTable rates)
    {
        ArgumentNullException.ThrowIfNull(rates);

        return Kind == CampaignKind.Percent
            ? rates with { CampaignPercent = Percent, CampaignName = Name }
            : rates with
            {
                BwPerSide = BwPricePerSide ?? rates.BwPerSide,
                ColorPerSide = ColorPricePerSide ?? rates.ColorPerSide,
                Tiers = [.. VolumeTiers.OrderBy(t => t.MinSides).Select(t => t.ToPriceTier())],
                TierMode = TierMode,
                CampaignPercent = 0m,
                CampaignName = Name,
            };
    }

    /// <summary>Checks the values an administrator entered; problems by field, as message keys.</summary>
    public IReadOnlyList<(string Field, string Message)> Validate()
    {
        var problems = new List<(string, string)>();

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
        {
            problems.Add(("name", "pricing.campaignNameRequired"));
        }

        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(TierMode))
        {
            problems.Add(("kind", "pricing.campaignKindInvalid"));
        }

        if (Kind == CampaignKind.Percent && (Percent <= 0 || Percent >= 100))
        {
            problems.Add(("percent", "pricing.campaignPercentRange"));
        }

        if (Kind == CampaignKind.Price)
        {
            if (BwPricePerSide is not { } bw || ColorPricePerSide is not { } color || bw < 0 || color < 0)
            {
                problems.Add(("prices", "pricing.campaignPricesRequired"));
            }

            problems.AddRange(VolumeTier.Check(VolumeTiers));
        }

        if (StartsAt is { } from && EndsAt is { } until && until <= from)
        {
            problems.Add(("endsAt", "pricing.campaignDatesOrder"));
        }

        return problems;
    }
}
