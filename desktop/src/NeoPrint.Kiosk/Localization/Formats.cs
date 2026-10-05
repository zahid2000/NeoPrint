using System.Globalization;

namespace NeoPrint.Kiosk.Localization;

/// <summary>
/// Dates, times, money and page counts as docs/DESIGN_SYSTEM.md section 6 writes
/// them, in one place for the whole kiosk. The admin panel
/// (<c>shared/format.pipes.ts</c>) and the mobile app
/// (<c>core/format/formats.dart</c>) follow the same table.
/// </summary>
/// <remarks>
/// Dates read the same in every language - <c>27.09.2026 18:33</c>, 24-hour -
/// with "today" and "yesterday" as words. Numbers follow the language: a
/// decimal comma and spaced thousands in Azerbaijani and Russian, a decimal
/// point and comma thousands in English.
/// </remarks>
public static class Formats
{
    private const char NoBreakSpace = ' ';

    /// <summary><c>18:33</c>.</summary>
    public static string Time(DateTime value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>27.09.2026 18:33</c> always: for paper, which is read on another day.</summary>
    public static string Absolute(DateTime value) => value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>27.09.2026 18:33</c>, or <c>Bu gün 18:33</c> / <c>Dünən 18:33</c>.</summary>
    public static string DateTime(DateTime value, string language, DateTime now)
    {
        var days = (now.Date - value.Date).Days;

        return days switch
        {
            0 => $"{Strings.Get(language, "format.today")} {Time(value)}",
            1 => $"{Strings.Get(language, "format.yesterday")} {Time(value)}",
            _ => value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
        };
    }

    /// <summary><c>1 240,00 ₼</c> (az, ru) or <c>1,240.00 ₼</c> (en).</summary>
    public static string Money(decimal amount, string language)
    {
        var english = language == "en";

        var format = new NumberFormatInfo
        {
            NumberDecimalSeparator = english ? "." : ",",
            NumberGroupSeparator = english ? "," : NoBreakSpace.ToString(),
            NumberGroupSizes = [3],
        };

        var text = Math.Abs(amount).ToString("N2", format);

        return $"{(amount < 0 ? "−" : string.Empty)}{text}{NoBreakSpace}₼";
    }

    /// <summary><c>1,2 MB</c> (az), <c>1.2 MB</c> (en), <c>1,2 МБ</c> (ru): a file size (DESIGN_SYSTEM.md section 6).</summary>
    public static string Megabytes(double value, string language)
        => $"{Number(value, language)}{NoBreakSpace}{(language == "ru" ? "МБ" : "MB")}";

    /// <summary><c>340 KB</c> / <c>340 КБ</c>.</summary>
    public static string Kilobytes(double value, string language)
        => $"{Math.Round(value).ToString("0", CultureInfo.InvariantCulture)}{NoBreakSpace}{(language == "ru" ? "КБ" : "KB")}";

    /// <summary><c>85,6</c> (az, ru) or <c>85.6</c> (en): a length in millimetres, to a hundredth.</summary>
    public static string Millimetres(double value, string language)
    {
        var text = value.ToString("0.##", CultureInfo.InvariantCulture);
        return language == "en" ? text : text.Replace('.', ',');
    }

    private static string Number(double value, string language)
    {
        var text = value.ToString(value >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);
        return language == "en" ? text : text.Replace('.', ',');
    }

    /// <summary>
    /// <c>12 səhifə</c> / <c>12 pages</c> / <c>12 страниц</c>, never
    /// abbreviated (DESIGN_SYSTEM.md section 6).
    /// </summary>
    public static string Pages(int count, string language)
        => Strings.Get(language, $"format.pages.{PluralForm(count, language)}", count);

    /// <summary>
    /// Which plural form a number takes: Russian has three (1 страница,
    /// 2 страницы, 5 страниц), English two, Azerbaijani one.
    /// </summary>
    public static string PluralForm(int count, string language)
    {
        var n = Math.Abs(count);

        if (language == "ru")
        {
            if (n % 10 == 1 && n % 100 != 11)
            {
                return "one";
            }

            return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
        }

        return n == 1 ? "one" : "many";
    }
}
