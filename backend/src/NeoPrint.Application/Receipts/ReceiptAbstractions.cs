using NeoPrint.Domain.Receipts;

namespace NeoPrint.Application.Receipts;

/// <summary>The <c>receipts</c> collection (plan section 5).</summary>
public interface IReceiptRepository
{
    Task<Receipt?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <returns>False when the payment already has its receipt.</returns>
    Task<bool> TryAddAsync(Receipt receipt, CancellationToken cancellationToken = default);

    Task UpdateAsync(Receipt receipt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Receipt>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default);

    /// <summary>Receipts still rendered without their fiscal receipt, made after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<Receipt>> ListWithoutFiscalAsync(DateTime since, int take, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Receipt> Items, long Total)> SearchAsync(string? number, Guid? paymentId, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>The receipt number: a gapless count per year (plan section 5, <c>NP-2026-000001</c>).</summary>
public interface IReceiptNumbers
{
    /// <summary>The next number of <paramref name="year"/>; inside the caller's transaction, so an aborted one gives it back.</summary>
    Task<long> NextAsync(int year, CancellationToken cancellationToken = default);
}

/// <summary>Turns a receipt's content into a PDF (QuestPDF in Infrastructure).</summary>
public interface IReceiptRenderer
{
    byte[] Render(ReceiptContent content);
}

/// <summary>Everything the PDF shows, already in the receipt's language.</summary>
/// <param name="Labels">The texts, by key, from the message catalogs.</param>
/// <param name="QrContent">What the QR encodes: the fiscal verification link once issued.</param>
public sealed record ReceiptContent(
    string Language,
    string Number,
    DateTime PaidAt,
    string? KioskCode,
    string? KioskAddress,
    string? JobCode,
    string? FileName,
    string? PrintSummary,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Total,
    string Currency,
    IReadOnlyList<ReceiptLine> PaidWith,
    int BonusPages,
    ReceiptFiscal? Fiscal,
    string? QrContent,
    IReadOnlyDictionary<string, string> Labels);

public sealed record ReceiptLine(string Text, decimal Amount);

/// <param name="Issued">False: the PDF says the fiscal receipt is being prepared.</param>
public sealed record ReceiptFiscal(bool Issued, string? DocumentNumber, string? FiscalId, string? VerificationUrl, string? TerminalId);
