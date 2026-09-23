namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Issues the JSON web tokens the four clients present (plan section 2.8).
/// </summary>
public interface ITokenService
{
    /// <summary>Access token for a mobile user. Valid for 15 minutes.</summary>
    IssuedToken CreateUserToken(Guid userId, string email);

    /// <summary>
    /// Access token for an administrator or operator, carrying the effective
    /// permissions and the kiosk scope. Valid for 15 minutes, which bounds how
    /// long a revoked permission can still be used.
    /// </summary>
    IssuedToken CreateAdminToken(
        Guid adminUserId,
        string email,
        IReadOnlyCollection<string> permissions,
        bool allKiosks,
        IReadOnlyCollection<Guid> kioskIds,
        bool mustChangePassword);

    /// <summary>
    /// Token for an activated kiosk device. Valid for one hour and renewed
    /// automatically (plan section 7.11).
    /// </summary>
    IssuedToken CreateKioskToken(Guid deviceId, Guid kioskId);

    /// <summary>
    /// Limited token for a device that is registered but not yet activated. It
    /// may only renew its pairing code and report printers.
    /// </summary>
    IssuedToken CreateProvisioningToken(Guid deviceId);

    /// <summary>
    /// Generates a refresh token. Only its hash is stored, and it is rotated on
    /// every use (plan section 2.8).
    /// </summary>
    string CreateRefreshToken();
}

/// <summary>An issued token and when it stops being accepted.</summary>
public sealed record IssuedToken(string Token, DateTime ExpiresAt);

/// <summary>Claim names used across the tokens.</summary>
public static class NeoPrintClaims
{
    /// <summary>Which kind of principal the token represents.</summary>
    public const string TokenType = "npt";

    public const string AdminUserId = "admin_id";
    public const string KioskId = "kiosk_id";
    public const string DeviceId = "device_id";
    public const string Permission = "perm";

    /// <summary>Present when the admin may act on every kiosk.</summary>
    public const string AllKiosks = "all_kiosks";

    /// <summary>Kiosk ids in scope when <see cref="AllKiosks"/> is absent.</summary>
    public const string ScopedKiosk = "kiosk_scope";

    /// <summary>Present while a password change is still outstanding.</summary>
    public const string MustChangePassword = "must_change_password";
}

/// <summary>The kinds of principal a token can represent.</summary>
public static class TokenTypes
{
    public const string User = "user";
    public const string Admin = "admin";
    public const string Kiosk = "kiosk";
    public const string Provisioning = "provisioning";
}
