namespace NeoPrint.Application.Common;

/// <summary>
/// Asia/Baku, where people read the times (plan section 2.1: the server stores
/// UTC, what people see is Baku time). The IANA name, the Windows name, or -
/// on a machine that knows neither - a fixed UTC+4: Azerbaijan has had no
/// daylight saving time since 2016.
/// </summary>
public static class BakuTime
{
    public static TimeZoneInfo Zone { get; } = Find();

    public static DateTime From(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Baku", "Azerbaijan Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // The next name.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Asia/Baku", TimeSpan.FromHours(4), "Asia/Baku", "Asia/Baku");
    }
}
