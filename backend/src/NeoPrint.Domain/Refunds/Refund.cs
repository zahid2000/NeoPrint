using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Refunds;

/// <summary>Where the money goes back to (plan section 2.5, refund model).</summary>
public enum RefundDestination
{
    /// <summary>The card or bank account that paid (a provider refund).</summary>
    OriginalMethod = 0,

    Wallet = 1,

    /// <summary>A code worth the amount, only when a bank refund to an anonymous customer is impossible.</summary>
    Voucher = 2,

    /// <summary>Bonus pages back to the bonus balance.</summary>
    Bonus = 3,
}

/// <summary>
/// Which part of the payment it gives back (plan section 2.5: "hər hissə öz
/// mənbəyinə qaytarılır"): the bank or card part, the wallet part, the bonus pages.
/// </summary>
public enum RefundSource
{
    External = 0,
    Wallet = 1,
    Bonus = 2,
}

/// <summary>Why (plan section 2.5, <c>Reason</c>; the last two from docs/DECISIONS.md 2026-09-30).</summary>
public enum RefundReason
{
    SystemError = 0,
    UserCancel = 1,
    ScheduleCancel = 2,
    AdminManual = 3,
    PartialPrint = 4,

    /// <summary>The bank confirmed after the job could no longer be printed.</summary>
    LatePayment = 5,

    /// <summary>A second payment for a job already paid.</summary>
    DuplicatePayment = 6,

    /// <summary>Paid in the app for later and not collected at the kiosk in time (plan section 9.4).</summary>
    NotCollected = 7,
}

public enum RefundStatus
{
    Pending = 0,
    Processing = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>What decided how much of a partly printed job comes back (the owner's rule, 2026-09-30).</summary>
public enum EvidenceSource
{
    /// <summary>The printer's own page counter before and after.</summary>
    PrinterCounter = 0,

    /// <summary>The operating system's print spooler said the job finished.</summary>
    Spooler = 1,

    /// <summary>What the kiosk reported printing.</summary>
    KioskJournal = 2,

    /// <summary>Nothing reliable: the full amount comes back.</summary>
    None = 3,
}

/// <param name="PrintedSides">What the chosen source says was printed.</param>
/// <param name="Detail">Every source that had something to say, for the admin panel.</param>
public sealed record RefundEvidence(EvidenceSource Source, int PrintedSides, int OrderedSides, string Detail);

/// <summary>
/// Money going back to a customer (plan section 2.5; docs/STATE_MACHINES.md
/// "Refund"). One payment can have several; together they never exceed it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DedupKey"/> is unique: the same decision reaching the refund
/// module twice - a redelivered event, a retried consumer, two admins pressing
/// the button - makes one refund (docs/IDEMPOTENCY.md: no double refund).
/// </para>
/// <para>
/// A provider refund is tried <c>Refund.ProviderMaxAttempts</c> times with
/// growing gaps; after that it is <see cref="RefundStatus.Failed"/> and waits
/// for an admin, who may retry or send it elsewhere (a wallet, a voucher).
/// </para>
/// </remarks>
public sealed class Refund : Entity
{
    public Guid PaymentId { get; set; }

    public Guid? JobId { get; set; }

    public Guid? UserId { get; set; }

    public Money Amount { get; set; }

    public int BonusPages { get; set; }

    public RefundSource Source { get; set; }

    public RefundDestination Destination { get; private set; }

    public RefundReason Reason { get; set; }

    public ActorType InitiatedBy { get; set; }

    public Guid? InitiatorId { get; set; }

    public RefundStatus Status { get; private set; } = RefundStatus.Pending;

    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The provider's reference for the refund.</summary>
    public string? ProviderRefundRef { get; private set; }

    public Guid? FiscalRefundReceiptId { get; set; }

    /// <summary>Provider tries so far (for the current destination).</summary>
    public int Attempts { get; private set; }

    /// <summary>When the next provider try is due; null when none is scheduled.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>The last failure, as a stored message key; never a provider secret.</summary>
    public string? LastError { get; private set; }

    /// <summary>The admin's note or the system's reason, as a stored message.</summary>
    public string? Note { get; set; }

    /// <summary>For a partly printed job: which evidence decided the amount.</summary>
    public RefundEvidence? Evidence { get; set; }

    /// <summary>The voucher made for a <see cref="RefundDestination.Voucher"/> refund.</summary>
    public string? VoucherCode { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public static readonly StateMachine<RefundStatus> Transitions =
        new StateMachine<RefundStatus>(nameof(Refund))
            .Allow(RefundStatus.Pending, RefundStatus.Processing)
            .Allow(RefundStatus.Processing, RefundStatus.Succeeded, RefundStatus.Failed)
            .Allow(RefundStatus.Failed, RefundStatus.Processing);

    public static Refund Create(
        Guid paymentId,
        Guid? jobId,
        Guid? userId,
        Money amount,
        int bonusPages,
        RefundSource source,
        RefundDestination destination,
        RefundReason reason,
        ActorType initiatedBy,
        Guid? initiatorId,
        string dedupKey,
        DateTime now,
        string? note = null,
        RefundEvidence? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

        if (amount.IsNegative || bonusPages < 0 || amount.IsZero && bonusPages == 0)
        {
            throw new DomainException("refunds.nothingToRefund");
        }

        if (destination == RefundDestination.Bonus != (bonusPages > 0 && amount.IsZero))
        {
            // Bonus pages go back as pages and only as pages; money never goes to the bonus balance.
            throw new DomainException("refunds.bonusOnlyAsPages");
        }

        if ((source == RefundSource.Bonus) != (destination == RefundDestination.Bonus)
            || source == RefundSource.Wallet && destination != RefundDestination.Wallet)
        {
            // Bonus pages back as pages; the wallet part back to the wallet, never out (plan 2.14).
            throw new DomainException("refunds.walletStaysInWallet");
        }

        return new Refund
        {
            PaymentId = paymentId,
            JobId = jobId,
            UserId = userId,
            Amount = amount,
            BonusPages = bonusPages,
            Source = source,
            Destination = destination,
            Reason = reason,
            InitiatedBy = initiatedBy,
            InitiatorId = initiatorId,
            DedupKey = dedupKey,
            Note = note,
            Evidence = evidence,
            CreatedAt = now,
        };
    }

    /// <summary>Work on it has started (the provider is being asked, or the wallet credited).</summary>
    public void Start(ActorContext actor, DateTime now)
    {
        Move(RefundStatus.Processing, actor, now);
        NextAttemptAt = null;
    }

    /// <summary>A provider try; counted before it is made, so a crash mid-call still counts.</summary>
    public void CountAttempt() => Attempts++;

    /// <summary>The money is back.</summary>
    public void Succeed(string? providerRefundRef, ActorContext actor, DateTime now, string? voucherCode = null)
    {
        Move(RefundStatus.Succeeded, actor, now);
        AwaitingProvider = false;
        ProviderRefundRef = providerRefundRef ?? ProviderRefundRef;
        VoucherCode = voucherCode ?? VoucherCode;
        LastError = null;
        CompletedAt = now;
    }

    /// <summary>The provider took the refund but has not finished it; <c>RefundStatusPollingJob</c> asks later.</summary>
    public void AwaitProvider(string? providerRefundRef)
    {
        ProviderRefundRef = providerRefundRef ?? ProviderRefundRef;
        NextAttemptAt = null;
        AwaitingProvider = true;
    }

    /// <summary>Whether the provider is still working on it.</summary>
    public bool AwaitingProvider { get; private set; }

    /// <summary>A try failed; another comes at <paramref name="retryAt"/>, or none and an admin decides.</summary>
    public void Fail(string errorKey, DateTime? retryAt, ActorContext actor, DateTime now)
    {
        LastError = errorKey;
        AwaitingProvider = false;

        if (retryAt is { } at)
        {
            // Still ours to retry: stays Processing, waiting for its next try.
            NextAttemptAt = at;
            return;
        }

        Move(RefundStatus.Failed, actor, now);
        NextAttemptAt = null;
    }

    /// <summary>An admin tries again, the same way or another (plan section 2.5).</summary>
    /// <exception cref="DomainException">Only a failed refund can be retried or redirected.</exception>
    public void Retry(RefundDestination destination, ActorContext actor, DateTime now)
    {
        if (Status != RefundStatus.Failed)
        {
            throw new DomainException("refunds.notFailed");
        }

        if (destination == RefundDestination.Bonus || Source == RefundSource.Bonus)
        {
            throw new DomainException("refunds.bonusOnlyAsPages");
        }

        // The wallet part only ever goes back to the wallet (plan section 2.14).
        if (Source == RefundSource.Wallet && destination != RefundDestination.Wallet)
        {
            throw new DomainException("refunds.walletStaysInWallet");
        }

        // A fresh set of provider tries, the same way or another.
        Destination = destination;
        Attempts = 0;

        Move(RefundStatus.Processing, actor, now);

        // Due at once: the retry job picks it up if nothing else does first.
        NextAttemptAt = now;
    }

    private void Move(RefundStatus next, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, next, actor, now));
        Status = next;
    }
}
