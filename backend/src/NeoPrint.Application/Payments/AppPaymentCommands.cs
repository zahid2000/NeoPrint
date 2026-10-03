using MediatR;

using NeoPrint.Application.Behaviors;

namespace NeoPrint.Application.Payments;

/// <summary>
/// The confirmation screen after the app scanned the kiosk's
/// <c>neoprint://pay/{id}</c> QR (plan section 9.2, step 3): what is being
/// paid for and what the user has to pay with.
/// </summary>
public sealed record GetAppPaymentQuery(Guid UserId, Guid PaymentId) : IRequest<AppPaymentView>;

/// <summary>
/// Pays a kiosk's app payment from the mobile app (plan section 9.2, step 3:
/// <c>POST payments/{id}/pay-with-app</c>): bonus pages first if asked, the
/// rest from the wallet or a card.
/// </summary>
/// <param name="Source"><c>Wallet</c> or <c>Card</c>: what pays whatever the bonus pages do not.</param>
/// <param name="ExpectedAmount">What the app showed as the money part; a different price (a promo that stopped applying) is refused, never charged silently.</param>
/// <param name="PromoCode">A code typed in the app when the kiosk order has none (plan section 2.7: one per order); a code that does not apply is a 400 on <c>promoCode</c> and nothing is taken.</param>
public sealed record PayWithAppCommand(Guid UserId, Guid PaymentId, string Source, bool UseBonus, decimal? ExpectedAmount = null, string? PromoCode = null)
    : IRequest<AppPaymentView>;

/// <summary>
/// Step one of <see cref="PayWithAppCommand"/>: the wallet and bonus parts are
/// taken (reserved) and, when nothing is left for the card, the payment is
/// settled in the same transaction.
/// </summary>
public sealed record ReserveAppPaymentCommand(Guid UserId, Guid PaymentId, string Source, bool UseBonus, decimal? ExpectedAmount = null, string? PromoCode = null)
    : IRequest<AppPaymentPlan>, ITransactionalRequest;

/// <summary>
/// After a card charge (a top-up or the card part of an app payment): the
/// acquirer's own status decides - paid, or declined and the reservation
/// goes back.
/// </summary>
public sealed record SettleCardPaymentCommand(Guid PaymentId) : IRequest<SettleCardOutcome>, ITransactionalRequest;

/// <summary>What is left to do after the reservation.</summary>
/// <param name="CardReference">The acquirer's key for this try; null when no card is charged.</param>
public sealed record AppPaymentPlan(Guid PaymentId, bool Settled, string? CardReference, decimal CardAmount);

public enum SettleCardOutcome
{
    Paid = 0,
    Declined = 1,

    /// <summary>The acquirer does not know yet; reconciliation will ask again.</summary>
    Unknown = 2,
}

/// <param name="BonusPagesUsable">How many bonus pages this order can take (plan section 2.6: at most <c>Referral.MaxBonusPagesPerJob</c>).</param>
/// <param name="BonusValue">What those pages take off the price.</param>
/// <param name="CollectAtKiosk">Paid for later (plan section 9.4): once paid it waits at the kiosk for the user's QR, not in the queue.</param>
/// <param name="ScheduledAt">A scheduled print (plan section 9.5): once paid it waits for this time, and the app asks then.</param>
public sealed record AppPaymentView(
    Guid Id,
    string Status,
    decimal Amount,
    string Currency,
    DateTime ExpiresAt,
    int SecondsLeft,
    string? KioskCode,
    string? KioskName,
    string? JobCode,
    string? FileName,
    int Pages,
    int Copies,
    bool Color,
    bool Duplex,
    string? Paper,
    decimal WalletBalance,
    int BonusPages,
    int BonusPagesUsable,
    decimal BonusValue,
    string? Method,
    DateTime? PaidAt,
    string? PromoCode = null,
    decimal PromoDiscount = 0,
    decimal PromoDiscountWithBonus = 0,
    string? PromoRefusal = null,
    bool CollectAtKiosk = false,
    DateTime? ScheduledAt = null);
