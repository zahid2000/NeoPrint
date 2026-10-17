namespace NeoPrint.Domain.Analytics;

/// <summary>
/// How many of a kiosk's analytics events were not kept on one Baku day, and
/// why (T-282): an unknown event is never dropped without a trace.
/// </summary>
/// <remarks>
/// Counts only. <see cref="LastUnknownName"/> is the last unknown name as the
/// kiosk sent it, reduced to safe characters and cut to 40 - an event name,
/// never a value.
/// </remarks>
public sealed class AnalyticsRejectionDay
{
    /// <summary><c>yyyy-MM-dd:{kiosk id}</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public string Day { get; set; } = string.Empty;

    public Guid KioskId { get; set; }

    public int UnknownEvent { get; set; }

    public int NoSession { get; set; }

    public int OutOfTime { get; set; }

    public string? LastUnknownName { get; set; }

    public DateTime LastAt { get; set; }

    public int Total => UnknownEvent + NoSession + OutOfTime;

    public static string IdOf(string day, Guid kioskId) => $"{day}:{kioskId:N}";
}
