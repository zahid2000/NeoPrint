using System.Globalization;

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using NeoPrint.Application.Receipts;
using NeoPrint.Domain.Receipts;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Receipts;

public sealed class ReceiptRepository : IReceiptRepository
{
    private readonly MongoContext _context;

    public ReceiptRepository(MongoContext context) => _context = context;

    public Task<Receipt?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Receipts.Find(r => r.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<bool> TryAddAsync(Receipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // Checked first: inside a transaction a duplicate key aborts it; the
        // id (the payment's) is the backstop for a race.
        if (await _context.Receipts.Find(r => r.Id == receipt.Id).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AggregateWriter.InsertAsync(_context, _context.Receipts, receipt, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task UpdateAsync(Receipt receipt, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Receipts, receipt, cancellationToken);

    public async Task<IReadOnlyList<Receipt>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default)
        => await _context.Receipts.Find(r => r.UserId == userId).SortByDescending(r => r.CreatedAt).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Receipt>> ListWithoutFiscalAsync(DateTime since, int take, CancellationToken cancellationToken = default)
        => await _context.Receipts.Find(r => !r.FiscalShown && r.CreatedAt >= since).SortBy(r => r.CreatedAt).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<(IReadOnlyList<Receipt> Items, long Total)> SearchAsync(
        string? number, Guid? paymentId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var builder = Builders<Receipt>.Filter;
        var query = builder.Empty;

        if (!string.IsNullOrEmpty(number))
        {
            query &= builder.Eq(r => r.Number, number);
        }

        if (paymentId is { } payment)
        {
            query &= builder.Eq(r => r.PaymentId, payment);
        }

        var items = await _context.Receipts.Find(query).SortByDescending(r => r.CreatedAt).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.Receipts.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }
}

/// <summary>A running count, one document per series (<c>receipts:2026</c>).</summary>
public sealed class Counter
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long Value { get; set; }
}

/// <summary>
/// Receipt numbers from an atomic increment. It joins the caller's
/// transaction, so a receipt that is not saved gives its number back and the
/// series has no gaps.
/// </summary>
public sealed class ReceiptNumbers : IReceiptNumbers
{
    private readonly MongoContext _context;

    public ReceiptNumbers(MongoContext context) => _context = context;

    public async Task<long> NextAsync(int year, CancellationToken cancellationToken = default)
    {
        var counter = await _context.Counters
            .FindOneAndUpdateAsync(
                Builders<Counter>.Filter.Eq(c => c.Id, $"receipts:{year.ToString(CultureInfo.InvariantCulture)}"),
                Builders<Counter>.Update.Inc(c => c.Value, 1),
                new FindOneAndUpdateOptions<Counter, Counter> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                cancellationToken)
            .ConfigureAwait(false);

        return counter!.Value;
    }
}
