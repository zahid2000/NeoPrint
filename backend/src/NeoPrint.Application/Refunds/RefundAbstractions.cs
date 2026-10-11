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

    Task<(IReadOnlyList<Incident> Items, long Total)> ListAsync(
        IReadOnlyCollection<IncidentStatus>? statuses, IReadOnlyCollection<IncidentType>? types, int skip, int take, CancellationToken cancellationToken = default);

    Task<long> CountOpenAsync(IncidentType type, CancellationToken cancellationToken = default);

    /// <summary>How many incidents of a kiosk are not resolved yet.</summary>
    Task<long> CountOpenForKioskAsync(Guid kioskId, CancellationToken cancellationToken = default);

    Task<Incident?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default);

    /// <summary>A kiosk's incidents raised between the two times, newest first: what else happened there.</summary>
    Task<IReadOnlyList<Incident>> ListForKioskAsync(Guid kioskId, DateTime from, DateTime to, int take, CancellationToken cancellationToken = default);
}

/// <summary>Real-time money events for the admin panel (plan section 6, <c>/hubs/admin</c>).</summary>
public interface IPaymentsAdminNotifier
{
    Task IncidentRaisedAsync(Guid incidentId, string type, string severity, CancellationToken cancellationToken = default);

    Task RefundStatusChangedAsync(Guid refundId, Guid paymentId, string status, CancellationToken cancellationToken = default);

    Task PaymentSucceededAsync(Guid paymentId, Guid? kioskId, decimal amount, CancellationToken cancellationToken = default);

    Task FiscalStatusChangedAsync(Guid receiptId, string status, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens an incident once per cause and tells the panel (plan section 8A.1:
/// "incident gələndə səsli siqnal + toast").
/// </summary>
public sealed class IncidentReporter
{
    private readonly IIncidentRepository _incidents;
    private readonly IPaymentsAdminNotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger<IncidentReporter> _logger;

    public IncidentReporter(IIncidentRepository incidents, IPaymentsAdminNotifier notifier, IClock clock, ILogger<IncidentReporter> logger)
    {
        _incidents = incidents;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task RaiseAsync(
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
            return;
        }

        _logger.LogWarning("Incident {Type} ({Severity}) raised: {DedupKey}.", type, severity, dedupKey);
        await _notifier.IncidentRaisedAsync(incident.Id, type.ToString(), severity.ToString(), cancellationToken).ConfigureAwait(false);
    }
}
