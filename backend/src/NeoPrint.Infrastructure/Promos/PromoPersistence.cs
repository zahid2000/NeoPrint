using System.Text.RegularExpressions;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Application.Promos;
using NeoPrint.Domain.Promos;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Promos;

public sealed class PromoCodeRepository : IPromoCodeRepository
{
    private readonly MongoContext _context;

    public PromoCodeRepository(MongoContext context) => _context = context;

    public Task<PromoCode?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PromoCodes.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<PromoCode?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => _context.PromoCodes.Find(p => p.Code == code).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<bool> TryAddAsync(PromoCode promo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promo);

        if (await _context.PromoCodes.Find(p => p.Code == promo.Code).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AggregateWriter.InsertAsync(_context, _context.PromoCodes, promo, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task UpdateAsync(PromoCode promo, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.PromoCodes, promo, cancellationToken);

    public async Task<bool> TryTakeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // $expr compares two fields of the same document; a plain filter cannot.
        var filter = Builders<PromoCode>.Filter.Eq(p => p.Id, id)
                 & Builders<PromoCode>.Filter.Or(
                     Builders<PromoCode>.Filter.Eq(p => p.TotalLimit, null),
                     new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray { "$UsedCount", "$TotalLimit" })));

        var result = await _context.PromoCodes
            .UpdateOneAsync(filter, Builders<PromoCode>.Update.Inc(p => p.UsedCount, 1), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount == 1;
    }

    public Task GiveBackAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PromoCodes.UpdateOneAsync(
            Builders<PromoCode>.Filter.Eq(p => p.Id, id) & Builders<PromoCode>.Filter.Gt(p => p.UsedCount, 0),
            Builders<PromoCode>.Update.Inc(p => p.UsedCount, -1),
            cancellationToken: cancellationToken);

    public Task TakeAnywayAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PromoCodes.UpdateOneAsync(
            Builders<PromoCode>.Filter.Eq(p => p.Id, id), Builders<PromoCode>.Update.Inc(p => p.UsedCount, 1), cancellationToken: cancellationToken);

    public async Task<(IReadOnlyList<PromoCode> Items, long Total)> SearchAsync(
        string? code, bool? active, int skip, int take, CancellationToken cancellationToken = default)
    {
        var builder = Builders<PromoCode>.Filter;
        var query = builder.Empty;

        if (!string.IsNullOrWhiteSpace(code))
        {
            query &= builder.Regex(p => p.Code, new BsonRegularExpression("^" + Regex.Escape(code.Trim().ToUpperInvariant())));
        }

        if (active is { } isActive)
        {
            query &= builder.Eq(p => p.IsActive, isActive);
        }

        var items = await _context.PromoCodes.Find(query).SortByDescending(p => p.CreatedAt).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.PromoCodes.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }
}

public sealed class PromoUsageRepository : IPromoUsageRepository
{
    private readonly MongoContext _context;

    public PromoUsageRepository(MongoContext context) => _context = context;

    public Task<PromoUsage?> FindByPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default)
        => _context.PromoUsages.Find(u => u.PaymentId == paymentId).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(PromoUsage usage, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.PromoUsages, usage, cancellationToken);

    public Task UpdateAsync(PromoUsage usage, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.PromoUsages, usage, cancellationToken);

    public Task<long> CountHeldAsync(Guid promoCodeId, Guid userId, CancellationToken cancellationToken = default)
        => _context.PromoUsages.CountDocumentsAsync(
            u => u.PromoCodeId == promoCodeId && u.UserId == userId
                 && (u.Status == PromoUsageStatus.Reserved || u.Status == PromoUsageStatus.Confirmed),
            cancellationToken);

    public async Task<(long Count, decimal Discount)> StatsAsync(Guid promoCodeId, CancellationToken cancellationToken = default)
    {
        var confirmed = await _context.PromoUsages
            .Find(u => u.PromoCodeId == promoCodeId && u.Status == PromoUsageStatus.Confirmed)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (confirmed.Count, confirmed.Sum(u => u.Discount.Amount));
    }
}
