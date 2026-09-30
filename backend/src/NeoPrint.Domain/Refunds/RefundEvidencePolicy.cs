using System.Globalization;

using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Refunds;

/// <summary>One try at printing, as far as the refund needs to know it.</summary>
/// <param name="CounterUnit"><c>sheets</c> or <c>impressions</c>: what the printer's counter counts.</param>
/// <param name="Status">The kiosk's last report for the try: Printing, Completed or Interrupted.</param>
public sealed record AttemptEvidence(long? CounterBefore, long? CounterAfter, string? CounterUnit, string? SpoolerJobId, string Status);

/// <summary>
/// How much of an interrupted job was printed, and so how much comes back
/// (plan section 2.5: "çap olunmamış tərəflərin dəyəri qaytarılır"; the
/// owner's rule of 2026-09-30).
/// </summary>
/// <remarks>
/// <para>
/// Sources in the order they are trusted: the printer's own counter, then the
/// spooler, then what the kiosk reported. With none of them the whole amount
/// comes back.
/// </para>
/// <para>
/// When sources disagree the customer gets the benefit of the doubt: the
/// smallest printed count wins. The chosen source and every value seen are
/// kept with the refund, so an admin can see why.
/// </para>
/// </remarks>
public static class RefundEvidencePolicy
{
    public static RefundEvidence Decide(int orderedSides, bool duplex, IReadOnlyList<AttemptEvidence> attempts, int? kioskReportedSides)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var found = new List<(EvidenceSource Source, int Printed)>();

        // 1. The printer's counter: the most direct evidence there is.
        var counted = attempts
            .Where(a => a.CounterBefore is { } before && a.CounterAfter is { } after && after >= before)
            .Select(a => SidesFromCounter(a.CounterAfter!.Value - a.CounterBefore!.Value, a.CounterUnit, duplex))
            .ToList();

        if (counted.Count > 0)
        {
            found.Add((EvidenceSource.PrinterCounter, Math.Min(orderedSides, counted.Sum())));
        }

        // 2. The spooler: it only ever says "all of it went out".
        if (attempts.Any(a => a.SpoolerJobId is not null && a.Status == "Completed"))
        {
            found.Add((EvidenceSource.Spooler, orderedSides));
        }

        // 3. What the kiosk reported.
        if (kioskReportedSides is { } reported and >= 0)
        {
            found.Add((EvidenceSource.KioskJournal, Math.Min(orderedSides, reported)));
        }

        var detail = found.Count == 0
            ? "none"
            : string.Join("; ", found.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Source}={f.Printed}")));

        if (found.Count == 0)
        {
            return new RefundEvidence(EvidenceSource.None, 0, orderedSides, detail);
        }

        // Disagreement goes the customer's way; ties keep the more trusted source.
        var chosen = found.OrderBy(f => f.Printed).ThenBy(f => (int)f.Source).First();

        return new RefundEvidence(chosen.Source, chosen.Printed, orderedSides, detail);
    }

    /// <summary>
    /// The money for the sides that did not print: all of it when nothing
    /// printed, none when everything did, the unprinted share otherwise -
    /// rounded the one way money is rounded.
    /// </summary>
    public static Money Unprinted(Money paid, RefundEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.OrderedSides <= 0 || evidence.PrintedSides <= 0)
        {
            return paid;
        }

        if (evidence.PrintedSides >= evidence.OrderedSides)
        {
            return Money.Zero;
        }

        return Money.FromAzn(paid.Amount * (evidence.OrderedSides - evidence.PrintedSides) / evidence.OrderedSides);
    }

    private static int SidesFromCounter(long delta, string? unit, bool duplex)
        => (int)Math.Min(int.MaxValue, unit == "sheets" && duplex ? delta * 2 : delta);
}
