using MongoDB.Driver;

using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Refunds;

/// <inheritdoc cref="IRefundRepository" />
public sealed class RefundRepository : IRefundRepository
{
    private readonly MongoContext _context;

    public RefundRepository(MongoContext context) => _context = context;

    public Task<Refund?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Refunds.Find(r => r.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<Refund?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default)
        => _context.Refunds.Find(r => r.DedupKey == dedupKey).FirstOrDefaultAsync(cancellationToken)!;

    public async Task AddAsync(Refund refund, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refund);

        try
        {
            await AggregateWriter.InsertAsync(_context, _context.Refunds, refund, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateRefundException(refund.DedupKey);
        }
    }

    public Task UpdateAsync(Refund refund, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Refunds, refund, cancellationToken);

    public async Task<IReadOnlyList<Refund>> ListForPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default)
        => await _context.Refunds.Find(r => r.PaymentId == paymentId).SortBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> ListOpenPaymentIdsAsync(int take, CancellationToken cancellationToken = default)
        => (await _context.Refunds
                .Find(r => r.Status == RefundStatus.Pending || r.Status == RefundStatus.Processing)
                .SortByDescending(r => r.CreatedAt)
                .Limit(take)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(r => r.PaymentId)
            .Distinct()
            .ToList();

    public Task<long> CountAsync(RefundStatus status, CancellationToken cancellationToken = default)
        => _context.Refunds.CountDocumentsAsync(r => r.Status == status, cancellationToken);

    public async Task<IReadOnlyList<Refund>> ListCompletedAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => await _context.Refunds
            .Find(r => r.Status == RefundStatus.Succeeded && r.CompletedAt >= from && r.CompletedAt < to)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Refund>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => await _context.Refunds.Find(r => r.JobId == jobId).SortBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Refund>> ListDueAsync(DateTime now, DateTime createdBefore, int take, CancellationToken cancellationToken = default)
        => await _context.Refunds
            .Find(r => (r.Status == RefundStatus.Pending && r.CreatedAt <= createdBefore)
                       || (r.Status == RefundStatus.Processing && r.NextAttemptAt != null && r.NextAttemptAt <= now))
            .SortBy(r => r.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Refund>> ListAwaitingProviderAsync(DateTime startedBefore, int take, CancellationToken cancellationToken = default)
        => await _context.Refunds
            .Find(r => r.Status == RefundStatus.Processing && r.AwaitingProvider && r.CreatedAt <= startedBefore)
            .SortBy(r => r.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<Refund> Items, long Total)> SearchAsync(
        RefundFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var builder = Builders<Refund>.Filter;
        var query = builder.Empty;

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            query &= builder.In(r => r.Status, statuses);
        }

        if (filter.Reasons is { Count: > 0 } reasons)
        {
            query &= builder.In(r => r.Reason, reasons);
        }

        if (filter.PaymentId is { } paymentId)
        {
            query &= builder.Eq(r => r.PaymentId, paymentId);
        }

        var items = await _context.Refunds.Find(query).SortByDescending(r => r.CreatedAt).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.Refunds.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }
}

/// <inheritdoc cref="IRefundVoucherRepository" />
public sealed class RefundVoucherRepository : IRefundVoucherRepository
{
    private readonly MongoContext _context;

    public RefundVoucherRepository(MongoContext context) => _context = context;

    public Task<RefundVoucher?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => _context.RefundVouchers.Find(v => v.Code == code).FirstOrDefaultAsync(cancellationToken)!;

    public Task<RefundVoucher?> FindByRefundAsync(Guid refundId, CancellationToken cancellationToken = default)
        => _context.RefundVouchers.Find(v => v.RefundId == refundId).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(RefundVoucher voucher, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.RefundVouchers, voucher, cancellationToken);

    public Task UpdateAsync(RefundVoucher voucher, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.RefundVouchers, voucher, cancellationToken);

    public async Task<(IReadOnlyList<RefundVoucher> Items, long Total)> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        var items = await _context.RefundVouchers.Find(Builders<RefundVoucher>.Filter.Empty).SortByDescending(v => v.CreatedAt)
            .Skip(skip).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.RefundVouchers.CountDocumentsAsync(Builders<RefundVoucher>.Filter.Empty, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }
}

/// <inheritdoc cref="IIncidentRepository" />
public sealed class IncidentRepository : IIncidentRepository
{
    private readonly MongoContext _context;

    public IncidentRepository(MongoContext context) => _context = context;

    public async Task<bool> TryAddAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);

        // Checked first: inside a transaction a duplicate key aborts it, so the
        // unique index is the backstop for a race, not the normal path.
        if (await _context.Incidents.Find(i => i.DedupKey == incident.DedupKey && i.Open).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AggregateWriter.InsertAsync(_context, _context.Incidents, incident, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<Incident?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Incidents.Find(i => i.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(Incident incident, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Incidents, incident, cancellationToken);

    public async Task<(IReadOnlyList<Incident> Items, long Total)> ListAsync(
        IReadOnlyCollection<IncidentStatus>? statuses, IReadOnlyCollection<IncidentType>? types, int skip, int take, CancellationToken cancellationToken = default)
    {
        var builder = Builders<Incident>.Filter;
        var query = builder.Empty;

        if (statuses is { Count: > 0 })
        {
            query &= builder.In(i => i.Status, statuses);
        }

        if (types is { Count: > 0 })
        {
            query &= builder.In(i => i.Type, types);
        }

        var items = await _context.Incidents.Find(query).SortBy(i => i.Severity).ThenByDescending(i => i.CreatedAt)
            .Skip(skip).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.Incidents.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }

    public Task<long> CountOpenAsync(IncidentType type, CancellationToken cancellationToken = default)
        => _context.Incidents.CountDocumentsAsync(i => i.Type == type && i.Open, cancellationToken);

    public Task<long> CountOpenForKioskAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => _context.Incidents.CountDocumentsAsync(i => i.KioskId == kioskId && i.Open, cancellationToken);

    public Task<Incident?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default)
        => _context.Incidents.Find(i => i.DedupKey == dedupKey && i.Open).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<Incident>> ListForKioskAsync(Guid kioskId, DateTime from, DateTime to, int take, CancellationToken cancellationToken = default)
        => await _context.Incidents
            .Find(i => i.KioskId == kioskId && i.CreatedAt >= from && i.CreatedAt <= to)
            .SortByDescending(i => i.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
