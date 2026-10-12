namespace NeoPrint.Domain.Supplies;

/// <summary>When a supply is expected to run out, and on what that rests.</summary>
/// <param name="PerDay">What is used in a day: sheets, or percent of a cartridge.</param>
/// <param name="RunsOutAt">Null when nothing is being used, or the end is too far to be worth a date.</param>
public sealed record SupplyForecast(decimal PerDay, DateTime? RunsOutAt);

/// <summary>
/// The forecast of plan section 2.15: the daily use of the last fourteen days
/// as a weighted average - yesterday counts fourteen times what the day two
/// weeks ago does - and from it the day the supply runs out.
/// </summary>
/// <remarks>
/// Arithmetic only: it is given the days and knows nothing of where they came
/// from, so it can be tested without a database.
/// </remarks>
public static class SupplyForecaster
{
    /// <summary>How many days of history the forecast looks at.</summary>
    public const int Days = 14;

    /// <summary>A supply that lasts longer than this has no date: "more than a year" is all anybody needs.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(365);

    /// <summary>
    /// The weighted average of a daily series, oldest day first: the n-th day
    /// weighs n.
    /// </summary>
    public static decimal WeightedDailyAverage(IReadOnlyList<decimal> daily)
    {
        ArgumentNullException.ThrowIfNull(daily);

        decimal sum = 0;
        decimal weights = 0;

        for (var index = 0; index < daily.Count; index++)
        {
            var weight = index + 1;

            sum += Math.Max(0, daily[index]) * weight;
            weights += weight;
        }

        return weights == 0 ? 0 : sum / weights;
    }

    /// <summary>What is left divided by what goes in a day.</summary>
    public static SupplyForecast Forecast(decimal current, decimal perDay, DateTime now)
    {
        if (perDay <= 0)
        {
            return new SupplyForecast(0, null);
        }

        if (current <= 0)
        {
            return new SupplyForecast(perDay, now);
        }

        var days = (double)(current / perDay);

        return new SupplyForecast(perDay, days > Horizon.TotalDays ? null : now.AddDays(days));
    }

    /// <summary>Paper: the sheets used on each of the last days, oldest first.</summary>
    public static SupplyForecast ForPaper(int currentSheets, IReadOnlyList<decimal> sheetsPerDay, DateTime now)
        => Forecast(currentSheets, WeightedDailyAverage(sheetsPerDay), now);

    /// <summary>
    /// A toner or a drum: what a sheet costs it, times the sheets of a day.
    /// </summary>
    /// <param name="levels">
    /// The level the printer reported over the period, oldest first. Only the
    /// falls count: a rise is a new cartridge, not negative use.
    /// </param>
    /// <param name="sheetsInPeriod">The sheets printed while those levels were read.</param>
    public static SupplyForecast ForPercent(
        int currentPercent, IReadOnlyList<int> levels, decimal sheetsInPeriod, IReadOnlyList<decimal> sheetsPerDay, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(levels);

        decimal used = 0;

        for (var index = 1; index < levels.Count; index++)
        {
            var fall = levels[index - 1] - levels[index];

            if (fall > 0)
            {
                used += fall;
            }
        }

        if (used <= 0 || sheetsInPeriod <= 0)
        {
            return new SupplyForecast(0, null);
        }

        return Forecast(currentPercent, used / sheetsInPeriod * WeightedDailyAverage(sheetsPerDay), now);
    }
}
