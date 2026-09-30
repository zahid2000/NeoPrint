using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Fiscal;

/// <summary>What the fiscal receipt records (plan section 2.12).</summary>
public enum FiscalReceiptType
{
    /// <summary>A print paid for: by card, bank QR or the wallet.</summary>
    Sale = 0,

    /// <summary>Money given back, referring to the sale's fiscal document.</summary>
    Refund = 1,

    /// <summary>Money put on a wallet in advance (<c>Fiscal.TopUpReceiptMode = OnTopUp</c>).</summary>
    TopUp = 2,
}

public enum FiscalReceiptStatus
{
    /// <summary>Not issued yet: waiting for its first try or its next one.</summary>
    Pending = 0,

    Issued = 1,

    /// <summary>The provider refused it for good, or an admin must look; retried only from the panel.</summary>
    Failed = 2,
}

/// <summary>One line of the receipt: the service and its price. Bonus pages are not on it (0 AZN).</summary>
public sealed record FiscalLine(string Name, int Quantity, Money UnitPrice, Money Total);

/// <summary>How the receipt's total was paid: <c>Card</c>, <c>BankQr</c>, <c>Advance</c> (a wallet paid in advance).</summary>
public sealed record FiscalPaymentPart(string Kind, Money Amount);

/// <summary>
/// A fiscal receipt (plan section 2.12, <c>fiscal_receipts</c>). Issued
/// asynchronously; the payment never waits for it.
/// </summary>
/// <remarks>
/// <see cref="DedupKey"/> is unique - <c>sale:{paymentId}</c>,
/// <c>refund:{refundId}</c>, <c>topup:{paymentId}</c> - so a repeated event
/// or a retried consumer never makes a second receipt (no double fiscal).
/// The receipt's id is also the key the provider is asked with, so a try
/// repeated after a lost answer returns the document it already made.
/// </remarks>
public sealed class FiscalReceipt : Entity
{
    public FiscalReceiptType Type { get; set; }

    public string DedupKey { get; set; } = string.Empty;

    public Guid? PaymentId { get; set; }

    public Guid? RefundId { get; set; }

    public Guid? KioskId { get; set; }

    /// <summary>The kiosk's fiscal terminal, or the online one for app sales; null until one is set up.</summary>
    public string? TerminalId { get; set; }

    public Money Amount { get; set; }

    public List<FiscalLine> Lines { get; set; } = [];

    public List<FiscalPaymentPart> PaidWith { get; set; } = [];

    /// <summary>For a refund receipt: the sale's fiscal id it refers to, filled once the sale is issued.</summary>
    public string? OriginalFiscalId { get; set; }

    public FiscalReceiptStatus Status { get; private set; } = FiscalReceiptStatus.Pending;

    public string? FiscalDocumentNumber { get; private set; }

    public string? FiscalId { get; private set; }

    public string? VerificationUrl { get; private set; }

    public DateTime? IssuedAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When the next try is due; null while none is scheduled.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>The last failure, as a message key; never a provider secret.</summary>
    public string? LastError { get; private set; }

    public static readonly StateMachine<FiscalReceiptStatus> Transitions =
        new StateMachine<FiscalReceiptStatus>(nameof(FiscalReceipt))
            .Allow(FiscalReceiptStatus.Pending, FiscalReceiptStatus.Issued, FiscalReceiptStatus.Failed)
            .Allow(FiscalReceiptStatus.Failed, FiscalReceiptStatus.Pending);

    public static FiscalReceipt Create(
        FiscalReceiptType type,
        string dedupKey,
        Money amount,
        IReadOnlyList<FiscalLine> lines,
        IReadOnlyList<FiscalPaymentPart> paidWith,
        DateTime now,
        Guid? paymentId = null,
        Guid? refundId = null,
        Guid? kioskId = null,
        string? terminalId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(paidWith);

        if (!amount.IsPositive)
        {
            // Nothing paid in money - all bonus pages - is not fiscalized (plan section 2.12).
            throw new DomainException("fiscal.nothingToFiscalize");
        }

        return new FiscalReceipt
        {
            Type = type,
            DedupKey = dedupKey,
            Amount = amount,
            Lines = [.. lines],
            PaidWith = [.. paidWith],
            PaymentId = paymentId,
            RefundId = refundId,
            KioskId = kioskId,
            TerminalId = string.IsNullOrWhiteSpace(terminalId) ? null : terminalId.Trim(),
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>A provider try; counted before it is made, so a crash mid-call still counts.</summary>
    public void CountAttempt(DateTime now)
    {
        if (Status != FiscalReceiptStatus.Pending)
        {
            throw new DomainException("fiscal.notPending");
        }

        Attempts++;

        // Held back while the call is in flight: a parallel run does not ask twice.
        NextAttemptAt = now.AddMinutes(5);
    }

    public void Issue(string documentNumber, string fiscalId, string? verificationUrl, ActorContext actor, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(fiscalId);

        if (Status == FiscalReceiptStatus.Issued)
        {
            return;
        }

        Move(FiscalReceiptStatus.Issued, actor, now);
        FiscalDocumentNumber = documentNumber;
        FiscalId = fiscalId;
        VerificationUrl = verificationUrl;
        IssuedAt = now;
        NextAttemptAt = null;
        LastError = null;
    }

    /// <summary>
    /// A try did not work. A passing failure waits until <paramref name="retryAt"/>;
    /// with no retry time the receipt is Failed and an admin retries it.
    /// </summary>
    public void Fail(string errorKey, DateTime? retryAt, ActorContext actor, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorKey);

        LastError = errorKey;

        if (retryAt is { } next)
        {
            NextAttemptAt = next;
            return;
        }

        Move(FiscalReceiptStatus.Failed, actor, now);
        NextAttemptAt = null;
    }

    /// <summary>"Təkrarla" in the panel: due at once, with a fresh count.</summary>
    public void Retry(ActorContext actor, DateTime now)
    {
        if (Status == FiscalReceiptStatus.Issued)
        {
            throw new DomainException("fiscal.alreadyIssued");
        }

        if (Status == FiscalReceiptStatus.Failed)
        {
            Move(FiscalReceiptStatus.Pending, actor, now);
        }

        Attempts = 0;
        NextAttemptAt = now;
    }

    private void Move(FiscalReceiptStatus next, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, next, actor, now));
        Status = next;
    }
}
