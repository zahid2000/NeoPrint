using NeoPrint.Kiosk.Core;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// What a kiosk may do when the backend cannot be reached
/// (plan section 7.3, <c>docs/OFFLINE_BEHAVIOR.md</c>).
/// </summary>
public sealed class KioskAvailabilityTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    [Fact]
    public void A_reachable_backend_means_everything_works()
    {
        var availability = KioskAvailabilityRules.Decide(
            backendReachable: true, configurationCachedAt: null, Ttl, Now);

        availability.Should().Be(KioskAvailability.Online);
        availability.CanTakeNewOrders().Should().BeTrue();
        availability.CanContinueQueue().Should().BeTrue();
    }

    [Fact]
    public void An_unreachable_backend_with_a_fresh_cache_is_degraded()
    {
        var availability = KioskAvailabilityRules.Decide(
            backendReachable: false, Now.AddHours(-1), Ttl, Now);

        availability.Should().Be(KioskAvailability.Degraded);

        // Paid work in the queue keeps printing: the customer has paid and the
        // job is local, so stopping would strand them.
        availability.CanContinueQueue().Should().BeTrue();

        // But no new payment. A payment the backend has not confirmed must
        // never be shown as successful (docs/OFFLINE_BEHAVIOR.md).
        availability.CanTakeNewOrders().Should().BeFalse();
    }

    [Fact]
    public void A_cache_older_than_its_ttl_takes_the_kiosk_offline()
    {
        var availability = KioskAvailabilityRules.Decide(
            backendReachable: false, Now.AddHours(-25), Ttl, Now);

        // Past the TTL the kiosk would be guessing at its own prices and
        // limits, and guessing at a price is worse than refusing.
        availability.Should().Be(KioskAvailability.Offline);
        availability.CanContinueQueue().Should().BeFalse();
        availability.CanTakeNewOrders().Should().BeFalse();
    }

    [Fact]
    public void A_cache_exactly_at_its_ttl_is_still_usable()
    {
        KioskAvailabilityRules
            .Decide(backendReachable: false, Now - Ttl, Ttl, Now)
            .Should().Be(KioskAvailability.Degraded);
    }

    [Fact]
    public void A_kiosk_that_never_had_a_configuration_is_offline()
    {
        // Nothing to fall back on: it has never been told its own prices.
        KioskAvailabilityRules
            .Decide(backendReachable: false, configurationCachedAt: null, Ttl, Now)
            .Should().Be(KioskAvailability.Offline);
    }

    [Fact]
    public void A_zero_ttl_means_never_trust_a_cache()
    {
        // A legitimate way to configure a kiosk that must not run unattended.
        KioskAvailabilityRules
            .Decide(backendReachable: false, Now.AddMinutes(-1), TimeSpan.Zero, Now)
            .Should().Be(KioskAvailability.Offline);
    }

    [Fact]
    public void A_cache_from_the_future_is_not_trusted()
    {
        // The clock has been put back. Treating the cache as fresh would let a
        // stale configuration run indefinitely.
        KioskAvailabilityRules
            .Decide(backendReachable: false, Now.AddHours(1), Ttl, Now)
            .Should().Be(KioskAvailability.Offline);
    }

    [Fact]
    public void No_availability_lets_a_new_payment_start_without_the_backend()
    {
        // The one invariant that holds across every state: the kiosk never
        // takes money it cannot have confirmed.
        foreach (var availability in Enum.GetValues<KioskAvailability>())
        {
            if (availability != KioskAvailability.Online)
            {
                availability.CanTakeNewOrders()
                    .Should().BeFalse($"{availability} has no confirmed link to the backend");
            }
        }
    }
}
