using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// The customer's own jobs, newest first (plan section 6: <c>GET print-jobs/my</c>):
/// the ones paid with the app and the ones sent from it.
/// </summary>
public sealed record ListMyPrintJobsQuery(Guid UserId) : IRequest<IReadOnlyList<MyPrintJobView>>;

/// <summary>
/// The owner cancels their own order (plan section 6: <c>POST print-jobs/{id}/cancel</c>;
/// section 9.6, A.5: "ləğv edib məbləği qaytarın"). Allowed while the order
/// waits - in the queue, or stopped at the printer; what was paid goes back
/// by the rules of plan section 2.5 (<c>PrintJobCancelled</c> → the refund module).
/// </summary>
/// <exception cref="Domain.Common.NotFoundException">No such order of this user.</exception>
/// <exception cref="Domain.Common.DomainException">The order is printing, finished, or waiting for an administrator's decision.</exception>
public sealed record CancelMyPrintJobCommand(Guid UserId, Guid JobId) : IRequest<Unit>, Behaviors.ITransactionalRequest;

/// <summary>A job as its owner sees it in the app.</summary>
/// <param name="Amount">What the job costs after the promo code quoted at the kiosk; what was actually charged is on the receipt.</param>
/// <param name="Reason">Why it stopped or was cancelled, in the reader's language.</param>
/// <param name="ScheduledAt">A scheduled print still waiting for its time (T-158): when it is due.</param>
/// <param name="CanCancel">The owner may cancel it now and take the money back (<see cref="CancelMyPrintJobCommand"/>).</param>
/// <param name="KioskStopped">Its kiosk cannot print at the moment (plan section 9.6): waiting may take a while.</param>
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
    DateTime? ScheduledAt = null,
    bool CanCancel = false,
    bool KioskStopped = false);

public sealed class MyPrintJobHandlers :
    IRequestHandler<ListMyPrintJobsQuery, IReadOnlyList<MyPrintJobView>>,
    IRequestHandler<CancelMyPrintJobCommand, Unit>
{
    public const string CancelledByOwner = "printJobs.reason.cancelledByOwner";

    /// <summary>
    /// What the owner may cancel: an order that waits. One at the printer
    /// cannot be taken back, and one whose outcome nobody knows (Suspected)
    /// is an administrator's to decide (plan section 2.5, scenario B).
    /// </summary>
    private static bool Cancellable(Domain.PrintJobs.PrintJob job)
        => job.Status is Domain.PrintJobs.PrintJobStatus.Queued or Domain.PrintJobs.PrintJobStatus.Interrupted;

    /// <summary>The app shows recent jobs; the full history is the receipts' and the wallet's.</summary>
    private const int Take = 100;

    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly Schedules.IScheduledPrintRepository _schedules;

    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _notifier;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public MyPrintJobHandlers(
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        Schedules.IScheduledPrintRepository schedules,
        PrintQueueService queue,
        IKioskNotifier notifier,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock)
    {
        _jobs = jobs;
        _kiosks = kiosks;
        _schedules = schedules;
        _queue = queue;
        _notifier = notifier;
        _events = events;
        _correlation = correlation;
        _clock = clock;
    }

    /// <inheritdoc cref="CancelMyPrintJobCommand" />
    public async Task<Unit> Handle(CancelMyPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        // Somebody else's order is not there at all, as far as this user goes.
        if (job is null || job.UserId != request.UserId)
        {
            throw new Domain.Common.NotFoundException("printJob");
        }

        if (job.Status == Domain.PrintJobs.PrintJobStatus.Cancelled)
        {
            // Pressed twice: the first one did it.
            return Unit.Value;
        }

        if (!Cancellable(job))
        {
            throw new Domain.Common.DomainException(
                job.Status == Domain.PrintJobs.PrintJobStatus.Printing ? "printJobs.cancelWhilePrinting" : "printJobs.cancelNotWaiting");
        }

        await PrintJobCancellation
            .CancelAsync(
                job,
                new Domain.Common.UserMessage(CancelledByOwner),
                Domain.Common.ActorContext.User(request.UserId, correlationId: _correlation.CorrelationId),
                _clock.UtcNow,
                _jobs,
                _queue,
                _notifier,
                _events,
                _correlation,
                cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
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
                    waiting.TryGetValue(job.Id, out var at) ? at : null,
                    Cancellable(job),
                    !job.IsFinished && kiosk?.FaultSince is not null);
            })
            .ToList();
    }
}
