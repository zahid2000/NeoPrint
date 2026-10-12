using System.Security.Cryptography;

using MediatR;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Pricing;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;
using NeoPrint.Pricing;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// The print job use cases the kiosk drives (plan sections 7.3–7.5, 9.1).
/// </summary>
public sealed class KioskPrintJobHandlers :
    IRequestHandler<CreateKioskPrintJobCommand, PrintJobResponse>,
    IRequestHandler<GetKioskPrintJobQuery, PrintJobResponse>,
    IRequestHandler<FindKioskPrintJobByCodeQuery, PrintJobResponse>,
    IRequestHandler<CancelKioskPrintJobCommand, PrintJobResponse>,
    IRequestHandler<ReportPrintJobStatusCommand, PrintJobResponse>,
    IRequestHandler<GetKioskQueueQuery, IReadOnlyList<QueueEntry>>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly PrintOrderRules _rules;
    private readonly IPrintJobRepository _jobs;
    private readonly IPrintAttemptRepository _attempts;
    private readonly ISupplyEventRepository _supplies;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _notifier;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly PrintJobOptions _options;
    private readonly SuspectedJobIncidents _suspected;
    private readonly Promos.PromoService _promos;
    private readonly ILogger<KioskPrintJobHandlers> _logger;

    public KioskPrintJobHandlers(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        PrintOrderRules rules,
        IPrintJobRepository jobs,
        IPrintAttemptRepository attempts,
        ISupplyEventRepository supplies,
        PrintQueueService queue,
        IKioskNotifier notifier,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        IOptions<PrintJobOptions> options,
        SuspectedJobIncidents suspected,
        Promos.PromoService promos,
        ILogger<KioskPrintJobHandlers> logger)
    {
        _devices = devices;
        _kiosks = kiosks;
        _rules = rules;
        _jobs = jobs;
        _attempts = attempts;
        _supplies = supplies;
        _queue = queue;
        _notifier = notifier;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _options = options.Value;
        _suspected = suspected;
        _promos = promos;
        _logger = logger;
    }

    /// <inheritdoc cref="CreateKioskPrintJobCommand" />
    public async Task<PrintJobResponse> Handle(CreateKioskPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SkipPayment && !_options.AllowPaymentSkip)
        {
            // Outside Development a job without a payment is a free print.
            _logger.LogWarning("Device {DeviceId} asked to skip payment outside Development.", request.DeviceId);
            throw Invalid("dev", "printJobs.paymentSkipNotAllowed");
        }

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);

        if (kiosk.Status != KioskStatus.Online)
        {
            throw new DomainException(UserMessage.Of("printJobs.kioskNotTakingOrders", ("status", kiosk.Status)));
        }

        var now = _clock.UtcNow;
        var price = await _rules.CheckAndPriceAsync(kiosk, request.PageCount, request.Options, now, cancellationToken).ConfigureAwait(false);

        Promos.PromoCheck? promo = null;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            // Checked now so the customer knows before paying; the place is taken
            // when the payment opens (plan section 2.7). No account here: no per-account limit.
            promo = await _promos.CheckAsync(request.PromoCode, kiosk.Id, price.ProfileId, price.Total, userId: null, cancellationToken).ConfigureAwait(false);

            if (promo.Refusal is { } refusal)
            {
                throw new ValidationException([new ValidationError("promoCode", refusal)]);
            }
        }

        var actor = ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId);
        var job = await _rules
            .AddWithUniqueCodeAsync(
                code => PrintJob.Create(
                    code,
                    kiosk.Id,
                    PrintJobSource.Flash,
                    request.PageCount,
                    request.Options,
                    price,
                    now,
                    localFileName: string.IsNullOrWhiteSpace(request.LocalFileName) ? null : Path.GetFileName(request.LocalFileName.Trim())),
                cancellationToken)
            .ConfigureAwait(false);

        if (promo is { Promo: { } code })
        {
            job.AttachPromo(code.Id, code.Code, promo.Discount.Amount);
        }

        if (request.SkipPayment)
        {
            job.SkipPaymentForDevelopment(actor.Because("printJobs.reason.paymentSkipped"), now);
            job.Enqueue(actor, now);
        }
        else
        {
            // Payment arrives in step 4; until then the job waits here.
            job.AwaitPayment(actor, now);
        }

        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        if (job.Status == PrintJobStatus.Queued)
        {
            await OnQueuedAsync(job, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Print job {JobId} created at kiosk {KioskId}: {Status}, {Sheets} sheets.",
            job.Id,
            job.KioskId,
            job.Status,
            job.RequiredSheets);

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="GetKioskPrintJobQuery" />
    public async Task<PrintJobResponse> Handle(GetKioskPrintJobQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await OwnJobAsync(request.DeviceId, request.JobId, cancellationToken).ConfigureAwait(false);

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FindKioskPrintJobByCodeQuery" />
    public async Task<PrintJobResponse> Handle(FindKioskPrintJobByCodeQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var code = request.JobCode?.Trim() ?? string.Empty;

        // Another kiosk's job reads as no job: the code is typed on a public
        // screen, and must not reveal what was ordered elsewhere.
        var job = code.Length == 6 && code.All(char.IsAsciiDigit)
            ? await _jobs.FindByCodeAsync(kiosk.Id, code, cancellationToken).ConfigureAwait(false)
            : null;

        return job is null
            ? throw new NotFoundException("printJob")
            : await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CancelKioskPrintJobCommand" />
    public async Task<PrintJobResponse> Handle(CancelKioskPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await OwnJobAsync(request.DeviceId, request.JobId, cancellationToken).ConfigureAwait(false);
        var actor = ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId);

        await PrintJobCancellation.CancelAsync(
                job,
                new UserMessage("printJobs.reason.cancelledAtKiosk"),
                actor,
                _clock.UtcNow,
                _jobs,
                _queue,
                _notifier,
                _events,
                _correlation,
                cancellationToken)
            .ConfigureAwait(false);

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ReportPrintJobStatusCommand" />
    public async Task<PrintJobResponse> Handle(ReportPrintJobStatusCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await OwnJobAsync(request.DeviceId, request.JobId, cancellationToken).ConfigureAwait(false);
        var actor = ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId);
        var now = _clock.UtcNow;
        var before = job.Status;

        if (request.Attempt is { } attempt)
        {
            // Kept whatever happens to the job below: the evidence matters
            // most when the report no longer changes anything.
            await _attempts.UpsertAsync(
                    new PrintAttempt
                    {
                        JobId = job.Id,
                        AttemptNo = attempt.Number,
                        KioskId = job.KioskId,
                        PrinterName = attempt.Printer,
                        StartedAt = attempt.StartedAt,
                        FinishedAt = attempt.FinishedAt,
                        SpoolerJobId = attempt.SpoolerJobId,
                        PrinterCounterBefore = attempt.CounterBefore,
                        PrinterCounterAfter = attempt.CounterAfter,
                        CounterUnit = attempt.CounterUnit,
                        ReportedSides = request.PrintedSides,
                        ReportedSheets = request.PrintedSheets,
                        Status = request.Status,
                        ErrorCode = request.ErrorCode,
                        Recovered = attempt.Recovered,
                        UpdatedAt = now,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (await SettledBySuspicionRulesAsync(job, request.Status, cancellationToken).ConfigureAwait(false))
        {
            return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
        }

        switch (request.Status)
        {
            case "Printing":
                job.StartPrinting(actor, now);
                break;

            case "Completed":
                job.Complete(request.PrintedSides, request.PrintedSheets, actor, now);
                break;

            case "Interrupted":
                // An unknown code still stops the job; it reads as a general
                // printer error rather than as the raw key.
                var reason = request.ErrorCode is { } code && Messages.Has(code) ? code : "printer.error";
                job.Interrupt(new UserMessage(reason), request.PrintedSides, request.PrintedSheets, actor.Because(reason), now);
                break;

            default:
                throw Invalid("status", "printJobs.statusNotReportable");
        }

        if (job.Status == before)
        {
            // The same report twice - a retry after a lost response. The
            // first one did everything; this one changes nothing.
            return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
        }

        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        if (job.Status is PrintJobStatus.Completed or PrintJobStatus.Interrupted)
        {
            await UsePaperAsync(job, now, cancellationToken).ConfigureAwait(false);
        }

        await _queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(
                new PrintJobStatusChanged(job.Id, job.KioskId, before.ToString(), job.Status.ToString(), _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (job.Status == PrintJobStatus.Completed)
        {
            await _events.PublishAsync(
                    new PrintJobCompleted(job.Id, job.KioskId, job.Options.Paper, job.PrintedSheets, _correlation.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (job.Status == PrintJobStatus.Interrupted)
        {
            await _events.PublishAsync(
                    new PrintJobInterrupted(job.Id, job.KioskId, job.Options.Paper, job.PrintedSheets, _correlation.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (before == PrintJobStatus.Suspected)
        {
            // The kiosk came back and said what happened: no admin decision
            // is needed (plan section 2.5). Interrupted goes on to scenario A.
            await _suspected
                .CloseAsync(job.Id, null, UserMessage.Of("incidents.resolution.kioskReported", ("status", job.Status.ToString())), cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Suspected job {JobId} settled by its kiosk's report: {Status}.", job.Id, job.Status);
        }

        await _queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports about a job that is, or was, Suspected (plan section 2.5,
    /// scenario B) that change nothing: "still printing" while it is
    /// Suspected waits for the outcome, and anything an admin's decision has
    /// overtaken is kept as evidence and shown to the admins, never undone.
    /// </summary>
    /// <returns>True when the report is settled without a transition.</returns>
    private async Task<bool> SettledBySuspicionRulesAsync(PrintJob job, string reported, CancellationToken cancellationToken)
    {
        if (job.SuspectedAt is null)
        {
            return false;
        }

        if (job.Status == PrintJobStatus.Suspected)
        {
            return reported == "Printing";
        }

        var target = reported switch
        {
            "Printing" => PrintJobStatus.Printing,
            "Completed" => PrintJobStatus.Completed,
            "Interrupted" => PrintJobStatus.Interrupted,
            _ => (PrintJobStatus?)null,
        };

        if (target is not { } next || next == job.Status || PrintJob.Transitions.IsAllowed(job.Status, next))
        {
            return false;
        }

        await _suspected.RaiseLateReportAsync(job, reported, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Kiosk reported {Reported} for job {JobId} after an admin decided it ({Status}); nothing changed.", reported, job.Id, job.Status);

        return true;
    }

    /// <summary>
    /// The sheets the job used come off the kiosk's counter (plan section
    /// 2.15), in the same transaction as the status that ended this run of it
    /// - which the state machine allows once per run, so a report sent twice
    /// cannot count twice. A job that stopped and printed again after the
    /// printer returned used paper both times, and both count.
    /// </summary>
    private async Task UsePaperAsync(PrintJob job, DateTime now, CancellationToken cancellationToken)
    {
        var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk?.UsePaper(job.Options.Paper, job.PrintedSheets, SupplyEventType.JobPrinted, now, jobId: job.Id, run: Math.Max(1, job.Runs))
            is not { } movement)
        {
            return;
        }

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
        await _supplies.AddAsync(movement, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="GetKioskQueueQuery" />
    public async Task<IReadOnlyList<QueueEntry>> Handle(GetKioskQueueQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);

        return await _queue.EntriesAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnQueuedAsync(PrintJob job, CancellationToken cancellationToken)
    {
        await _queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new PrintJobQueued(job.Id, job.KioskId, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);
        await _notifier.JobAddedAsync(job.KioskId, job.Id, cancellationToken).ConfigureAwait(false);
        await _queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Kiosk> KioskOfAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (device?.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        return await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("kiosk");
    }

    /// <summary>A job of this device's kiosk; any other reads as missing.</summary>
    private async Task<PrintJob> OwnJobAsync(Guid deviceId, Guid jobId, CancellationToken cancellationToken)
    {
        var kiosk = await KioskOfAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);

        return job is null || job.KioskId != kiosk.Id
            ? throw new NotFoundException("printJob")
            : job;
    }

    private static ValidationException Invalid(string field, string key, params (string Name, object? Value)[] arguments)
        => new([new ValidationError(field, key, arguments)]);
}

/// <summary>Cancelling a job, the same way from the kiosk and from the panel.</summary>
internal static class PrintJobCancellation
{
    /// <remarks>
    /// A job at the printer cannot be taken back - the paper is already moving
    /// - so Printing refuses; everything before it may go. A paid job's money
    /// goes back through the refund module: PrintJobCancelled carries who
    /// cancelled it and why (plan section 2.5).
    /// </remarks>
    public static async Task CancelAsync(
        PrintJob job,
        UserMessage reason,
        ActorContext actor,
        DateTime now,
        IPrintJobRepository jobs,
        PrintQueueService queue,
        IKioskNotifier notifier,
        IEventPublisher events,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        if (job.Status == PrintJobStatus.Cancelled)
        {
            return;
        }

        if (job.Status == PrintJobStatus.Printing)
        {
            throw new DomainException("printJobs.cancelWhilePrinting");
        }

        var before = job.Status;
        job.Cancel(reason, actor.Because(reason.Key), now);

        await jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        await queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(
                new PrintJobStatusChanged(job.Id, job.KioskId, before.ToString(), job.Status.ToString(), correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        // The payments module closes open intents and sends back what was paid (plan section 2.5).
        await events.PublishAsync(
                new PrintJobCancelled(
                    job.Id, job.KioskId, before.ToString(), job.PaymentId, actor.ActorType.ToString(), reason.Key, correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);
        await notifier.JobCancelledAsync(job.KioskId, job.Id, cancellationToken).ConfigureAwait(false);
        await queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The print job use cases of the admin panel (plan section 8A.3).</summary>
public sealed class AdminPrintJobHandlers :
    IRequestHandler<ListPrintJobsQuery, PrintJobPage>,
    IRequestHandler<GetPrintJobDetailQuery, PrintJobDetail>,
    IRequestHandler<GetAdminKioskQueueQuery, IReadOnlyList<QueueEntry>>,
    IRequestHandler<AdminCancelPrintJobCommand, PrintJobResponse>
{
    private readonly IPermissionResolver _permissions;
    private readonly IKioskRepository _kiosks;
    private readonly IPrintJobRepository _jobs;
    private readonly IPrintAttemptRepository _attempts;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _notifier;
    private readonly ITransitionHistory _history;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly SuspectedJobIncidents _suspected;
    private readonly IAuditWriter _audit;

    public AdminPrintJobHandlers(
        IPermissionResolver permissions,
        IKioskRepository kiosks,
        IPrintJobRepository jobs,
        IPrintAttemptRepository attempts,
        PrintQueueService queue,
        IKioskNotifier notifier,
        ITransitionHistory history,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        SuspectedJobIncidents suspected,
        IAuditWriter audit)
    {
        _suspected = suspected;
        _audit = audit;
        _permissions = permissions;
        _kiosks = kiosks;
        _jobs = jobs;
        _attempts = attempts;
        _queue = queue;
        _notifier = notifier;
        _history = history;
        _events = events;
        _correlation = correlation;
        _clock = clock;
    }

    /// <inheritdoc cref="ListPrintJobsQuery" />
    public async Task<PrintJobPage> Handle(ListPrintJobsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);
        var visible = kiosks.Where(kiosk => effective.CanSee(kiosk.Id)).ToDictionary(kiosk => kiosk.Id);

        IReadOnlyCollection<Guid> scope = request.KioskId is { } one
            ? visible.ContainsKey(one) ? [one] : []
            : visible.Keys.ToList();

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);

        if (scope.Count == 0)
        {
            return new PrintJobPage([], 0, page, size);
        }

        var (items, total) = await _jobs
            .SearchAsync(
                new PrintJobFilter(scope, request.Statuses, request.From, request.To, request.JobCode?.Trim()),
                (page - 1) * size,
                size,
                cancellationToken)
            .ConfigureAwait(false);

        return new PrintJobPage(
            [
                .. items.Select(job => new PrintJobListItem(
                    job.Id,
                    job.JobCode,
                    job.KioskId,
                    visible.TryGetValue(job.KioskId, out var kiosk) ? kiosk.Code : null,
                    job.Status.ToString(),
                    job.Source.ToString(),
                    job.LocalFileName,
                    job.Price.Price.Pages,
                    job.Options.Copies,
                    job.Price.Price.FinalPrice,
                    job.Price.Price.Currency,
                    job.PaymentSkipped,
                    job.CreatedAt,
                    job.FinishedAt)),
            ],
            total,
            page,
            size);
    }

    /// <inheritdoc cref="GetPrintJobDetailQuery" />
    public async Task<PrintJobDetail> Handle(GetPrintJobDetailQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (job, kiosk) = await VisibleJobAsync(request.AdminUserId, request.JobId, cancellationToken).ConfigureAwait(false);
        var history = await _history.ListAsync(nameof(PrintJob), job.Id, cancellationToken).ConfigureAwait(false);
        var attempts = await _attempts.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);

        return new PrintJobDetail(
            await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false),
            kiosk.Code,
            [
                .. InChainOrder(history).Select(step => new PrintJobTransitionView(
                    step.FromStatus,
                    step.ToStatus,
                    step.ActorType.ToString(),
                    Messages.WriteStored(step.Reason),
                    step.CreatedAt)),
            ],
            [
                .. attempts.OrderBy(attempt => attempt.AttemptNo).Select(attempt => new PrintAttemptView(
                    attempt.AttemptNo,
                    attempt.PrinterName,
                    attempt.SpoolerJobId,
                    attempt.StartedAt,
                    attempt.FinishedAt,
                    attempt.PrinterCounterBefore,
                    attempt.PrinterCounterAfter,
                    attempt.CounterUnit,
                    attempt.ReportedSides,
                    attempt.ReportedSheets,
                    attempt.Status,
                    attempt.ErrorCode is { } code && Messages.Has(code) ? Messages.Write(new UserMessage(code)) : null,
                    attempt.Recovered)),
            ]);
    }

    /// <inheritdoc cref="GetAdminKioskQueueQuery" />
    public async Task<IReadOnlyList<QueueEntry>> Handle(GetAdminKioskQueueQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        if (!effective.CanSee(request.KioskId)
            || await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new NotFoundException("kiosk");
        }

        return await _queue.EntriesAsync(request.KioskId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AdminCancelPrintJobCommand" />
    public async Task<PrintJobResponse> Handle(AdminCancelPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A destructive action: the reason is what the audit log keeps of why
        // (plan section 8A.1); without one nothing is cancelled.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ValidationException([new ValidationError("reason", "printJobs.cancelReasonRequired")]);
        }

        var (job, _) = await VisibleJobAsync(request.AdminUserId, request.JobId, cancellationToken).ConfigureAwait(false);
        var actor = ActorContext.Admin(request.AdminUserId, correlationId: _correlation.CorrelationId);
        var wasSuspected = job.Status == PrintJobStatus.Suspected;
        var before = job.Status;

        await PrintJobCancellation.CancelAsync(
                job,
                new UserMessage("printJobs.reason.cancelledByAdmin"),
                actor,
                _clock.UtcNow,
                _jobs,
                _queue,
                _notifier,
                _events,
                _correlation,
                cancellationToken)
            .ConfigureAwait(false);

        if (wasSuspected)
        {
            // Cancelled with the default refund: the investigation is over.
            await _suspected
                .CloseAsync(job.Id, request.AdminUserId, UserMessage.Of("incidents.resolution.suspectedDecided", ("decision", "Cancel")), cancellationToken)
                .ConfigureAwait(false);
        }

        // Asked again for a job already cancelled, nothing happened - and the
        // audit log keeps only what did.
        if (before != PrintJobStatus.Cancelled)
        {
            await _audit
                .WriteAsync(
                    new AuditEntry(
                        request.AdminUserId,
                        "printJobs.cancel",
                        "PrintJob",
                        job.Id.ToString(),
                        before.ToString(),
                        job.Status.ToString(),
                        request.Ip,
                        request.Reason.Trim()),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The history in the order it happened. Steps taken in one request share
    /// a timestamp (placed, paid and queued at once in Development), so ties
    /// are broken by the chain itself: each step starts where the last ended.
    /// </summary>
    internal static IReadOnlyList<StateTransition> InChainOrder(IReadOnlyList<StateTransition> history)
    {
        var ordered = new List<StateTransition>(history.Count);

        foreach (var group in history.GroupBy(step => step.CreatedAt).OrderBy(group => group.Key))
        {
            var pending = group.ToList();

            while (pending.Count > 0)
            {
                var last = ordered.Count > 0 ? ordered[^1].ToStatus : null;
                var next = pending.FirstOrDefault(step => step.FromStatus == last)
                           ?? pending.FirstOrDefault(step => pending.All(other => other.ToStatus != step.FromStatus))
                           ?? pending[0];

                ordered.Add(next);
                pending.Remove(next);
            }
        }

        return ordered;
    }

    private async Task<(PrintJob Job, Kiosk Kiosk)> VisibleJobAsync(
        Guid adminUserId, Guid jobId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (job is null || !effective.CanSee(job.KioskId))
        {
            throw new NotFoundException("printJob");
        }

        var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("kiosk");

        return (job, kiosk);
    }
}
