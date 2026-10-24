using System.Globalization;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// The hours updates may be applied in (<c>Kiosk.UpdateWindow</c>, "02:00-05:00"),
/// in the kiosk's local time; a window may run past midnight ("23:00-02:00").
/// </summary>
public sealed record UpdateWindow(TimeOnly From, TimeOnly To)
{
    /// <summary>The setting's default, and what an unreadable value falls back to.</summary>
    public static readonly UpdateWindow Default = new(new TimeOnly(2, 0), new TimeOnly(5, 0));

    public static UpdateWindow Parse(string? text)
    {
        var parts = (text ?? string.Empty).Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
               && TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
               && from != to
            ? new UpdateWindow(from, to)
            : Default;
    }

    /// <summary>Whether the local time is inside the window.</summary>
    public bool Contains(TimeOnly local) => From < To
        ? local >= From && local < To
        : local >= From || local < To;
}
