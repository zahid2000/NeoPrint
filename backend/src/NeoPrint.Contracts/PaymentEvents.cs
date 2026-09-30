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
public sealed record PaymentSucceeded(
    Guid PaymentId,
    Guid JobId,
    Guid? KioskId,
    Guid? UserId,
    decimal Amount,
    string Method,
    bool JobPaid,
    string? CorrelationId);

/// <summary>An intent nobody paid in time.</summary>
public sealed record PaymentExpired(Guid PaymentId, Guid JobId, Guid? KioskId, string? CorrelationId);

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
public sealed record RefundRequested(
    Guid PaymentId,
    Guid? JobId,
    string Reason,
    decimal? Amount,
    string DedupKey,
    string? CorrelationId);
