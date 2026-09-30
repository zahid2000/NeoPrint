using System.Globalization;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Domain.Receipts;

/// <summary>
/// NeoPrint's own receipt for a payment (plan section 2.12: an information
/// receipt, not the fiscal one, which it carries once issued). One per payment:
/// its id <em>is</em> the payment's id, so a repeated event can never make a
/// second one and the kiosk can ask for it before it exists.
/// </summary>
/// <remarks>
/// The PDF is a rendering of data kept elsewhere - the payment, the job, the
/// fiscal receipt - so it is re-made when the fiscal receipt arrives and
/// whenever the stored copy cannot be read.
/// </remarks>
public sealed class Receipt : Entity
{
    public const int MaxEmails = 5;

    /// <summary><c>NP-2026-000001</c>: the year and a number that runs within it.</summary>
    public string Number { get; set; } = string.Empty;

    public Guid PaymentId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? KioskId { get; set; }

    public Guid? JobId { get; set; }

    /// <summary>The language it is written in: the account's, or the kiosk's default.</summary>
    public string Language { get; set; } = "az";

    /// <summary>The stored PDF; null until the first rendering.</summary>
    public FileObject? Pdf { get; private set; }

    /// <summary>Whether the rendering carries the issued fiscal receipt, or still "Fiskal çek hazırlanır".</summary>
    public bool FiscalShown { get; private set; }

    public DateTime? RenderedAt { get; private set; }

    /// <summary>Where it was sent; kept so the fiscal version goes to the same people. Personal data: never logged.</summary>
    public List<string> EmailedTo { get; set; } = [];

    public static string Format(int year, long sequence)
        => $"NP-{year.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString("000000", CultureInfo.InvariantCulture)}";

    public static Receipt Create(Guid paymentId, int year, long sequence, Guid? userId, Guid? kioskId, Guid? jobId, string language, DateTime now)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return new Receipt
        {
            Id = paymentId,
            PaymentId = paymentId,
            Number = Format(year, sequence),
            UserId = userId,
            KioskId = kioskId,
            JobId = jobId,
            Language = string.IsNullOrWhiteSpace(language) ? "az" : language,
            CreatedAt = now,
        };
    }

    public void Rendered(FileObject pdf, bool fiscalShown, DateTime now)
    {
        Pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
        FiscalShown = fiscalShown;
        RenderedAt = now;
    }

    /// <summary>Remembers an address it was sent to.</summary>
    /// <returns>False when it was already sent there.</returns>
    /// <exception cref="DomainException">Sent to too many addresses already.</exception>
    public bool AddEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalized = email.Trim().ToLowerInvariant();

        if (EmailedTo.Contains(normalized))
        {
            return false;
        }

        if (EmailedTo.Count >= MaxEmails)
        {
            throw new DomainException(UserMessage.Of("receipts.tooManyEmails", ("max", MaxEmails)));
        }

        EmailedTo.Add(normalized);
        return true;
    }
}
