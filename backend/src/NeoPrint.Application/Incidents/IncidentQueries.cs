using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.Incidents;

/// <summary>
/// Incidents for the panel, filtered by status, type and kiosk (plan section
/// 8A.3). An account with a kiosk scope gets the incidents of its own kiosks
/// only (plan section 8A.2).
/// </summary>
public sealed record ListIncidentsQuery(
    Guid AdminUserId,
    IReadOnlyCollection<string>? Statuses,
    IReadOnlyCollection<string>? Types,
    Guid? KioskId,
    int Page,
    int PageSize)
    : IRequest<IncidentPage>;

/// <summary>
/// One incident with the orders it touched (plan section 9.6, A.6: "incident
/// detalında təsirlənən ödənişlər və refund statusları").
/// </summary>
public sealed record GetIncidentQuery(Guid AdminUserId, Guid IncidentId) : IRequest<IncidentDetail>;

/// <summary><c>POST admin/incidents/{id}/acknowledge</c>: somebody has seen it and is on it.</summary>
public sealed record AcknowledgeIncidentCommand(Guid AdminUserId, Guid IncidentId, string? Ip)
    : IRequest<IncidentView>, ITransactionalRequest;

/// <summary>
/// <c>POST admin/incidents/{id}/resolve</c>: closed by a person, with a note
/// (plan section 8A.1: mandatory, and in the audit log).
/// </summary>
public sealed record ResolveIncidentCommand(Guid AdminUserId, Guid IncidentId, string? Note, string? Ip)
    : IRequest<IncidentView>, ITransactionalRequest;

/// <param name="Details">What happened and what to do, in the reader's language.</param>
/// <param name="EntityType">What it is about (<c>FiscalReceipt</c>, <c>Refund</c>, <c>PrintJob</c>, <c>Kiosk</c>), with <see cref="EntityId"/>.</param>
public sealed record IncidentView(
    Guid Id,
    string Type,
    string Severity,
    string Status,
    string Details,
    string? EntityType,
    Guid? EntityId,
    Guid? KioskId,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    string? Resolution,
    string? KioskCode = null,
    DateTime? AcknowledgedAt = null,
    Guid? AcknowledgedBy = null,
    Guid? ResolvedBy = null)
{
    public static IncidentView From(Incident incident, string? kioskCode = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        return new IncidentView(
            incident.Id,
            incident.Type.ToString(),
            incident.Severity.ToString(),
            incident.Status.ToString(),
            Messages.WriteStored(incident.Details) ?? string.Empty,
            incident.EntityType,
            incident.EntityId,
            incident.KioskId,
            incident.CreatedAt,
            incident.ResolvedAt,
            Messages.WriteStored(incident.Resolution),
            kioskCode,
            incident.AcknowledgedAt,
            incident.AcknowledgedBy,
            incident.ResolvedBy);
    }
}

public sealed record IncidentPage(IReadOnlyList<IncidentView> Items, long Total, int Page, int PageSize);

/// <param name="Registered">Whether the order has an owner: theirs waits for their choice, a guest's is paid back by itself.</param>
/// <param name="Paid">What was paid for it; null when nothing was.</param>
public sealed record AffectedOrder(
    Guid JobId,
    string JobCode,
    string Status,
    bool Registered,
    string? Reason,
    Guid? PaymentId,
    decimal? Paid,
    string? Currency,
    string? PaymentStatus,
    IReadOnlyList<AffectedRefund> Refunds);

public sealed record AffectedRefund(Guid Id, string Status, decimal Amount, string Destination, string Reason, DateTime CreatedAt);

public sealed record IncidentDetail(IncidentView Incident, IReadOnlyList<AffectedOrder> Affected);

public sealed class IncidentQueryHandlers :
    IRequestHandler<ListIncidentsQuery, IncidentPage>,
    IRequestHandler<GetIncidentQuery, IncidentDetail>,
    IRequestHandler<AcknowledgeIncidentCommand, IncidentView>,
    IRequestHandler<ResolveIncidentCommand, IncidentView>
{
    /// <summary>An order placed this long before the incident may still have been waiting when it began.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromHours(24);

    private const int MostAffected = 100;

    private const int LongestNote = 500;

    private readonly IIncidentRepository _incidents;
    private readonly IKioskRepository _kiosks;
    private readonly IPrintJobRepository _jobs;
    private readonly IPaymentRepository _payments;
    private readonly IRefundRepository _refunds;
    private readonly IPermissionResolver _permissions;
    private readonly IPaymentsAdminNotifier _panel;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public IncidentQueryHandlers(
        IIncidentRepository incidents,
        IKioskRepository kiosks,
        IPrintJobRepository jobs,
        IPaymentRepository payments,
        IRefundRepository refunds,
        IPermissionResolver permissions,
        IPaymentsAdminNotifier panel,
        IAuditWriter audit,
        IClock clock)
    {
        _incidents = incidents;
        _kiosks = kiosks;
        _jobs = jobs;
        _payments = payments;
        _refunds = refunds;
        _permissions = permissions;
        _panel = panel;
        _audit = audit;
        _clock = clock;
    }

    /// <inheritdoc cref="ListIncidentsQuery" />
    public async Task<IncidentPage> Handle(ListIncidentsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statuses = Parse<IncidentStatus>(request.Statuses, "status");
        var types = Parse<IncidentType>(request.Types, "type");
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        // Null is "every incident". A scoped account never gets that: what is
        // about no kiosk (a refund, a fiscal receipt) is not its business.
        IReadOnlyCollection<Guid>? scope = request.KioskId is { } one
            ? effective.CanSee(one) ? [one] : []
            : effective.AllKiosks ? null : effective.KioskIds;

        if (scope is { Count: 0 })
        {
            return new IncidentPage([], 0, page, size);
        }

        var (items, total) = await _incidents
            .ListAsync(statuses, types, (page - 1) * size, size, cancellationToken, scope)
            .ConfigureAwait(false);

        var codes = new Dictionary<Guid, string?>();

        foreach (var kioskId in items.Select(item => item.KioskId).OfType<Guid>().Distinct())
        {
            codes[kioskId] = (await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false))?.Code;
        }

        return new IncidentPage(
            [.. items.Select(item => IncidentView.From(item, item.KioskId is { } kiosk ? codes[kiosk] : null))], total, page, size);
    }

    /// <inheritdoc cref="GetIncidentQuery" />
    public async Task<IncidentDetail> Handle(GetIncidentQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (incident, kiosk) = await VisibleAsync(request.AdminUserId, request.IncidentId, cancellationToken).ConfigureAwait(false);
        var jobs = await AffectedJobsAsync(incident, cancellationToken).ConfigureAwait(false);
        var affected = new List<AffectedOrder>();

        foreach (var job in jobs)
        {
            var payment = job.PaymentId is { } paymentId
                ? await _payments.FindByIdAsync(paymentId, cancellationToken).ConfigureAwait(false)
                : null;
            var refunds = await _refunds.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);

            affected.Add(new AffectedOrder(
                job.Id,
                job.JobCode,
                job.Status.ToString(),
                job.UserId is not null,
                Messages.WriteStored(job.ErrorReason ?? job.CancelReason),
                payment?.Id,
                payment is { WasPaid: true } ? payment.Amount.Amount : null,
                payment?.Amount.Currency,
                payment?.Status.ToString(),
                [
                    .. refunds.OrderBy(refund => refund.CreatedAt).Select(refund => new AffectedRefund(
                        refund.Id,
                        refund.Status.ToString(),
                        refund.Amount.Amount,
                        refund.Destination.ToString(),
                        refund.Reason.ToString(),
                        refund.CreatedAt)),
                ]));
        }

        return new IncidentDetail(IncidentView.From(incident, kiosk?.Code), affected);
    }

    /// <inheritdoc cref="AcknowledgeIncidentCommand" />
    public async Task<IncidentView> Handle(AcknowledgeIncidentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (incident, kiosk) = await VisibleAsync(request.AdminUserId, request.IncidentId, cancellationToken).ConfigureAwait(false);

        if (incident.Status != IncidentStatus.Open)
        {
            // Pressed twice, or somebody else got there first: it is taken.
            return IncidentView.From(incident, kiosk?.Code);
        }

        incident = await ChangeAsync(incident, current => current.Acknowledge(request.AdminUserId, _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "incidents.acknowledge",
                    nameof(Incident),
                    incident.Id.ToString(),
                    nameof(IncidentStatus.Open),
                    incident.Status.ToString(),
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);
        await _panel
            .IncidentResolvedAsync(incident.Id, incident.Type.ToString(), incident.Status.ToString(), cancellationToken, incident.KioskId)
            .ConfigureAwait(false);

        return IncidentView.From(incident, kiosk?.Code);
    }

    /// <inheritdoc cref="ResolveIncidentCommand" />
    public async Task<IncidentView> Handle(ResolveIncidentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var note = request.Note?.Trim();

        if (string.IsNullOrEmpty(note))
        {
            throw new ValidationException([new ValidationError("note", "incidents.noteRequired")]);
        }

        if (note.Length > LongestNote)
        {
            throw new ValidationException([new ValidationError("note", UserMessage.Of("incidents.noteTooLong", ("max", LongestNote)))]);
        }

        var (incident, kiosk) = await VisibleAsync(request.AdminUserId, request.IncidentId, cancellationToken).ConfigureAwait(false);

        if (incident.Status == IncidentStatus.Resolved)
        {
            return IncidentView.From(incident, kiosk?.Code);
        }

        var before = incident.Status;

        incident = await ChangeAsync(
                incident,
                current => current.Resolve(
                    request.AdminUserId, UserMessage.Of("incidents.resolution.byAdmin", ("note", note)).Encode(), _clock.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "incidents.resolve",
                    nameof(Incident),
                    incident.Id.ToString(),
                    before.ToString(),
                    incident.Status.ToString(),
                    request.Ip,
                    note),
                cancellationToken)
            .ConfigureAwait(false);
        await _panel
            .IncidentResolvedAsync(incident.Id, incident.Type.ToString(), incident.Status.ToString(), cancellationToken, incident.KioskId)
            .ConfigureAwait(false);

        return IncidentView.From(incident, kiosk?.Code);
    }

    /// <summary>
    /// Applies a person's decision and saves it. While an incident is new the
    /// system is still writing to it (whom it was sent to): losing that race
    /// is no reason to tell the person "somebody changed it, try again" - the
    /// incident is read again and the decision applied to that.
    /// </summary>
    private async Task<Incident> ChangeAsync(Incident incident, Action<Incident> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            change(incident);

            try
            {
                await _incidents.UpdateAsync(incident, cancellationToken).ConfigureAwait(false);
                return incident;
            }
            catch (ConcurrencyException) when (attempt < 5)
            {
                incident = await _incidents.FindByIdAsync(incident.Id, cancellationToken).ConfigureAwait(false)
                           ?? throw new NotFoundException("incident");
            }
        }
    }

    /// <summary>The incident, when this account may see it; "not found" otherwise - also for one outside its kiosks.</summary>
    private async Task<(Incident Incident, Kiosk? Kiosk)> VisibleAsync(Guid adminUserId, Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await _incidents.FindByIdAsync(incidentId, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("incident");
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);

        if (incident.KioskId is { } kioskId ? !effective.CanSee(kioskId) : !effective.AllKiosks)
        {
            throw new NotFoundException("incident");
        }

        return (
            incident,
            incident.KioskId is { } id ? await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) : null);
    }

    /// <summary>
    /// The orders a kiosk's incident touched: the ones stopped at the printer
    /// or waiting for a decision now, the ones still in the queue while it is
    /// open, and the ones cancelled while it lasted. An incident about one
    /// order has that order.
    /// </summary>
    private async Task<IReadOnlyList<PrintJob>> AffectedJobsAsync(Incident incident, CancellationToken cancellationToken)
    {
        if (incident is { EntityType: nameof(PrintJob), EntityId: { } jobId })
        {
            return await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) is { } job ? [job] : [];
        }

        if (incident.KioskId is not { } kioskId)
        {
            return [];
        }

        var until = incident.ResolvedAt ?? _clock.UtcNow;
        var (jobs, _) = await _jobs
            .SearchAsync(
                new PrintJobFilter(
                    [kioskId],
                    [PrintJobStatus.Queued, PrintJobStatus.Interrupted, PrintJobStatus.Suspected, PrintJobStatus.Cancelled],
                    incident.CreatedAt - LookBack,
                    until,
                    null),
                0,
                MostAffected,
                cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. jobs.Where(job => job.Status switch
            {
                PrintJobStatus.Interrupted or PrintJobStatus.Suspected => true,
                PrintJobStatus.Queued => incident.Open,
                PrintJobStatus.Cancelled => job.PaymentId is not null && job.FinishedAt >= incident.CreatedAt && job.FinishedAt <= until,
                _ => false,
            }),
        ];
    }

    private static List<T>? Parse<T>(IReadOnlyCollection<string>? values, string field)
        where T : struct, Enum
        => values?
            .Select(value => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ValidationException([new ValidationError(field, "incidents.filterInvalid")]))
            .ToList();
}
