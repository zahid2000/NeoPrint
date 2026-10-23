using NeoPrint.Domain.Fiscal;

namespace NeoPrint.Application.Fiscal;

/// <summary>
/// The e-kassa (plan section 2.12). One implementation today, the mock; a
/// real adapter is chosen with <c>NEOPRINT_FISCAL_PROVIDER</c> once a
/// provider is contracted (plan section 14).
/// </summary>
/// <remarks>
/// Every request carries <see cref="FiscalRequest.Key"/> - the receipt's id -
/// and the provider must answer a repeated key with the document it already
/// made: a try repeated after a lost answer never makes a second receipt.
/// </remarks>
public interface IFiscalProvider
{
    string Name { get; }

    Task<FiscalProviderResult> IssueSaleAsync(FiscalRequest request, CancellationToken cancellationToken = default);

    /// <param name="request">Its <see cref="FiscalRequest.OriginalFiscalId"/> names the sale being refunded.</param>
    Task<FiscalProviderResult> IssueRefundAsync(FiscalRequest request, CancellationToken cancellationToken = default);

    Task<FiscalProviderResult> GetStatusAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}

/// <param name="Type">Sale and TopUp go to <see cref="IFiscalProvider.IssueSaleAsync"/> (a top-up is an advance sale).</param>
public sealed record FiscalRequest(
    string Key,
    FiscalReceiptType Type,
    string TerminalId,
    decimal Amount,
    string Currency,
    IReadOnlyList<FiscalLine> Lines,
    IReadOnlyList<FiscalPaymentPart> PaidWith,
    string? OriginalFiscalId = null);

public enum FiscalOutcome
{
    Issued = 0,

    /// <summary>Refused for good (bad terminal, bad data): retrying the same request will not help.</summary>
    Rejected = 1,

    /// <summary>Down, timed out, or not sure: tried again later with the same key.</summary>
    Unavailable = 2,
}

public sealed record FiscalProviderResult(
    FiscalOutcome Outcome,
    string? DocumentNumber = null,
    string? FiscalId = null,
    string? VerificationUrl = null,
    string? FailureCode = null);

/// <summary>The <c>fiscal_receipts</c> collection.</summary>
public interface IFiscalReceiptRepository
{
    /// <returns>False when a receipt with the same dedup key exists: the same sale or refund again.</returns>
    Task<bool> TryAddAsync(FiscalReceipt receipt, CancellationToken cancellationToken = default);

    Task<FiscalReceipt?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FiscalReceipt?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken = default);

    Task UpdateAsync(FiscalReceipt receipt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FiscalReceipt>> ListForPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>Pending receipts whose next try is due.</summary>
    Task<IReadOnlyList<FiscalReceipt>> ListDueAsync(DateTime now, int take, CancellationToken cancellationToken = default);

    /// <summary>Pending receipts made before <paramref name="createdBefore"/>: late enough for an incident.</summary>
    Task<IReadOnlyList<FiscalReceipt>> ListPendingSinceAsync(DateTime createdBefore, int take, CancellationToken cancellationToken = default);

    /// <param name="kiosks">Null: every receipt. Otherwise only those of these kiosks (T-301).</param>
    Task<(IReadOnlyList<FiscalReceipt> Items, long Total)> SearchAsync(
        IReadOnlyCollection<FiscalReceiptStatus>? statuses,
        IReadOnlyCollection<FiscalReceiptType>? types,
        Guid? paymentId,
        int skip,
        int take,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? kiosks = null);

    Task<long> CountAsync(FiscalReceiptStatus status, CancellationToken cancellationToken = default);
}
