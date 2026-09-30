using MediatR;

using NeoPrint.Application.Behaviors;

namespace NeoPrint.Application.Wallets;

/// <summary>A user's balance and bonus pages (plan section 6: <c>GET wallet</c>).</summary>
public sealed record GetWalletQuery(Guid UserId) : IRequest<WalletView>;

/// <summary>The ledger, newest first (<c>GET wallet/transactions</c>).</summary>
public sealed record ListWalletTransactionsQuery(Guid UserId, int Page, int PageSize) : IRequest<WalletTransactionPage>;

/// <summary>
/// Puts money on the wallet from a card (plan section 2.14: never paid back
/// out). Opens a payment, charges the card with the payment as its reference,
/// then credits the wallet once.
/// </summary>
public sealed record TopUpWalletCommand(Guid UserId, decimal Amount, string? IdempotencyKey) : IRequest<TopUpResponse>;

/// <summary>
/// An admin correction (plan section 2.14): a reason is mandatory, it is
/// audited, and it can never take the balance below zero - so it cannot move
/// money out to anywhere.
/// </summary>
public sealed record AdjustWalletCommand(
    Guid AdminUserId, Guid UserId, decimal Amount, int BonusPages, string? Reason, string? IdempotencyKey, string? Ip)
    : IRequest<WalletView>, ITransactionalRequest;

public sealed record WalletView(decimal Balance, int BonusPages, string Currency);

/// <param name="Note">A stored message (the admin's reason or a system key).</param>
public sealed record WalletTransactionView(
    Guid Id, string Type, decimal Amount, int BonusPages, decimal BalanceAfter, int BonusPagesAfter, DateTime CreatedAt, string? Note);

public sealed record WalletTransactionPage(IReadOnlyList<WalletTransactionView> Items, long Total, int Page, int PageSize);

public sealed record TopUpResponse(Guid PaymentId, string Status, WalletView Wallet);
