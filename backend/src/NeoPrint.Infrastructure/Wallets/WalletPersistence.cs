using MongoDB.Driver;

using NeoPrint.Application.Wallets;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Wallets;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Wallets;

/// <inheritdoc cref="IWalletRepository" />
public sealed class WalletRepository : IWalletRepository
{
    private readonly MongoContext _context;

    public WalletRepository(MongoContext context) => _context = context;

    public Task<Wallet?> FindAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.Wallets.Find(wallet => wallet.Id == userId).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(Wallet wallet, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Wallets, wallet, cancellationToken);

    public Task UpdateAsync(Wallet wallet, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Wallets, wallet, cancellationToken);

    public async Task<IReadOnlyList<Wallet>> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
        => await _context.Wallets
            .Find(Builders<Wallet>.Filter.Empty)
            .SortBy(wallet => wallet.Id)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <inheritdoc cref="IWalletTransactionRepository" />
public sealed class WalletTransactionRepository : IWalletTransactionRepository
{
    private readonly MongoContext _context;

    public WalletTransactionRepository(MongoContext context) => _context = context;

    public Task<WalletTransaction?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default)
        => _context.WalletTransactions.Find(entry => entry.DedupKey == dedupKey).FirstOrDefaultAsync(cancellationToken)!;

    public async Task AddAsync(WalletTransaction entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await _context.WalletTransactions.InsertOneAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateLedgerEntryException(entry.DedupKey);
        }
    }

    public async Task<(IReadOnlyList<WalletTransaction> Items, long Total)> ListAsync(
        Guid userId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var filter = Builders<WalletTransaction>.Filter.Eq(entry => entry.UserId, userId);

        var items = await _context.WalletTransactions
            .Find(filter)
            .SortByDescending(entry => entry.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var total = await _context.WalletTransactions.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (items, total);
    }

    public async Task<(Money Amount, int BonusPages)> SumAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Summed here rather than in the database: Money is stored as a
        // Decimal128 sub-document, and a user's ledger is short.
        var entries = await _context.WalletTransactions
            .Find(entry => entry.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (entries.Aggregate(Money.Zero, (sum, entry) => sum + entry.Amount), entries.Sum(entry => entry.BonusPages));
    }
}
