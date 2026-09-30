using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Wallets;

/// <summary>Why the wallet moved (plan section 5, <c>wallet_transactions.Type</c>).</summary>
public enum WalletTransactionType
{
    TopUp = 0,
    Payment = 1,
    Refund = 2,
    Bonus = 3,
    VoucherRedeem = 4,
    AdminAdjust = 5,
}

/// <summary>
/// One line of the wallet ledger - the source of truth (plan section 2.14;
/// docs/DOMAIN_RULES.md "Wallet").
/// </summary>
/// <remarks>
/// <para>
/// Written once and never changed. Money in is positive, money out negative;
/// the same for bonus pages.
/// </para>
/// <para>
/// <see cref="DedupKey"/> carries a unique index: <c>topup:{paymentId}</c>,
/// <c>payment:{paymentId}</c>, <c>refund:{refundId}</c>, <c>voucher:{code}</c>,
/// <c>adjust:{requestId}</c>. A retried consumer or a resent request writes
/// the same key, and the database refuses the second line - so the wallet is
/// never credited or charged twice for one thing.
/// </para>
/// </remarks>
public sealed class WalletTransaction : Entity
{
    public Guid UserId { get; set; }

    public WalletTransactionType Type { get; set; }

    /// <summary>Signed: positive in, negative out.</summary>
    public Money Amount { get; set; }

    /// <summary>Signed, like <see cref="Amount"/>.</summary>
    public int BonusPages { get; set; }

    /// <summary>What caused it: a payment, a refund, a voucher code, an adjustment.</summary>
    public string RefId { get; set; } = string.Empty;

    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The balance right after this line, for the history screen and the reconciliation job.</summary>
    public Money BalanceAfter { get; set; }

    public int BonusPagesAfter { get; set; }

    /// <summary>The admin's reason for an adjustment (plan section 2.14: mandatory), a stored message otherwise.</summary>
    public string? Note { get; set; }

    /// <summary>Who did it: the user, an admin, or null for the system.</summary>
    public Guid? ActorId { get; set; }
}

/// <summary>
/// A user's prepaid service credit (plan section 2.14).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Balance"/> is only the ledger's sum, kept next to it so a read
/// does not add up every line; it changes in the same transaction as the line
/// that explains it (<see cref="Post"/>), and <c>WalletLedgerReconciliationJob</c>
/// raises an incident if the two ever disagree.
/// </para>
/// <para>
/// There is no way out: the balance is never paid to a card, returned or
/// cashed - by the user or by an admin. Only a print, a refund of a print, a
/// voucher or an admin correction moves it. Keyed by the user's id.
/// </para>
/// </remarks>
public sealed class Wallet : Entity
{
    public Money Balance { get; private set; }

    public int BonusPages { get; private set; }

    public Guid UserId => Id;

    public static Wallet For(Guid userId) => new() { Id = userId, Balance = Money.Zero };

    /// <summary>
    /// Applies a movement and returns the ledger line that records it.
    /// </summary>
    /// <exception cref="DomainException">It would take the balance or the bonus pages below zero.</exception>
    public WalletTransaction Post(
        WalletTransactionType type,
        Money amount,
        int bonusPages,
        string refId,
        string dedupKey,
        Guid? actorId,
        DateTime now,
        string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

        if (amount.IsZero && bonusPages == 0)
        {
            throw new DomainException("wallet.nothingToPost");
        }

        var balance = Balance + amount;
        var pages = BonusPages + bonusPages;

        if (balance.IsNegative)
        {
            throw new DomainException(UserMessage.Of("wallet.insufficient", ("balance", Balance.Amount), ("needed", (-amount).Amount)));
        }

        if (pages < 0)
        {
            throw new DomainException(UserMessage.Of("wallet.insufficientBonus", ("pages", BonusPages), ("needed", -bonusPages)));
        }

        if (!AllowedDirection(type, amount, bonusPages))
        {
            throw new DomainException(UserMessage.Of("wallet.wrongDirection", ("type", type)));
        }

        Balance = balance;
        BonusPages = pages;

        return new WalletTransaction
        {
            UserId = Id,
            Type = type,
            Amount = amount,
            BonusPages = bonusPages,
            RefId = refId,
            DedupKey = dedupKey,
            BalanceAfter = Balance,
            BonusPagesAfter = BonusPages,
            ActorId = actorId,
            Note = note,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Which way each type may move money. A refund, a top-up, a voucher or a
    /// bonus only ever adds; a payment only takes. Only an admin correction
    /// goes either way - and it can never go below zero, so it cannot take
    /// money out to anywhere.
    /// </summary>
    private static bool AllowedDirection(WalletTransactionType type, Money amount, int bonusPages) => type switch
    {
        WalletTransactionType.Payment => !amount.IsPositive && bonusPages <= 0,
        WalletTransactionType.AdminAdjust => true,
        _ => !amount.IsNegative && bonusPages >= 0,
    };
}
