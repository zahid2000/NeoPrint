using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Application.Refunds;

/// <summary>The <c>refunds</c> collection (plan section 2.5).</summary>
public interface IRefundRepository
{
    Task<Refund?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Refund?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateRefundException">A refund with this dedup key already exists.</exception>
    Task AddAsync(Refund refund, CancellationToken cancellationToken = default);

    Task UpdateAsync(Refund refund, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Refund>> ListForPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Refund>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Payments with a refund still on its way (pending or with the provider), newest first.</summary>
    Task<IReadOnlyList<Guid>> ListOpenPaymentIdsAsync(int take, CancellationToken cancellationToken = default);

    Task<long> CountAsync(RefundStatus status, CancellationToken cancellationToken = default);

    /// <summary>Refunds that went through in a period, for the revenue figures.</summary>
    Task<IReadOnlyList<Refund>> ListCompletedAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds with work due: created but not started (a consumer that died),
    /// or waiting for their next provider try.
    /// </summary>
    Task<IReadOnlyList<Refund>> ListDueAsync(DateTime now, DateTime createdBefore, int take, CancellationToken cancellationToken = default);

    /// <summary>Provider refunds accepted but not finished, for <c>RefundStatusPollingJob</c>.</summary>
    Task<IReadOnlyList<Refund>> ListAwaitingProviderAsync(DateTime startedBefore, int take, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Refund> Items, long Total)> SearchAsync(RefundFilter filter, int skip, int take, CancellationToken cancellationToken = default);
}

public sealed record RefundFilter(IReadOnlyCollection<RefundStatus>? Statuses, IReadOnlyCollection<RefundReason>? Reasons, Guid? PaymentId);

/// <summary>The unique dedup key refused a second refund for the same decision.</summary>
public sealed class DuplicateRefundException : Exception
{
    public DuplicateRefundException(string dedupKey)
        : base($"A refund for {dedupKey} already exists.")
        => DedupKey = dedupKey;

    public string DedupKey { get; }
}

/// <summary>The <c>refund_vouchers</c> collection (plan section 5).</summary>
public interface IRefundVoucherRepository
{
    Task<RefundVoucher?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<RefundVoucher?> FindByRefundAsync(Guid refundId, CancellationToken cancellationToken = default);

    Task AddAsync(RefundVoucher voucher, CancellationToken cancellationToken = default);

    Task UpdateAsync(RefundVoucher voucher, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<RefundVoucher> Items, long Total)> ListAsync(int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>The <c>incidents</c> collection (plan section 5).</summary>
public interface IIncidentRepository
{
    /// <returns>False when an open incident with this dedup key is already there.</returns>
    Task<bool> TryAddAsync(Incident incident, CancellationToken cancellationToken = default);

    Task<Incident?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Incident incident, CancellationToken cancellationToken = default);

    /// <param name="kioskIds">
    /// Null for every incident. Otherwise only the incidents of these kiosks:
    /// an account with a kiosk scope sees what happened at its own kiosks and
    /// nothing else (plan section 8A.2).
    /// </param>
    Task<(IReadOnlyList<Incident> Items, long Total)> ListAsync(
        IReadOnlyCollection<IncidentStatus>? statuses,
        IReadOnlyCollection<IncidentType>? types,
        int skip,
        int take,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? kioskIds = null);

    Task<long> CountOpenAsync(IncidentType type, CancellationToken cancellationToken = default);

    /// <summary>How many incidents of a kiosk are not resolved yet.</summary>
    Task<long> CountOpenForKioskAsync(Guid kioskId, CancellationToken cancellationToken = default);

    Task<Incident?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes down that the incident was sent to a recipient - in a row of its
    /// own, so nothing written to the incident in the same moment (it is being
    /// acknowledged, or closing by itself) is in its way.
    /// </summary>
    /// <returns>False when the mark was there already.</returns>
    Task<bool> NoteNotifiedAsync(IncidentNotification mark, CancellationToken cancellationToken = default);

    /// <summary>The ids (<see cref="IncidentNotification.IdOf"/>) of everybody the incident was sent to.</summary>
    Task<IReadOnlySet<string>> ListNotifiedAsync(Guid incidentId, CancellationToken cancellationToken = default);

    /// <summary>A kiosk's incidents that are not resolved yet.</summary>
    Task<IReadOnlyList<Incident>> ListOpenForKioskAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>A kiosk's incidents raised between the two times, newest first: what else happened there.</summary>
    Task<IReadOnlyList<Incident>> ListForKioskAsync(Guid kioskId, DateTime from, DateTime to, int take, CancellationToken cancellationToken = default);
}

/// <summary>Real-time money events for the admin panel (plan section 6, <c>/hubs/admin</c>).</summary>
/// <remarks>
/// Each goes to the accounts that could read the same thing over HTTP: they
/// hold the permission of the list, and the kiosk is in their scope. What is
/// about no kiosk - a wallet top-up - is for those who see every kiosk (T-261).
/// </remarks>
public interface IPaymentsAdminNotifier
{
    /// <param name="kioskId">The kiosk it is about: only the accounts that may see that kiosk are told.</param>
    Task IncidentRaisedAsync(
        Guid incidentId, string type, string severity, CancellationToken cancellationToken = default, Guid? kioskId = null);

    /// <summary>An incident was acknowledged or resolved: open lists and counters refresh.</summary>
    Task IncidentResolvedAsync(
        Guid incidentId, string type, string status, CancellationToken cancellationToken = default, Guid? kioskId = null);

    /// <param name="kioskId">The kiosk of the payment the refund belongs to.</param>
    Task RefundStatusChangedAsync(
        Guid refundId, Guid paymentId, string status, CancellationToken cancellationToken = default, Guid? kioskId = null);

    Task PaymentSucceededAsync(Guid paymentId, Guid? kioskId, decimal amount, CancellationToken cancellationToken = default);

    /// <param name="kioskId">The kiosk the receipt was issued for.</param>
    Task FiscalStatusChangedAsync(Guid receiptId, string status, CancellationToken cancellationToken = default, Guid? kioskId = null);
}

/// <summary>
/// Opens an incident once per cause and tells the panel (plan section 8A.1:
/// "incident gələndə səsli siqnal + toast") and, through
/// <see cref="NeoPrint.Contracts.IncidentRaised"/>, the people who are not
/// looking at it (e-mail, Telegram). Closes it when the cause goes away.
/// </summary>
public sealed class IncidentReporter
{
    private readonly IIncidentRepository _incidents;
    private readonly IPaymentsAdminNotifier _notifier;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<IncidentReporter> _logger;

    public IncidentReporter(
        IIncidentRepository incidents,
        IPaymentsAdminNotifier notifier,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<IncidentReporter> logger)
    {
        _incidents = incidents;
        _notifier = notifier;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>The incident opened now; null when one with this key is open already.</returns>
    public async Task<Incident?> RaiseAsync(
        IncidentType type,
        IncidentSeverity severity,
        UserMessage details,
        string dedupKey,
        CancellationToken cancellationToken,
        Guid? kioskId = null,
        string? entityType = null,
        Guid? entityId = null)
    {
        var incident = Incident.Raise(type, severity, details, dedupKey, _clock.UtcNow, kioskId, entityType, entityId);

        if (!await _incidents.TryAddAsync(incident, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        _logger.LogWarning("Incident {Type} ({Severity}) raised: {DedupKey}.", type, severity, dedupKey);
        await _events
            .PublishAsync(
                new NeoPrint.Contracts.IncidentRaised(incident.Id, type.ToString(), severity.ToString(), kioskId, _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);
        await _notifier
            .IncidentRaisedAsync(incident.Id, type.ToString(), severity.ToString(), cancellationToken, kioskId)
            .ConfigureAwait(false);

        return incident;
    }

    /// <summary>The cause went away: the open incident with this key is closed by the system.</summary>
    /// <returns>False when none was open.</returns>
    public async Task<bool> ResolveAsync(string dedupKey, UserMessage resolution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        Incident? open;

        for (var attempt = 1; ; attempt++)
        {
            open = await _incidents.FindOpenByDedupKeyAsync(dedupKey, cancellationToken).ConfigureAwait(false);

            if (open is null)
            {
                return false;
            }

            open.Resolve(null, resolution.Encode(), _clock.UtcNow);

            try
            {
                await _incidents.UpdateAsync(open, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (ConcurrencyException) when (attempt < 5)
            {
                // Being acknowledged, or noted as sent to somebody, in the
                // same moment: read it again and close that.
            }
        }

        _logger.LogInformation("Incident {Type} closed by the system: {DedupKey}.", open.Type, dedupKey);
        await _notifier
            .IncidentResolvedAsync(open.Id, open.Type.ToString(), open.Status.ToString(), cancellationToken, open.KioskId)
            .ConfigureAwait(false);

        return true;
    }
}
