using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Payments;

/// <summary>How a payment was made (plan section 5, <c>payments.Method</c>).</summary>
public enum PaymentMethod
{
    /// <summary>A bank app scanning the kiosk's QR (anonymous).</summary>
    BankQr = 0,

    /// <summary>The kiosk's "pay with NeoPrint" QR; the app picks wallet, card or bonus when it pays.</summary>
    MobileApp = 1,

    Wallet = 2,
    Card = 3,
    Bonus = 4,

    /// <summary>More than one source: bonus pages, wallet and card together.</summary>
    Mixed = 5,
}

/// <summary>Where a payment is (docs/STATE_MACHINES.md, "Payment").</summary>
public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Expired = 3,
    Refunded = 4,
    PartiallyRefunded = 5,
    RefundFailed = 6,
}

/// <summary>Where the customer paid from.</summary>
public enum PaymentChannel
{
    Kiosk = 0,
    Mobile = 1,
}

/// <summary>One line of a payment's history (plan section 5, <c>Timeline[]</c>).</summary>
/// <param name="Event">A stable name: <c>created</c>, <c>succeeded</c>, <c>callbackDuplicate</c>, ...</param>
/// <param name="Details">Free text for the admin panel; never a card number, a token or a secret.</param>
public sealed record PaymentEvent(DateTime At, string Event, string? Details = null);

/// <summary>
/// What a payment is made of: the external part (a bank or a card), the
/// wallet part and the bonus pages (plan section 2.5: "hər hissə öz
/// mənbəyinə qaytarılır").
/// </summary>
/// <param name="External">Taken by the bank or card provider.</param>
/// <param name="Wallet">Taken from the user's wallet.</param>
/// <param name="BonusPages">Sides paid with bonus pages; worth 0 AZN on the fiscal receipt.</param>
public sealed record PaymentParts(Money External, Money Wallet, int BonusPages)
{
    public static PaymentParts ExternalOnly(Money amount) => new(amount, Money.Zero, 0);

    public Money Total => External + Wallet;
}

/// <summary>
/// A payment for one print job (plan sections 2.5, 5; docs/PAYMENT_FLOWS.md).
/// </summary>
/// <remarks>
/// <para>
/// The amount comes from the job's price snapshot when the payment is made and
/// is never recomputed. The provider's confirmation is applied once: a
/// callback, the reconciliation job and the expiry job can all learn that the
/// bank took the money, and only the first changes anything
/// (<see cref="Succeed"/> answers whether it did).
/// </para>
/// <para>
/// A payment can succeed after it expired - the bank took the money but its
/// answer came late. That is recorded as it is (<c>Expired → Succeeded</c>):
/// hiding it would leave money the books do not show. The job it was for is
/// gone by then, so the caller refunds it (docs/DECISIONS.md, 2026-09-30).
/// </para>
/// </remarks>
public sealed class Payment : Entity
{
    public Guid JobId { get; set; }

    /// <summary>Null for an anonymous kiosk payment.</summary>
    public Guid? UserId { get; private set; }

    public Guid? KioskId { get; set; }

    public PaymentMethod Method { get; private set; }

    public PaymentChannel Channel { get; set; }

    /// <summary>What the customer pays, from the job's price snapshot.</summary>
    public Money Amount { get; set; }

    /// <summary>How the amount was covered; set when the payment is made.</summary>
    public PaymentParts? Parts { get; private set; }

    public Guid? PromoCodeId { get; set; }

    /// <summary>The promo discount already taken off <see cref="Amount"/>.</summary>
    public Money Discount { get; set; }

    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;

    /// <summary>The provider that holds the external part, for example <c>MockBank</c>; null for wallet and bonus.</summary>
    public string? Provider { get; private set; }

    /// <summary>The provider's reference for the transaction.</summary>
    public string? ProviderRef { get; private set; }

    /// <summary>What the customer shows the bank app: the QR content (plan section 2.9).</summary>
    public string? QrContent { get; private set; }

    /// <summary>Refunded so far, across every refund of this payment.</summary>
    public Money RefundedAmount { get; private set; }

    public Guid? FiscalReceiptId { get; set; }

    public string? IdempotencyKey { get; set; }

    /// <summary>After this the intent is no longer payable (<c>Payment.TimeoutMinutes</c>).</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? PaidAt { get; private set; }

    /// <summary>Set once the provider's status has been checked after expiry or silence.</summary>
    public DateTime? ReconciledAt { get; private set; }

    /// <summary>Why it failed, as a stored message.</summary>
    public string? FailureReason { get; private set; }

    public List<PaymentEvent> Timeline { get; set; } = [];

    /// <summary>Money the customer could still get back.</summary>
    public Money Refundable => Parts is null ? Money.Zero : Parts.Total - RefundedAmount;

    /// <summary>Whether the provider has confirmed the money was taken, whatever happened after.</summary>
    public bool WasPaid => PaidAt is not null;

    public static readonly StateMachine<PaymentStatus> Transitions =
        new StateMachine<PaymentStatus>(nameof(Payment))
            .Allow(PaymentStatus.Pending, PaymentStatus.Succeeded, PaymentStatus.Failed, PaymentStatus.Expired)
            // The bank's answer came after the intent expired (docs/DECISIONS.md).
            .Allow(PaymentStatus.Expired, PaymentStatus.Succeeded)
            .Allow(PaymentStatus.Succeeded, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded, PaymentStatus.RefundFailed)
            .Allow(PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded, PaymentStatus.RefundFailed)
            .Allow(PaymentStatus.RefundFailed, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded);

    /// <summary>A payment waiting for the customer (plan section 9.1, step 2).</summary>
    public static Payment Open(
        Guid jobId,
        Guid? kioskId,
        PaymentMethod method,
        PaymentChannel channel,
        Money amount,
        DateTime now,
        DateTime expiresAt,
        string? idempotencyKey = null)
    {
        if (!amount.IsPositive)
        {
            throw new DomainException("payments.amountNotPositive");
        }

        if (expiresAt <= now)
        {
            throw new DomainException("payments.expiryInPast");
        }

        return new Payment
        {
            JobId = jobId,
            KioskId = kioskId,
            Method = method,
            Channel = channel,
            Amount = amount,
            Discount = Money.Zero,
            RefundedAmount = Money.Zero,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            Timeline = [new PaymentEvent(now, "created", method.ToString())],
        };
    }

    /// <summary>The provider has made a QR (or card session) for it.</summary>
    public void AttachProvider(string provider, string providerRef, string? qrContent, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRef);

        if (ProviderRef is not null && ProviderRef != providerRef)
        {
            throw new DomainException("payments.providerAlreadyAttached");
        }

        Provider = provider;
        ProviderRef = providerRef;
        QrContent = qrContent;
        Note(now, "providerAttached", provider);
    }

    /// <summary>
    /// The kiosk shows the "pay with the NeoPrint app" QR (plan section 2.9:
    /// <c>neoprint://pay/{paymentIntentId}</c>). The app picks the source when it pays.
    /// </summary>
    public void ShowAppQr(DateTime now)
    {
        if (Method != PaymentMethod.MobileApp)
        {
            throw new DomainException("payments.notAnAppPayment");
        }

        QrContent = $"neoprint://pay/{Id}";
        Note(now, "appQrShown");
    }

    /// <summary>The user of the mobile app took it over (plan section 9.2, step 4).</summary>
    public void AssignUser(Guid userId, DateTime now)
    {
        if (UserId is { } owner && owner != userId)
        {
            throw new DomainException("payments.belongsToAnotherUser");
        }

        UserId = userId;
    }

    /// <summary>
    /// The money was taken. Idempotent: the second confirmation of the same
    /// payment returns <c>false</c> and changes nothing.
    /// </summary>
    /// <exception cref="DomainException">The confirmed amount is not the amount asked for.</exception>
    public bool Succeed(PaymentParts parts, PaymentMethod method, DateTime paidAt, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (WasPaid)
        {
            Note(now, "confirmedAgain");
            return false;
        }

        if (parts.Total != Amount)
        {
            throw new DomainException(UserMessage.Of(
                "payments.amountMismatch", ("expected", Amount.Amount), ("actual", parts.Total.Amount)));
        }

        var late = Status == PaymentStatus.Expired;

        Move(PaymentStatus.Succeeded, actor, now);
        Parts = parts;
        Method = method;
        PaidAt = paidAt;
        Note(now, late ? "succeededAfterExpiry" : "succeeded", method.ToString());

        return true;
    }

    /// <summary>The provider refused it; the customer may try again with a new intent.</summary>
    public void Fail(string reasonKey, ActorContext actor, DateTime now)
    {
        if (Status == PaymentStatus.Failed)
        {
            return;
        }

        Move(PaymentStatus.Failed, actor.Because(reasonKey), now);
        FailureReason = new UserMessage(reasonKey).Encode();
        Note(now, "failed", reasonKey);
    }

    /// <summary>Nobody paid in time (<c>PaymentExpirationJob</c>).</summary>
    public void Expire(ActorContext actor, DateTime now)
    {
        if (Status == PaymentStatus.Expired)
        {
            return;
        }

        Move(PaymentStatus.Expired, actor, now);
        Note(now, "expired");
    }

    /// <summary>The provider's own status was checked (<c>PaymentReconciliationJob</c>).</summary>
    public void MarkReconciled(string outcome, DateTime now)
    {
        ReconciledAt = now;
        Note(now, "reconciled", outcome);
    }

    /// <summary>
    /// Money went back to the customer. The payment only reflects the refund;
    /// the refund itself is its own entity (plan section 2.5).
    /// </summary>
    /// <exception cref="DomainException">More would come back than was paid.</exception>
    public void ApplyRefund(Money amount, ActorContext actor, DateTime now)
    {
        if (!amount.IsPositive)
        {
            throw new DomainException("payments.refundNotPositive");
        }

        if (amount > Refundable)
        {
            throw new DomainException(UserMessage.Of(
                "payments.refundExceedsPayment", ("refundable", Refundable.Amount), ("asked", amount.Amount)));
        }

        RefundedAmount += amount;
        Move(Refundable.IsZero ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded, actor, now);
        Note(now, "refunded", amount.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A refund could not be completed after its retries (plan section 2.5).</summary>
    public void MarkRefundFailed(ActorContext actor, DateTime now)
    {
        Move(PaymentStatus.RefundFailed, actor, now);
        Note(now, "refundFailed");
    }

    /// <summary>An entry in the history without a status change.</summary>
    public void Note(DateTime at, string @event, string? details = null) => Timeline.Add(new PaymentEvent(at, @event, details));

    private void Move(PaymentStatus next, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, next, actor, now));
        Status = next;
    }
}
