namespace NeoPrint.Contracts;

/// <summary>
/// The provider confirmed the money was taken (plan section 6). Fiscalization
/// and the receipt start from here.
/// </summary>
/// <param name="JobPaid">
/// False when the job could no longer be printed - it had expired, been
/// cancelled or paid by another payment. The money then goes back
/// (<see cref="RefundRequested"/>) instead of into the queue.
/// </param>
/// <param name="JobId">Null for a wallet top-up.</param>
public sealed record PaymentSucceeded(
    Guid PaymentId,
    Guid? JobId,
    Guid? KioskId,
    Guid? UserId,
    decimal Amount,
    string Method,
    bool JobPaid,
    string? CorrelationId,
    string Purpose = "PrintJob");

/// <summary>An intent nobody paid in time.</summary>
public sealed record PaymentExpired(Guid PaymentId, Guid? JobId, Guid? KioskId, string? CorrelationId);

/// <summary>
/// Money should go back to the customer (plan section 2.5). The refund
/// module creates the refund; this event carries only the decision.
/// </summary>
/// <param name="Reason">A <c>RefundReason</c> name: SystemError, UserCancel, AdminManual, PartialPrint, LatePayment, DuplicatePayment.</param>
/// <param name="Amount">Null for everything still refundable.</param>
/// <param name="DedupKey">
/// The same decision made twice (a duplicate event, a retried consumer) has
/// the same key and makes one refund.
/// </param>
/// <param name="Destination">
/// A <c>RefundDestination</c> name chosen by an admin for the payment's
/// external part; null for the default matrix (plan section 2.5).
/// </param>
/// <param name="Evidence">For a partly printed job: which source decided the amount.</param>
public sealed record RefundRequested(
    Guid PaymentId,
    Guid? JobId,
    string Reason,
    decimal? Amount,
    string DedupKey,
    string? CorrelationId,
    string? Destination = null,
    string? Note = null,
    Guid? InitiatorId = null,
    string InitiatedBy = "System",
    RefundEvidenceData? Evidence = null);

/// <summary>What decided the amount of a partly printed job's refund.</summary>
public sealed record RefundEvidenceData(string Source, int PrintedSides, int OrderedSides, string Detail);
