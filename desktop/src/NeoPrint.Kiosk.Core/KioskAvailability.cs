namespace NeoPrint.Kiosk.Core;

/// <summary>
/// What a kiosk may do right now (plan section 7.3,
/// <c>docs/OFFLINE_BEHAVIOR.md</c>).
/// </summary>
public enum KioskAvailability
{
    /// <summary>Backend reachable: everything works.</summary>
    Online = 0,

    /// <summary>
    /// Backend unreachable but the cached configuration is still fresh: the
    /// screen stays open and paid work in the queue keeps printing, but no new
    /// payment is taken.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// No connection and no usable cache: no new orders at all.
    /// </summary>
    Offline = 2,
}

/// <summary>
/// Decides what a kiosk may do when the backend cannot be reached
/// (plan section 7.3, screens 15 and 16).
/// </summary>
/// <remarks>
/// The distinction the plan draws is between a kiosk that still knows its own
/// prices and limits and one that does not. A kiosk with a fresh cache can
/// finish work that was already paid for; one whose cache has gone stale would
/// be guessing, and guessing at a price is worse than refusing.
///
/// New payments stop in both cases. <c>docs/OFFLINE_BEHAVIOR.md</c> is explicit
/// that the kiosk must never show a payment as successful without the backend
/// or the provider having confirmed it, so an unreachable backend means no new
/// payment, full stop.
/// </remarks>
public static class KioskAvailabilityRules
{
    /// <summary>
    /// Works out the availability.
    /// </summary>
    /// <param name="backendReachable">Whether the last call to the backend succeeded.</param>
    /// <param name="configurationCachedAt">
    /// When the cached configuration was last refreshed, or <c>null</c> when
    /// the kiosk has never had one.
    /// </param>
    /// <param name="cacheTtl">
    /// How long a cache stays usable - <c>Kiosk.ConfigCacheTtlHours</c>, which
    /// the backend sends with the configuration.
    /// </param>
    /// <param name="now">The current time.</param>
    public static KioskAvailability Decide(
        bool backendReachable,
        DateTime? configurationCachedAt,
        TimeSpan cacheTtl,
        DateTime now)
    {
        if (backendReachable)
        {
            return KioskAvailability.Online;
        }

        if (configurationCachedAt is null)
        {
            // Never configured, so there is nothing to fall back on.
            return KioskAvailability.Offline;
        }

        // A non-positive TTL means "do not trust a cache at all", which is a
        // legitimate way to configure a kiosk that must never run unattended.
        if (cacheTtl <= TimeSpan.Zero)
        {
            return KioskAvailability.Offline;
        }

        var age = now - configurationCachedAt.Value;

        // A cache from the future is a clock that has been put back; treating
        // it as fresh would let a stale configuration run indefinitely.
        if (age < TimeSpan.Zero)
        {
            return KioskAvailability.Offline;
        }

        return age <= cacheTtl ? KioskAvailability.Degraded : KioskAvailability.Offline;
    }

    /// <summary>Whether a new order may be started.</summary>
    /// <remarks>
    /// Only when the backend is reachable. A payment the backend has not
    /// confirmed must never be shown as successful
    /// (<c>docs/OFFLINE_BEHAVIOR.md</c>).
    /// </remarks>
    public static bool CanTakeNewOrders(this KioskAvailability availability)
        => availability == KioskAvailability.Online;

    /// <summary>
    /// Whether work already paid for may keep printing.
    /// </summary>
    /// <remarks>
    /// Yes while the cache is fresh: the customer has paid and the job is in
    /// the local queue, so stopping would strand them (plan section 7.3).
    /// </remarks>
    public static bool CanContinueQueue(this KioskAvailability availability)
        => availability != KioskAvailability.Offline;
}
