using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.PrintJobs;

/// <summary>
/// The options the user already chose in the app; the rest are chosen at the
/// kiosk (the owner's rule for plan section 9.4, 2026-10-03: "seçimlər qismən
/// də verilə bilər"). A null field is "choose at the kiosk".
/// </summary>
public sealed record PreparedOptions(
    int? Copies = null,
    bool? Color = null,
    bool? Duplex = null,
    string? Paper = null,
    int? PagesPerSheet = null,
    string? PageRange = null)
{
    public static readonly PreparedOptions None = new();

    /// <summary>What is wrong with the given ones for a document of <paramref name="pageCount"/> pages; empty when fine.</summary>
    public IReadOnlyList<(string Field, UserMessage Message)> Problems(int pageCount)
        => Complete(PrintOptions.Default).Problems(pageCount);

    /// <summary>The same without the page range, which needs the document's page count.</summary>
    public IReadOnlyList<(string Field, UserMessage Message)> ProblemsBesidesRange()
        => (Complete(PrintOptions.Default) with { PageRange = null }).Problems(1);

    /// <summary>These choices over <paramref name="rest"/>: what the kiosk starts its options screen with.</summary>
    public PrintOptions Complete(PrintOptions rest)
    {
        ArgumentNullException.ThrowIfNull(rest);

        return rest with
        {
            Copies = Copies ?? rest.Copies,
            Color = Color ?? rest.Color,
            Duplex = Duplex ?? rest.Duplex,
            Paper = Paper ?? rest.Paper,
            PagesPerSheet = PagesPerSheet ?? rest.PagesPerSheet,
            PageRange = string.IsNullOrWhiteSpace(PageRange) ? rest.PageRange : PageRange.Trim(),
        };
    }
}

/// <summary>
/// A file the user got ready in the app to print at any kiosk, unpaid (plan
/// section 9.4, step 1; <c>docs/DECISIONS.md</c> T-144). It is priced and
/// paid at the kiosk where the user shows their QR, by that kiosk's current
/// profile. A preparation paid in advance is a <see cref="PrintJob"/> held for
/// its owner instead (<see cref="PrintJob.HeldForUser"/>).
/// </summary>
public sealed class PreparedPrint : Entity
{
    public Guid UserId { get; private set; }

    public Guid FileId { get; private set; }

    public PreparedOptions Options { get; private set; } = PreparedOptions.None;

    /// <summary>The order the last kiosk made from it: printed or printing, it is not offered again.</summary>
    public Guid? LastJobId { get; private set; }

    public DateTime? RemovedAt { get; private set; }

    public bool IsRemoved => RemovedAt is not null;

    public static PreparedPrint Create(Guid userId, Guid fileId, PreparedOptions options, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new PreparedPrint
        {
            UserId = userId,
            FileId = fileId,
            Options = options,
            CreatedAt = now,
        };
    }

    /// <summary>A kiosk placed an order from it.</summary>
    public void Ordered(Guid jobId)
    {
        if (IsRemoved)
        {
            throw new DomainException("preparedPrints.removed");
        }

        LastJobId = jobId;
    }

    /// <summary>The user took it off the list; removing it twice is no error.</summary>
    public void Remove(DateTime now) => RemovedAt ??= now;
}
