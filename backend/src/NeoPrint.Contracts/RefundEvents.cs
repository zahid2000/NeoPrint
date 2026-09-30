namespace NeoPrint.Contracts;

/// <summary>Money went back (plan section 6: <c>RefundCompleted</c>). The fiscal refund receipt starts here.</summary>
public sealed record RefundCompleted(
    Guid RefundId, Guid PaymentId, Guid? JobId, Guid? UserId, decimal Amount, int BonusPages, string Destination, string? CorrelationId);

/// <summary>A refund could not be completed after its retries; an admin decides (plan section 2.5).</summary>
public sealed record RefundFailed(Guid RefundId, Guid PaymentId, Guid? JobId, string Destination, string? CorrelationId);
