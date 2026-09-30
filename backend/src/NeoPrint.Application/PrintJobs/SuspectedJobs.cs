using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Payments;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Support;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// <c>StuckJobWatchdogJob</c> (plan section 2.5, scenario B): a queued or
/// printing job whose kiosk has said nothing - no heartbeat, no status - for
/// <c>Job.SuspectedAfterMinutes</c> becomes Suspected. No cancellation, no
/// refund: the outcome is unknown and guessing loses money either way.
/// Suspected jobs nobody decided on for <c>Job.SuspectedAutoResolveHours</c>
/// remind the admins, once; nothing else happens on its own.
/// </summary>
public sealed record WatchStuckJobsCommand : IRequest<StuckJobsResult>;

public sealed record StuckJobsResult(int Suspected, int Reminded);

/// <summary>One job to Suspected, checked again inside the transaction.</summary>
public sealed record MarkJobSuspectedCommand(Guid JobId, DateTime Cutoff) : IRequest<bool>, ITransactionalRequest;

/// <summary>One reminder, once per suspicion.</summary>
public sealed record RemindSuspectedJobCommand(Guid JobId) : IRequest<bool>, ITransactionalRequest;

/// <summary>"Baxış gözləyən job-lar" (plan section 8A.3): the longest-waiting first.</summary>
public sealed record ListSuspectedJobsQuery(Guid AdminUserId) : IRequest<IReadOnlyList<SuspectedJobItem>>;

/// <summary>The investigation card of one suspected job (plan section 8A.3).</summary>
public sealed record GetSuspectedJobQuery(Guid AdminUserId, Guid JobId) : IRequest<SuspectedJobInvestigation>;

/// <summary>What an admin decided about a suspected job (plan section 2.5).</summary>
public enum SuspectedJobDecision
{
    /// <summary>"Tam qaytar": the job is cancelled and all of it goes back.</summary>
    FullRefund = 0,

    /// <summary>"Hissəvi qaytar": the job counts as printed in part, the amount given goes back.</summary>
    PartialRefund = 1,

    /// <summary>"Qaytarma yoxdur (çap olunub)": the job is completed, nothing goes back.</summary>
    NoRefund = 2,

    /// <summary>"Yenidən çapa göndər": back in the queue, no second payment.</summary>
    Reprint = 3,
}

/// <summary>
/// An admin's decision on a suspected job: the reason and the note are
/// mandatory and audited. Planned in one transaction, then any refunds are
/// carried out.
/// </summary>
/// <param name="Destination">For a refund's bank or card part: as the payment's refund options allow.</param>
public sealed record ResolveSuspectedJobCommand(
    Guid AdminUserId,
    Guid JobId,
    string? Decision,
    decimal? Amount,
    string? Destination,
    string? Reason,
    string? Note,
    string? Ip)
    : IRequest<SuspectedJobResolution>;

/// <summary>Step one of <see cref="ResolveSuspectedJobCommand"/>.</summary>
public sealed record DecideSuspectedJobCommand(ResolveSuspectedJobCommand Request) : IRequest<SuspectedJobResolution>, ITransactionalRequest;

public sealed record SuspectedJobResolution(Guid JobId, string Decision, string Status, IReadOnlyList<Guid> RefundIds);

/// <param name="Overdue">Waiting longer than <c>Job.SuspectedAutoResolveHours</c>: marked apart in the list.</param>
public sealed record SuspectedJobItem(
    Guid JobId,
    string JobCode,
    Guid KioskId,
    string? KioskCode,
    DateTime? SuspectedAt,
    DateTime? KioskLastHeartbeatAt,
    decimal Amount,
    string Currency,
    string? PaymentMethod,
    bool HasAccount,
    int OpenClaims,
    bool Overdue);

public sealed record SuspectedPaymentView(
    Guid Id, string Method, string Status, decimal Amount, string? ProviderRef, decimal Refundable, IReadOnlyList<string> ExternalDestinations);

public sealed record KioskIncidentView(Guid Id, string Type, string Severity, string Status, string Details, DateTime CreatedAt);

public sealed record NearbyJobView(Guid Id, string JobCode, string Status, DateTime CreatedAt, DateTime? FinishedAt);

public sealed record SuspectedJobInvestigation(
    PrintJobDetail Detail,
    DateTime? SuspectedAt,
    bool Overdue,
    string? KioskStatus,
    DateTime? KioskLastHeartbeatAt,
    SuspectedPaymentView? Payment,
    IReadOnlyList<SupportClaimView> Claims,
    IReadOnlyList<KioskIncidentView> KioskIncidents,
    IReadOnlyList<NearbyJobView> KioskJobs);

/// <summary>
/// What the kiosk handler and the admin decision share: the job's
/// investigation incidents close when the outcome becomes known.
/// </summary>
public sealed class SuspectedJobIncidents
{
    private readonly IIncidentRepository _incidents;
    private readonly IncidentReporter _reporter;
    private readonly IClock _clock;

    public SuspectedJobIncidents(IIncidentRepository incidents, IncidentReporter reporter, IClock clock)
    {
        _incidents = incidents;
        _reporter = reporter;
        _clock = clock;
    }

    public static string SuspectedKey(Guid jobId) => $"suspected:{jobId}";

    public static string ReminderKey(Guid jobId) => $"suspected-reminder:{jobId}";

    public static string LateReportKey(Guid jobId) => $"suspected-late:{jobId}";

    /// <param name="adminId">Null when the kiosk's own report settled it.</param>
    public async Task CloseAsync(Guid jobId, Guid? adminId, UserMessage resolution, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { SuspectedKey(jobId), ReminderKey(jobId) })
        {
            if (await _incidents.FindOpenByDedupKeyAsync(key, cancellationToken).ConfigureAwait(false) is { } incident)
            {
                incident.Resolve(adminId, resolution.Encode(), _clock.UtcNow);
                await _incidents.UpdateAsync(incident, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The kiosk came back and reported a job an admin had already decided
    /// on. Nothing is changed - the decision stands and money has moved - but
    /// the admins are told, with the kiosk's evidence kept in <c>print_attempts</c>.
    /// </summary>
    public Task RaiseLateReportAsync(PrintJob job, string reported, CancellationToken cancellationToken)
        => _reporter.RaiseAsync(
            IncidentType.JobSuspected,
            IncidentSeverity.Medium,
            UserMessage.Of("incidents.jobSuspectedLateReport", ("jobCode", job.JobCode), ("reported", reported), ("status", job.Status.ToString())),
            LateReportKey(job.Id),
            cancellationToken,
            job.KioskId,
            nameof(PrintJob),
            job.Id);
}

public sealed class SuspectedJobHandlers :
    IRequestHandler<WatchStuckJobsCommand, StuckJobsResult>,
    IRequestHandler<MarkJobSuspectedCommand, bool>,
    IRequestHandler<RemindSuspectedJobCommand, bool>,
    IRequestHandler<ListSuspectedJobsQuery, IReadOnlyList<SuspectedJobItem>>,
    IRequestHandler<GetSuspectedJobQuery, SuspectedJobInvestigation>,
    IRequestHandler<ResolveSuspectedJobCommand, SuspectedJobResolution>,
    IRequestHandler<DecideSuspectedJobCommand, SuspectedJobResolution>
{
    /// <summary>Jobs looked at per watchdog run; the rest wait for the next minute.</summary>
    private const int Batch = 200;

    /// <summary>How far around the suspicion the card looks for other events at the kiosk.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(2);

    private readonly ISender _sender;
    private readonly IPermissionResolver _permissions;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly IPaymentRepository _payments;
    private readonly IIncidentRepository _incidents;
    private readonly ISupportClaimRepository _claims;
    private readonly RefundPlanner _planner;
    private readonly IncidentReporter _reporter;
    private readonly SuspectedJobIncidents _jobIncidents;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _notifier;
    private readonly ISettingsReader _settings;
    private readonly IAuditWriter _audit;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<SuspectedJobHandlers> _logger;

    public SuspectedJobHandlers(
        ISender sender,
        IPermissionResolver permissions,
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        IPaymentRepository payments,
        IIncidentRepository incidents,
        ISupportClaimRepository claims,
        RefundPlanner planner,
        IncidentReporter reporter,
        SuspectedJobIncidents jobIncidents,
        PrintQueueService queue,
        IKioskNotifier notifier,
        ISettingsReader settings,
        IAuditWriter audit,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<SuspectedJobHandlers> logger)
    {
        _sender = sender;
        _permissions = permissions;
        _jobs = jobs;
        _kiosks = kiosks;
        _payments = payments;
        _incidents = incidents;
        _claims = claims;
        _planner = planner;
        _reporter = reporter;
        _jobIncidents = jobIncidents;
        _queue = queue;
        _notifier = notifier;
        _settings = settings;
        _audit = audit;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="WatchStuckJobsCommand" />
    public async Task<StuckJobsResult> Handle(WatchStuckJobsCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var cutoff = now.AddMinutes(-await SuspectedAfterMinutesAsync(cancellationToken).ConfigureAwait(false));
        var suspected = 0;

        var candidates = await _jobs.ListStuckCandidatesAsync(cutoff, Batch, cancellationToken).ConfigureAwait(false);

        foreach (var group in candidates.GroupBy(job => job.KioskId))
        {
            var kiosk = await _kiosks.FindByIdAsync(group.Key, cancellationToken).ConfigureAwait(false);

            if (kiosk?.LastHeartbeatAt is { } heartbeat && heartbeat > cutoff)
            {
                // The kiosk is alive: a long queue, not a lost one.
                continue;
            }

            foreach (var job in group)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (await _sender.Send(new MarkJobSuspectedCommand(job.Id, cutoff), cancellationToken).ConfigureAwait(false))
                    {
                        suspected++;
                    }
                }
                catch (ConcurrencyException)
                {
                    // The kiosk reported in the meantime; the next run looks again.
                }
            }
        }

        var remindAfter = await _settings.GetIntAsync(SettingKeys.JobSuspectedAutoResolveHours, 72, cancellationToken).ConfigureAwait(false);
        var reminded = 0;

        foreach (var job in await _jobs.ListSuspectedForReminderAsync(now.AddHours(-remindAfter), Batch, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await _sender.Send(new RemindSuspectedJobCommand(job.Id), cancellationToken).ConfigureAwait(false))
                {
                    reminded++;
                }
            }
            catch (ConcurrencyException)
            {
                // Decided on in the meantime.
            }
        }

        if (suspected + reminded > 0)
        {
            _logger.LogWarning("Stuck job watchdog: {Suspected} job(s) now Suspected, {Reminded} reminder(s) sent.", suspected, reminded);
        }

        return new StuckJobsResult(suspected, reminded);
    }

    /// <inheritdoc cref="MarkJobSuspectedCommand" />
    public async Task<bool> Handle(MarkJobSuspectedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        // Read again: the kiosk may have reported between the list and now.
        if (job is null || !job.IsInQueue || job.LastKioskActivityAt is not { } activity || activity > request.Cutoff)
        {
            return false;
        }

        var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk?.LastHeartbeatAt is { } heartbeat && heartbeat > request.Cutoff)
        {
            return false;
        }

        var before = job.Status;
        var now = _clock.UtcNow;
        job.MarkSuspected(ActorContext.System("printJobs.reason.kioskSilent", _correlation.CorrelationId), now);

        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        await _queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(
                new PrintJobStatusChanged(job.Id, job.KioskId, before.ToString(), job.Status.ToString(), _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        await _reporter.RaiseAsync(
                IncidentType.JobSuspected,
                IncidentSeverity.High,
                UserMessage.Of("incidents.jobSuspected", ("jobCode", job.JobCode), ("kioskCode", kiosk?.Code ?? "-"), ("minutes", (int)(now - activity).TotalMinutes)),
                SuspectedJobIncidents.SuspectedKey(job.Id),
                cancellationToken,
                job.KioskId,
                nameof(PrintJob),
                job.Id)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Job {JobId} at kiosk {KioskId} is Suspected: nothing from the kiosk since {Activity:O} (was {Before}). No refund; an admin decides.",
            job.Id,
            job.KioskId,
            activity,
            before);

        return true;
    }

    /// <inheritdoc cref="RemindSuspectedJobCommand" />
    public async Task<bool> Handle(RemindSuspectedJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is not { Status: PrintJobStatus.Suspected, SuspectedReminderAt: null })
        {
            return false;
        }

        var now = _clock.UtcNow;
        job.NoteSuspectedReminder(now);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        var hours = job.SuspectedAt is { } since ? (int)(now - since).TotalHours : 0;

        await _reporter.RaiseAsync(
                IncidentType.JobSuspected,
                IncidentSeverity.High,
                UserMessage.Of("incidents.jobSuspectedReminder", ("jobCode", job.JobCode), ("hours", hours)),
                SuspectedJobIncidents.ReminderKey(job.Id),
                cancellationToken,
                job.KioskId,
                nameof(PrintJob),
                job.Id)
            .ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc cref="ListSuspectedJobsQuery" />
    public async Task<IReadOnlyList<SuspectedJobItem>> Handle(ListSuspectedJobsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(kiosk => effective.CanSee(kiosk.Id))
            .ToDictionary(kiosk => kiosk.Id);

        if (kiosks.Count == 0)
        {
            return [];
        }

        var jobs = await _jobs.ListSuspectedAsync(kiosks.Keys.ToList(), Batch, cancellationToken).ConfigureAwait(false);
        var overdueBefore = await OverdueBeforeAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<SuspectedJobItem>(jobs.Count);

        foreach (var job in jobs)
        {
            var payment = job.PaymentId is { } paymentId
                ? await _payments.FindByIdAsync(paymentId, cancellationToken).ConfigureAwait(false)
                : null;
            var claims = await _claims.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
            var kiosk = kiosks.GetValueOrDefault(job.KioskId);

            items.Add(new SuspectedJobItem(
                job.Id,
                job.JobCode,
                job.KioskId,
                kiosk?.Code,
                job.SuspectedAt,
                kiosk?.LastHeartbeatAt,
                job.Price.Price.FinalPrice,
                job.Price.Price.Currency,
                payment?.Method.ToString(),
                job.UserId is not null,
                claims.Count(claim => claim.Status == Domain.Support.ClaimStatus.Open),
                job.SuspectedAt is { } since && since <= overdueBefore));
        }

        return items;
    }

    /// <inheritdoc cref="GetSuspectedJobQuery" />
    public async Task<SuspectedJobInvestigation> Handle(GetSuspectedJobQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The detail query checks the admin may see this kiosk.
        var detail = await _sender.Send(new GetPrintJobDetailQuery(request.AdminUserId, request.JobId), cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("printJob");
        var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        SuspectedPaymentView? paymentView = null;

        if (job.PaymentId is { } paymentId && await _payments.FindByIdAsync(paymentId, cancellationToken).ConfigureAwait(false) is { } payment)
        {
            var options = payment.WasPaid
                ? await _sender.Send(new GetRefundOptionsQuery(payment.Id), cancellationToken).ConfigureAwait(false)
                : null;

            paymentView = new SuspectedPaymentView(
                payment.Id,
                payment.Method.ToString(),
                payment.Status.ToString(),
                payment.Amount.Amount,
                payment.ProviderRef,
                options?.Refundable ?? 0,
                options?.ExternalDestinations ?? []);
        }

        var around = job.SuspectedAt ?? job.LastKioskActivityAt ?? job.CreatedAt;
        var from = (job.LastKioskActivityAt ?? around) - Window;
        var to = around + Window;

        var incidents = await _incidents.ListForKioskAsync(job.KioskId, from, to, 50, cancellationToken).ConfigureAwait(false);
        var (nearby, _) = await _jobs.SearchAsync(new PrintJobFilter([job.KioskId], null, from, to, null), 0, 50, cancellationToken).ConfigureAwait(false);
        var claims = await _claims.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var overdueBefore = await OverdueBeforeAsync(cancellationToken).ConfigureAwait(false);

        return new SuspectedJobInvestigation(
            detail,
            job.SuspectedAt,
            job.Status == PrintJobStatus.Suspected && job.SuspectedAt is { } since && since <= overdueBefore,
            kiosk?.Status.ToString(),
            kiosk?.LastHeartbeatAt,
            paymentView,
            [.. claims.Select(SupportClaimView.From)],
            [
                .. incidents.Select(incident => new KioskIncidentView(
                    incident.Id,
                    incident.Type.ToString(),
                    incident.Severity.ToString(),
                    incident.Status.ToString(),
                    Messages.WriteStored(incident.Details) ?? string.Empty,
                    incident.CreatedAt)),
            ],
            [
                .. nearby.Where(other => other.Id != job.Id)
                    .Select(other => new NearbyJobView(other.Id, other.JobCode, other.Status.ToString(), other.CreatedAt, other.FinishedAt)),
            ]);
    }

    /// <inheritdoc cref="ResolveSuspectedJobCommand" />
    public async Task<SuspectedJobResolution> Handle(ResolveSuspectedJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _sender.Send(new DecideSuspectedJobCommand(request), cancellationToken).ConfigureAwait(false);

        foreach (var refundId in result.RefundIds)
        {
            // Carried out now; a provider that does not answer is retried in the background.
            await _sender.Send(new ProcessRefundCommand(refundId), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc cref="DecideSuspectedJobCommand" />
    public async Task<SuspectedJobResolution> Handle(DecideSuspectedJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = command.Request;
        var errors = new List<ValidationError>();

        if (!Enum.TryParse<SuspectedJobDecision>(request.Decision, ignoreCase: false, out var decision) || !Enum.IsDefined(decision))
        {
            errors.Add(new ValidationError("decision", "printJobs.suspected.decisionInvalid"));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors.Add(new ValidationError("reason", "printJobs.suspected.reasonRequired"));
        }

        if (string.IsNullOrWhiteSpace(request.Note))
        {
            errors.Add(new ValidationError("note", "printJobs.suspected.noteRequired"));
        }

        if (decision == SuspectedJobDecision.PartialRefund
            && (request.Amount is not { } asked || asked <= 0 || decimal.Round(asked, 2) != asked))
        {
            errors.Add(new ValidationError("amount", "refunds.amountInvalid"));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is null || !effective.CanSee(job.KioskId))
        {
            throw new NotFoundException("printJob");
        }

        if (job.Status != PrintJobStatus.Suspected)
        {
            // Decided already - by another admin, or by the kiosk that came back.
            throw new DomainException(UserMessage.Of("printJobs.suspected.alreadyResolved", ("status", job.Status.ToString())));
        }

        var reason = request.Reason!.Trim();
        var note = request.Note!.Trim();
        var now = _clock.UtcNow;
        var before = job.Status;
        var decisionKey = $"printJobs.suspected.decision.{decision}";
        var actor = ActorContext.Admin(request.AdminUserId, decisionKey, _correlation.CorrelationId);
        IReadOnlyList<Guid> refundIds = [];
        Payment? payment = null;

        if (decision is SuspectedJobDecision.FullRefund or SuspectedJobDecision.PartialRefund)
        {
            payment = job.PaymentId is { } paymentId ? await _payments.FindByIdAsync(paymentId, cancellationToken).ConfigureAwait(false) : null;

            if (payment is not { WasPaid: true })
            {
                throw new DomainException("printJobs.suspected.nothingPaid");
            }

            var options = await _sender.Send(new GetRefundOptionsQuery(payment.Id), cancellationToken).ConfigureAwait(false);

            if (request.Amount is { } amount && amount > options.Refundable)
            {
                throw new DomainException(UserMessage.Of("payments.refundExceedsPayment", ("refundable", options.Refundable), ("asked", amount)));
            }

            if (request.Destination is { } chosen && !options.ExternalDestinations.Contains(chosen))
            {
                throw new DomainException("refunds.destinationNotAllowed");
            }
        }

        switch (decision)
        {
            case SuspectedJobDecision.FullRefund:
                // Cancelled with the refund's own reason: the cancellation
                // consumer closes the payment's intents and leaves the money
                // to the refunds made here.
                await PrintJobCancellation
                    .CancelAsync(job, new UserMessage(RefundHandlers.CancelledForRefund), actor, now, _jobs, _queue, _notifier, _events, _correlation, cancellationToken)
                    .ConfigureAwait(false);
                refundIds = await PlanRefundAsync(job, payment!, null, request, reason, note, cancellationToken).ConfigureAwait(false);
                break;

            case SuspectedJobDecision.PartialRefund:
                // Completed with what the kiosk last reported; the paper
                // counter follows printer reports only, not decisions.
                job.Complete(job.PrintedSides, job.PrintedSheets, actor, now);
                await SaveAsync(job, before, cancellationToken).ConfigureAwait(false);
                refundIds = await PlanRefundAsync(job, payment!, request.Amount, request, reason, note, cancellationToken).ConfigureAwait(false);
                break;

            case SuspectedJobDecision.NoRefund:
                job.Complete(job.Price.Price.Sides, job.RequiredSheets, actor, now);
                await SaveAsync(job, before, cancellationToken).ConfigureAwait(false);
                break;

            case SuspectedJobDecision.Reprint:
                // The same job, paid once, printed again (plan section 2.5).
                job.Enqueue(actor, now);
                await SaveAsync(job, before, cancellationToken).ConfigureAwait(false);
                await _queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
                break;
        }

        await _jobIncidents
            .CloseAsync(job.Id, request.AdminUserId, UserMessage.Of("incidents.resolution.suspectedDecided", ("decision", decision.ToString())), cancellationToken)
            .ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "printJobs.suspected.resolve",
                    nameof(PrintJob),
                    job.Id.ToString(),
                    before.ToString(),
                    JsonSerializer.Serialize(new
                    {
                        decision = decision.ToString(),
                        status = job.Status.ToString(),
                        amount = request.Amount,
                        request.Destination,
                        note,
                        refunds = refundIds,
                    }),
                    request.Ip,
                    reason),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {AdminId} decided suspected job {JobId}: {Decision} ({Refunds} refund(s)).", request.AdminUserId, job.Id, decision, refundIds.Count);

        return new SuspectedJobResolution(job.Id, decision.ToString(), job.Status.ToString(), refundIds);
    }

    private async Task SaveAsync(PrintJob job, PrintJobStatus before, CancellationToken cancellationToken)
    {
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
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
    }

    private Task<IReadOnlyList<Guid>> PlanRefundAsync(
        PrintJob job, Payment payment, decimal? amount, ResolveSuspectedJobCommand request, string reason, string note, CancellationToken cancellationToken)
        => _planner.PlanAsync(
            new RefundRequested(
                payment.Id,
                job.Id,
                nameof(RefundReason.AdminManual),
                amount,
                // One decision per suspicion: the job leaves Suspected with it.
                $"suspected:{job.Id}",
                _correlation.CorrelationId,
                request.Destination,
                $"{reason} — {note}",
                request.AdminUserId,
                nameof(ActorType.Admin)),
            cancellationToken);

    private async Task<int> SuspectedAfterMinutesAsync(CancellationToken cancellationToken)
        => Math.Max(1, await _settings.GetIntAsync(SettingKeys.JobSuspectedAfterMinutes, 15, cancellationToken).ConfigureAwait(false));

    private async Task<DateTime> OverdueBeforeAsync(CancellationToken cancellationToken)
        => _clock.UtcNow.AddHours(-await _settings.GetIntAsync(SettingKeys.JobSuspectedAutoResolveHours, 72, cancellationToken).ConfigureAwait(false));
}

