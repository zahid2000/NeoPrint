namespace NeoPrint.Domain.Analytics;

/// <summary>Where an analytics event came from (plan section 2.17).</summary>
public enum AnalyticsSource
{
    Kiosk = 0,
    Mobile = 1,
}

/// <summary>
/// One thing that happened on a kiosk or in the app, for the pilot's
/// questions (plan section 2.17): which formats arrive, where conversion
/// fails, on which screen people give up, how long an order takes.
/// </summary>
/// <remarks>
/// <para>
/// Our own analytics - no third party. Kept in a MongoDB time-series
/// collection that expires after <c>Analytics.RetentionDays</c>.
/// </para>
/// <para>
/// Only the names and properties <see cref="AnalyticsCatalog"/> lists are
/// kept, so nothing personal - a file name, an e-mail - can slip in through a
/// property; an anonymous customer has no <see cref="UserId"/>.
/// </para>
/// </remarks>
public sealed class AnalyticsEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Timestamp { get; set; }

    public AnalyticsSource Source { get; set; }

    public Guid? KioskId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>One customer's visit to the kiosk, from the first touch to the order or the idle reset.</summary>
    public string SessionId { get; set; } = string.Empty;

    public string Event { get; set; } = string.Empty;

    public Dictionary<string, string> Props { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The events a kiosk may send and the properties each may carry (plan
/// section 2.17).
/// </summary>
/// <remarks>
/// Money, refunds and printing are not counted from these events but from
/// the records that hold them (payments, refunds, print attempts): an event
/// can be lost on a kiosk that stays offline, a payment cannot. The events
/// say what only the screen knows - which file, which screen, how long.
/// </remarks>
public static class AnalyticsCatalog
{
    /// <summary>The longest property value kept.</summary>
    public const int MaxValueLength = 64;

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> KioskEvents =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["screen_view"] = Keys("screen"),
            ["file_selected"] = Keys("format", "sizeMb", "pages"),
            ["conversion_failed"] = Keys("format", "error"),
            ["options_set"] = Keys("copies", "color", "duplex", "paper", "pagesPerSheet", "range"),
            ["price_shown"] = Keys("total", "currency"),
            ["order_placed"] = Keys("sheets", "jobCode"),
            ["print_completed"] = Keys("sheets", "jobCode"),
            ["print_failed"] = Keys("sheets", "error", "jobCode"),
            ["flow_abandoned"] = Keys("lastScreen", "seconds"),

            // T-214: the payment screens. The kiosk used to send them under
            // names this catalogue did not list, and every one was dropped.
            ["payment_started"] = Keys("method"),
            ["payment_succeeded"] = Keys("method", "seconds"),
            ["payment_failed"] = Keys("method", "seconds"),
            ["payment_expired"] = Keys("method", "seconds"),
        };

    /// <summary>
    /// The name and properties a kiosk built before T-214 sends, under today's
    /// name (T-282): such kiosks stay in the field until the update system of
    /// step 7B replaces them. <c>payment_method {method}</c> is
    /// <c>payment_started</c>; <c>payment_ended {status}</c> is
    /// <c>payment_failed</c> when the bank said no, <c>payment_expired</c>
    /// otherwise - it never said which method. Any other name is returned as it is.
    /// </summary>
    public static (string? Name, IReadOnlyDictionary<string, string?>? Props) Current(string? name, IReadOnlyDictionary<string, string?>? props)
        => name switch
        {
            "payment_method" => ("payment_started", props),
            "payment_ended" => (
                props?.GetValueOrDefault("status") == "Failed" ? "payment_failed" : "payment_expired",
                new Dictionary<string, string?>(StringComparer.Ordinal)),
            _ => (name, props),
        };

    /// <summary>
    /// The properties of <paramref name="name"/> the catalogue allows, cut to
    /// length; null when the event is not one a kiosk sends.
    /// </summary>
    public static Dictionary<string, string>? KioskProps(string? name, IReadOnlyDictionary<string, string?>? props)
    {
        if (name is null || !KioskEvents.TryGetValue(name, out var allowed))
        {
            return null;
        }

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in props ?? new Dictionary<string, string?>())
        {
            if (allowed.Contains(key) && !string.IsNullOrEmpty(value))
            {
                kept[key] = value.Length > MaxValueLength ? value[..MaxValueLength] : value;
            }
        }

        return kept;
    }

    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);
}
