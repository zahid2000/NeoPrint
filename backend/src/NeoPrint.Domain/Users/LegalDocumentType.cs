namespace NeoPrint.Domain.Users;

/// <summary>
/// The documents a user consents to (plan sections 2.8, 2.13).
/// </summary>
public enum LegalDocumentType
{
    /// <summary>Privacy policy, accepted at registration.</summary>
    Privacy = 0,

    /// <summary>Terms of use, including the clause that the wallet is not refundable.</summary>
    Terms = 1,

    /// <summary>One-off notice shown before the first document scan.</summary>
    ScanNotice = 2,
}
