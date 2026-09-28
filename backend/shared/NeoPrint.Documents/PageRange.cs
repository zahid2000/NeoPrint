using System.Globalization;

namespace NeoPrint.Documents;

/// <summary>
/// A page range as a person types it: <c>1-3,5,8-10</c> (plan section 2.3).
/// </summary>
/// <remarks>
/// Shared so that the kiosk's options screen accepts exactly what the backend
/// accepts, and prices the same pages.
/// </remarks>
public static class PageRange
{
    /// <summary>
    /// The pages a range selects, in the order written; null or blank means
    /// every page. False for anything malformed, empty, reversed or outside
    /// the document.
    /// </summary>
    public static bool TryParse(string? range, int pageCount, out IReadOnlyList<int> pages)
    {
        pages = [];

        if (pageCount < 1)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(range))
        {
            pages = Enumerable.Range(1, pageCount).ToList();
            return true;
        }

        var selected = new List<int>();
        var seen = new HashSet<int>();

        foreach (var part in range.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0)
            {
                return false;
            }

            var dash = part.IndexOf('-', StringComparison.Ordinal);
            int from;
            int to;

            if (dash < 0)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out from))
                {
                    return false;
                }

                to = from;
            }
            else if (!int.TryParse(part.AsSpan(0, dash).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out from)
                     || !int.TryParse(part.AsSpan(dash + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out to))
            {
                return false;
            }

            if (from < 1 || to > pageCount || from > to)
            {
                return false;
            }

            for (var page = from; page <= to; page++)
            {
                // A page named twice prints once: "1-3,2" is three pages, not four.
                if (seen.Add(page))
                {
                    selected.Add(page);
                }
            }
        }

        pages = selected;
        return selected.Count > 0;
    }
}

/// <summary>How much paper a job takes (plan section 2.15).</summary>
public static class PrintSheets
{
    /// <summary>
    /// Sheets the printer actually uses: each copy starts on a fresh sheet, so
    /// five pages on both sides take three sheets a copy, not two and a half.
    /// </summary>
    /// <remarks>
    /// The plan's formula (<c>ceil(sides / 2)</c>, section 2.3) prices the job
    /// and stays as it is; for the paper it would undercount every odd-paged
    /// double-sided copy, and the counter would drift from the tray
    /// (docs/DECISIONS.md).
    /// </remarks>
    public static int Physical(int selectedPages, int pagesPerSheet, int copies, bool duplex)
    {
        var faces = (selectedPages + Math.Max(1, pagesPerSheet) - 1) / Math.Max(1, pagesPerSheet);
        var perCopy = duplex ? (faces + 1) / 2 : faces;

        return perCopy * copies;
    }
}
