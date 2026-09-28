using NeoPrint.Domain.Common;
using NeoPrint.Pricing;

namespace NeoPrint.Domain.PrintJobs;

/// <summary>Portrait, landscape, or whatever each page is (plan section 2.3).</summary>
public enum PageOrientation
{
    Auto = 0,
    Portrait = 1,
    Landscape = 2,
}

/// <summary>Fit the page to the paper, or print it at its real size (plan section 2.3).</summary>
public enum PageScaling
{
    Fit = 0,

    /// <summary>100%: required for layouts, where a card must come out card-sized (plan section 2.11).</summary>
    Actual = 1,
}

/// <summary>
/// What the customer chose on the print options screen (plan section 2.3).
/// </summary>
/// <param name="Copies">1–50.</param>
/// <param name="PageRange">Null or empty for every page, otherwise <c>1-3,5,8-10</c>.</param>
/// <param name="Color">Colour rather than black and white.</param>
/// <param name="Duplex">Both sides of the sheet.</param>
/// <param name="Paper">A4 or A3.</param>
/// <param name="PagesPerSheet">1, 2 or 4.</param>
public sealed record PrintOptions(
    int Copies,
    string? PageRange,
    bool Color,
    bool Duplex,
    string Paper,
    PageOrientation Orientation,
    int PagesPerSheet,
    PageScaling Scaling)
{
    public const int MaxCopies = 50;

    public static readonly IReadOnlyList<string> Papers = ["A4", "A3"];

    public static readonly IReadOnlyList<int> PagesPerSheetChoices = [1, 2, 4];

    /// <summary>One black-and-white single-sided A4 copy of everything.</summary>
    public static readonly PrintOptions Default =
        new(1, null, false, false, "A4", PageOrientation.Auto, 1, PageScaling.Fit);

    /// <summary>
    /// What is wrong with these options for a document of
    /// <paramref name="pageCount"/> pages, as field and message key; empty
    /// when they are fine.
    /// </summary>
    public IReadOnlyList<(string Field, UserMessage Message)> Problems(int pageCount)
    {
        var problems = new List<(string, UserMessage)>();

        if (Copies is < 1 or > MaxCopies)
        {
            problems.Add(("copies", UserMessage.Of("printJobs.copiesRange", ("max", MaxCopies))));
        }

        if (!Papers.Contains(Paper))
        {
            problems.Add(("paper", new UserMessage("printJobs.paperInvalid")));
        }

        if (!PagesPerSheetChoices.Contains(PagesPerSheet))
        {
            problems.Add(("pagesPerSheet", new UserMessage("printJobs.pagesPerSheetInvalid")));
        }

        if (!Enum.IsDefined(Orientation) || !Enum.IsDefined(Scaling))
        {
            problems.Add(("options", new UserMessage("printJobs.optionsInvalid")));
        }

        if (!PrintJobs.PageRange.TryParse(PageRange, pageCount, out _))
        {
            problems.Add(("pageRange", UserMessage.Of("printJobs.pageRangeInvalid", ("count", pageCount))));
        }

        return problems;
    }

    /// <summary>The pages that will be printed, in order.</summary>
    /// <exception cref="DomainException">The range does not fit the document.</exception>
    public IReadOnlyList<int> SelectedPages(int pageCount)
        => PrintJobs.PageRange.TryParse(PageRange, pageCount, out var pages)
            ? pages
            : throw new DomainException(UserMessage.Of("printJobs.pageRangeInvalid", ("count", pageCount)));

    /// <summary>The specification the price is computed from (plan section 2.3, formula).</summary>
    public PrintSpec ToSpec(int pageCount)
        => new(SelectedPages(pageCount).Count, PagesPerSheet, Copies, Duplex, Color, Paper);
}

/// <summary>
/// A page range as a person types it: <c>1-3,5,8-10</c> (plan section 2.3).
/// </summary>
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
                if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out from))
                {
                    return false;
                }

                to = from;
            }
            else if (!int.TryParse(part.AsSpan(0, dash).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out from)
                     || !int.TryParse(part.AsSpan(dash + 1).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out to))
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
