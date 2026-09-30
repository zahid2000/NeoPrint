using MongoDB.Driver;

using NeoPrint.Application.Fiscal;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Fiscal;

public sealed class FiscalReceiptRepository : IFiscalReceiptRepository
{
    private readonly MongoContext _context;

    public FiscalReceiptRepository(MongoContext context) => _context = context;

    public async Task<bool> TryAddAsync(FiscalReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // Checked first: inside a transaction a duplicate key aborts it, so the
        // unique index is the backstop for a race, not the normal path.
        if (await _context.FiscalReceipts.Find(r => r.DedupKey == receipt.DedupKey).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AggregateWriter.InsertAsync(_context, _context.FiscalReceipts, receipt, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<FiscalReceipt?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.FiscalReceipts.Find(r => r.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<FiscalReceipt?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default)
        => _context.FiscalReceipts.Find(r => r.DedupKey == dedupKey).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(FiscalReceipt receipt, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.FiscalReceipts, receipt, cancellationToken);

    public async Task<IReadOnlyList<FiscalReceipt>> ListForPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default)
        => await _context.FiscalReceipts.Find(r => r.PaymentId == paymentId).SortBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<FiscalReceipt>> ListDueAsync(DateTime now, int take, CancellationToken cancellationToken = default)
        => await _context.FiscalReceipts
            .Find(r => r.Status == FiscalReceiptStatus.Pending && r.NextAttemptAt <= now)
            .SortBy(r => r.NextAttemptAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<FiscalReceipt>> ListPendingSinceAsync(DateTime createdBefore, int take, CancellationToken cancellationToken = default)
        => await _context.FiscalReceipts
            .Find(r => r.Status == FiscalReceiptStatus.Pending && r.CreatedAt <= createdBefore)
            .SortBy(r => r.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<FiscalReceipt> Items, long Total)> SearchAsync(
        IReadOnlyCollection<FiscalReceiptStatus>? statuses,
        IReadOnlyCollection<FiscalReceiptType>? types,
        Guid? paymentId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<FiscalReceipt>.Filter;
        var query = builder.Empty;

        if (statuses is { Count: > 0 })
        {
            query &= builder.In(r => r.Status, statuses);
        }

        if (types is { Count: > 0 })
        {
            query &= builder.In(r => r.Type, types);
        }

        if (paymentId is { } payment)
        {
            query &= builder.Eq(r => r.PaymentId, payment);
        }

        var items = await _context.FiscalReceipts.Find(query).SortByDescending(r => r.CreatedAt).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.FiscalReceipts.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }

    public Task<long> CountAsync(FiscalReceiptStatus status, CancellationToken cancellationToken = default)
        => _context.FiscalReceipts.CountDocumentsAsync(r => r.Status == status, cancellationToken);
}
