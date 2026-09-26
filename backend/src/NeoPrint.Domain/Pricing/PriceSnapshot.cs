using NeoPrint.Domain.Common;
using NeoPrint.Pricing;

namespace NeoPrint.Domain.Pricing;

/// <summary>
/// A price as it was when the order was placed (plan section 2.3).
/// </summary>
/// <remarks>
/// <para>
/// Holds <em>copies</em>: the rates, the specification and the result. Nothing
/// in here refers back to the profile, so editing or deleting the profile
/// afterwards cannot change what an existing order costs - which is the rule
/// ("sonrakı profil dəyişiklikləri mövcud job-lara təsir etmir") and what
/// <c>PriceSnapshotTests</c> proves, including after a round trip through the
/// database.
/// </para>
/// <para>
/// The profile's id, name and row version are kept for the audit trail: which
/// profile, and which revision of it, produced this price.
/// </para>
/// </remarks>
public sealed record PriceSnapshot(
    Guid ProfileId,
    string ProfileName,
    int ProfileVersion,
    PriceTable Rates,
    PrintSpec Spec,
    PriceBreakdown Price,
    DateTime TakenAt)
{
    /// <summary>Prices a specification against a profile, as of now.</summary>
    public static PriceSnapshot Take(PricingProfile profile, PrintSpec spec, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(spec);

        var rates = profile.ToPriceTable();

        return new PriceSnapshot(
            profile.Id,
            profile.Name,
            profile.RowVersion,
            rates,
            spec,
            PriceCalculator.Quote(rates, spec),
            now);
    }

    /// <summary>What the customer pays.</summary>
    public Money Total => Money.FromAzn(Price.FinalPrice);

    /// <summary>
    /// Recomputes the price from the rates held here.
    /// </summary>
    /// <remarks>
    /// For an audit: it has to agree with <see cref="Price"/>, whatever the
    /// profile says today.
    /// </remarks>
    public PriceBreakdown Recompute() => PriceCalculator.Quote(Rates, Spec);
}
