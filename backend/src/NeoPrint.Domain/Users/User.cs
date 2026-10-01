using System.Security.Cryptography;

using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Users;

/// <summary>
/// A registered mobile user (plan section 5, <c>users</c>).
/// </summary>
public sealed class User : Entity
{
    /// <summary>
    /// Human-shareable identifier, <c>NP-XXXXXX</c>. Used for file sharing, so
    /// it must be short enough to read out and type in.
    /// </summary>
    public string PublicId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>Hashed by the ASP.NET Core Identity password hasher (plan section 2.8).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string? FullName { get; set; }

    /// <summary>Referral code of this user, <c>NP</c> plus six characters (plan section 2.6).</summary>
    public string ReferralCode { get; set; } = string.Empty;

    /// <summary>Who invited this user, if anyone.</summary>
    public Guid? ReferredBy { get; set; }

    public bool EmailConfirmed { get; set; }

    /// <summary>
    /// Devices this account has been used from, for the self-referral and
    /// repeat-referral check (plan section 2.6).
    /// </summary>
    public List<string> DeviceIds { get; set; } = [];

    public List<string> FcmTokens { get; set; } = [];

    /// <summary>UI language: az (default), en or ru.</summary>
    public string Language { get; set; } = "az";

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Characters used for generated identifiers: no 0/O or 1/I/L, so a code
    /// read off a screen cannot be mistyped (plan section 7.11).
    /// </summary>
    private const string UnambiguousAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Generates a public id of the form <c>NP-XXXXXX</c>.</summary>
    public static string NewPublicId() => "NP-" + RandomCode(6);

    /// <summary>Generates a referral code of the form <c>NPXXXXXX</c> (plan section 2.6).</summary>
    public static string NewReferralCode() => "NP" + RandomCode(6);

    /// <summary>A cryptographically random code from the unambiguous alphabet.</summary>
    public static string RandomCode(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, 0);

        return string.Create(length, length, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = UnambiguousAlphabet[RandomNumberGenerator.GetInt32(UnambiguousAlphabet.Length)];
            }
        });
    }

    /// <summary>
    /// Emails are compared and stored in lower case, so that the same address
    /// cannot be registered twice in different casings.
    /// </summary>
    public static string NormalizeEmail(string email)
        => email.Trim().ToLowerInvariant();

    /// <summary>Whether this account may sign in and place orders.</summary>
    public bool CanSignIn => Status == UserStatus.Active;

    /// <summary>When an admin blocked the account; null while it is not blocked.</summary>
    public DateTime? BlockedAt { get; set; }

    /// <summary>
    /// An admin stops the account (plan section 8A.3, "İstifadəçilər"). The
    /// wallet is kept as it is - blocking takes no money - and unblocking
    /// gives everything back.
    /// </summary>
    /// <returns>False when it was already blocked: nothing changed.</returns>
    /// <exception cref="DomainException">A deleted account cannot be blocked.</exception>
    public bool Block(DateTime now)
    {
        switch (Status)
        {
            case UserStatus.Blocked:
                return false;
            case UserStatus.Deleted:
                throw new DomainException("users.deleted");
        }

        Status = UserStatus.Blocked;
        BlockedAt = now;
        return true;
    }

    /// <returns>False when it was not blocked: nothing changed.</returns>
    public bool Unblock()
    {
        if (Status != UserStatus.Blocked)
        {
            return false;
        }

        Status = UserStatus.Active;
        BlockedAt = null;
        return true;
    }
}

/// <summary>
/// An email confirmation code (plan section 2.8: six digits).
/// </summary>
public sealed class EmailConfirmation : Entity
{
    public Guid UserId { get; set; }

    /// <summary>Hashed, so that a database dump does not hand out valid codes.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public int Attempts { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    /// <summary>Generates a six-digit code.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}

/// <summary>
/// A refresh token, stored hashed and rotated on every use (plan section 2.8).
/// </summary>
public sealed class RefreshToken : Entity
{
    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public string? DeviceId { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// The token that replaced this one. A request presenting an already
    /// rotated token is a sign of theft, and revokes the whole chain.
    /// </summary>
    public Guid? ReplacedBy { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt is null && ExpiresAt > utcNow;
}

/// <summary>
/// A recorded consent (plan section 2.13, <c>consents</c>).
/// </summary>
public sealed class Consent : Entity
{
    public Guid UserId { get; set; }

    public LegalDocumentType DocType { get; set; }

    public string Version { get; set; } = string.Empty;

    public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;

    public string? Ip { get; set; }

    public string? DeviceId { get; set; }
}

/// <summary>
/// A published version of a legal document (plan section 2.13,
/// <c>legal_documents</c>).
/// </summary>
/// <remarks>
/// Documents are versioned: when a new version is published, users accept it
/// again at their next sign-in.
/// </remarks>
public sealed class LegalDocument : Entity
{
    public LegalDocumentType DocType { get; set; }

    public string Version { get; set; } = string.Empty;

    /// <summary>az, en or ru.</summary>
    public string Language { get; set; } = "az";

    /// <summary>Markdown.</summary>
    public string Content { get; set; } = string.Empty;

    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;

    public Guid? PublishedBy { get; set; }
}
