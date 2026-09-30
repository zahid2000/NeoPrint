using NeoPrint.Domain.Common;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Application.Wallets;

/// <summary>The <c>wallets</c> collection: the materialized balance (plan section 5).</summary>
public interface IWalletRepository
{
    Task<Wallet?> FindAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AddAsync(Wallet wallet, CancellationToken cancellationToken = default);

    /// <exception cref="ConcurrencyException">Somebody else moved the wallet first.</exception>
    Task UpdateAsync(Wallet wallet, CancellationToken cancellationToken = default);

    /// <summary>Every wallet, a page at a time, for the reconciliation job.</summary>
    Task<IReadOnlyList<Wallet>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>The <c>wallet_transactions</c> ledger (plan section 2.14): append only.</summary>
public interface IWalletTransactionRepository
{
    Task<WalletTransaction?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateLedgerEntryException">A line with this dedup key already exists.</exception>
    Task AddAsync(WalletTransaction entry, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<WalletTransaction> Items, long Total)> ListAsync(
        Guid userId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>The ledger's own totals for a user: what the balance must be.</summary>
    Task<(Money Amount, int BonusPages)> SumAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>The unique dedup key refused a second line: the movement was already recorded.</summary>
public sealed class DuplicateLedgerEntryException : Exception
{
    public DuplicateLedgerEntryException(string dedupKey)
        : base($"The wallet ledger already has {dedupKey}.")
        => DedupKey = dedupKey;

    public string DedupKey { get; }
}

/// <summary>
/// Moves a wallet: the ledger line and the balance together, in the caller's
/// transaction (plan section 2.14; docs/DOMAIN_RULES.md "Wallet").
/// </summary>
/// <remarks>
/// <para>
/// A movement already in the ledger (same dedup key) is returned as it is and
/// nothing moves: a resent request, a redelivered event or a retried job credits
/// once. Two movements racing each other both read the wallet at the same
/// version; the second write conflicts, its transaction aborts and the retry
/// finds the first one's line.
/// </para>
/// <para>
/// No Redis lock is taken, unlike plan section 10 suggests: the transaction,
/// the row version and the unique dedup key already make a double movement
/// impossible, and a lock would only add a way for the wallet to be
/// unavailable when Redis is (docs/DECISIONS.md, 2026-09-30).
/// </para>
/// </remarks>
public sealed class WalletLedger
{
    private readonly IWalletRepository _wallets;
    private readonly IWalletTransactionRepository _ledger;

    public WalletLedger(IWalletRepository wallets, IWalletTransactionRepository ledger)
    {
        _wallets = wallets;
        _ledger = ledger;
    }

    /// <returns>The ledger line, and whether this call wrote it (false: it was already there).</returns>
    public async Task<(WalletTransaction Entry, bool Applied)> PostAsync(
        Guid userId,
        WalletTransactionType type,
        Money amount,
        int bonusPages,
        string refId,
        string dedupKey,
        Guid? actorId,
        DateTime now,
        string? note,
        CancellationToken cancellationToken)
    {
        if (await _ledger.FindByDedupKeyAsync(dedupKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return (existing, false);
        }

        var wallet = await _wallets.FindAsync(userId, cancellationToken).ConfigureAwait(false);
        var isNew = wallet is null;
        wallet ??= Wallet.For(userId);

        var entry = wallet.Post(type, amount, bonusPages, refId, dedupKey, actorId, now, note);

        await _ledger.AddAsync(entry, cancellationToken).ConfigureAwait(false);

        if (isNew)
        {
            await _wallets.AddAsync(wallet, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _wallets.UpdateAsync(wallet, cancellationToken).ConfigureAwait(false);
        }

        return (entry, true);
    }

    public async Task<Wallet> ReadAsync(Guid userId, CancellationToken cancellationToken)
        => await _wallets.FindAsync(userId, cancellationToken).ConfigureAwait(false) ?? Wallet.For(userId);
}
