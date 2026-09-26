using NeoPrint.Domain.Common;
using NeoPrint.Pricing;

namespace NeoPrint.Domain.Tests.Pricing;

/// <summary>
/// The kiosk and the backend round money the same way.
/// </summary>
/// <remarks>
/// The price formula lives in NeoPrint.Pricing so the kiosk can show exactly
/// what the backend charges, and the kiosk has no Money type - so the rounding
/// rule exists twice. A kiosk that rounded 0.125 up while the backend rounded
/// it down would show one price and charge another; this is what stops the
/// two from drifting apart.
/// </remarks>
public sealed class RoundingParityTests
{
    public static IEnumerable<object[]> Amounts()
    {
        // The midpoints are where half-up and half-to-even disagree.
        foreach (var value in new[] { 0.005m, 0.015m, 0.025m, 0.125m, 0.135m, 0.375m, 1.005m, 2.675m, 10.125m, 0.2925m })
        {
            yield return [value];
            yield return [-value];
        }

        var random = new Random(20260926);

        for (var i = 0; i < 200; i++)
        {
            yield return [Math.Round((decimal)random.NextDouble() * 1000m, 4)];
        }
    }

    [Theory]
    [MemberData(nameof(Amounts))]
    public void Both_sides_round_every_amount_identically(decimal amount)
        => PriceCalculator.Round(amount).Should().Be(Money.Round(amount));
}
