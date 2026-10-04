using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// The customer's own jobs, newest first (plan section 6: <c>GET print-jobs/my</c>):
/// the ones paid with the app and the ones sent from it.
/// </summary>
public sealed record ListMyPrintJobsQuery(Guid UserId) : IRequest<IReadOnlyList<MyPrintJobView>>;

/// <summary>A job as its owner sees it in the app.</summary>
/// <param name="Amount">What the job costs after the promo code quoted at the kiosk; what was actually charged is on the receipt.</param>
/// <param name="Reason">Why it stopped or was cancelled, in the reader's language.</param>
/// <param name="ScheduledAt">A scheduled print still waiting for its time (T-158): when it is due.</param>
public sealed record MyPrintJobView(
    Guid Id,
    string JobCode,
    string Status,
    Guid KioskId,
    string? KioskCode,
    string? KioskName,
    string? FileName,
    int Pages,
    int Copies,
    bool Color,
    string Paper,
    decimal Amount,
    string Currency,
    int PrintedSides,
    string? Reason,
    Guid? PaymentId,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    DateTime? ScheduledAt = null);

public sealed class MyPrintJobHandlers : IRequestHandler<ListMyPrintJobsQuery, IReadOnlyList<MyPrintJobView>>
{
    /// <summary>The app shows recent jobs; the full history is the receipts' and the wallet's.</summary>
    private const int Take = 100;

    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly Schedules.IScheduledPrintRepository _schedules;

    public MyPrintJobHandlers(IPrintJobRepository jobs, IKioskRepository kiosks, Schedules.IScheduledPrintRepository schedules)
    {
        _jobs = jobs;
        _kiosks = kiosks;
        _schedules = schedules;
    }

    /// <inheritdoc cref="ListMyPrintJobsQuery" />
    public async Task<IReadOnlyList<MyPrintJobView>> Handle(ListMyPrintJobsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var jobs = await _jobs.ListForUserAsync(request.UserId, Take, cancellationToken).ConfigureAwait(false);
        var kiosks = new Dictionary<Guid, Domain.Kiosks.Kiosk?>();

        foreach (var kioskId in jobs.Select(job => job.KioskId).Distinct())
        {
            kiosks[kioskId] = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);
        }

        // A scheduled print waiting for its time is "Planlanıb · {time}" in the
        // app, not "Paid" - it is not waiting at a kiosk (T-158).
        var waiting = jobs.Any(job => job.Source == Domain.PrintJobs.PrintJobSource.Scheduled)
            ? (await _schedules.ListForUserAsync(request.UserId, Take, cancellationToken).ConfigureAwait(false))
                .Where(schedule => schedule.Status is Domain.Schedules.ScheduledPrintStatus.Scheduled or Domain.Schedules.ScheduledPrintStatus.AwaitingUser)
                .ToDictionary(schedule => schedule.JobId, schedule => schedule.ScheduledAt)
            : [];

        return jobs
            .Select(job =>
            {
                var kiosk = kiosks[job.KioskId];

                return new MyPrintJobView(
                    job.Id,
                    job.JobCode,
                    job.Status.ToString(),
                    job.KioskId,
                    kiosk?.Code,
                    kiosk?.Name,
                    job.LocalFileName,
                    job.Price.Price.Pages,
                    job.Options.Copies,
                    job.Options.Color,
                    job.Options.Paper,
                    job.Price.Price.FinalPrice - job.PromoQuote,
                    job.Price.Price.Currency,
                    job.PrintedSides,
                    Messages.WriteStored(job.ErrorReason ?? job.CancelReason),
                    job.PaymentId,
                    job.CreatedAt,
                    job.FinishedAt,
                    waiting.TryGetValue(job.Id, out var at) ? at : null);
            })
            .ToList();
    }
}
