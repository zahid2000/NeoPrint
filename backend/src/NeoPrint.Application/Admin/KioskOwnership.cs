namespace NeoPrint.Application.Admin;

/// <summary>The kinds of record a kiosk owns that an admin endpoint takes by id (T-301).</summary>
public enum RecordKind
{
    Payment,

    /// <summary>A refund has no kiosk of its own: its payment's.</summary>
    Refund,

    FiscalReceipt,

    Receipt,

    SupportClaim,
}

/// <summary>Whether a record exists, and the kiosk it belongs to (null: none - a top-up, an app order).</summary>
public sealed record KioskOwner(bool Found, Guid? KioskId);

/// <summary>
/// Which kiosk a record belongs to, so an account that looks after some kiosks
/// is shown and lets act on those kiosks' records only (T-301).
/// </summary>
public interface IKioskOwnershipReader
{
    Task<KioskOwner> KioskOfAsync(RecordKind kind, Guid id, CancellationToken cancellationToken = default);
}
