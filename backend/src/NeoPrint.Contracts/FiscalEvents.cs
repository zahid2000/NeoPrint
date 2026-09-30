namespace NeoPrint.Contracts;

/// <summary>
/// The e-kassa issued a fiscal receipt (plan section 2.12): the payment's own
/// receipt is re-made with it and sent again to whoever it went to.
/// </summary>
/// <param name="Type">Sale, Refund or TopUp.</param>
public sealed record FiscalReceiptIssued(Guid FiscalReceiptId, string Type, Guid? PaymentId, Guid? RefundId, string? CorrelationId);
