using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Application.Admin;

// ---------------------------------------------------------------------------
// Repositories
// ---------------------------------------------------------------------------

/// <summary>Persistence for administrator accounts.</summary>
public interface IAdminUserRepository
{
    Task<AdminUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AdminUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How many active accounts hold a role.
    /// </summary>
    /// <remarks>
    /// Used to keep at least one active SuperAdmin, and as the holder count the
    /// panel shows beside a role (plan section 8A.2).
    /// </remarks>
    Task<long> CountActiveWithRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many accounts hold a role, whether they are active or not.
    /// </summary>
    /// <remarks>
    /// What deletion is refused on: a deactivated account keeps its roles, and
    /// deleting one out from under it would leave it pointing at a role that no
    /// longer exists - which only surfaces when someone activates the account
    /// again (plan section 8A.2).
    /// </remarks>
    Task<long> CountWithRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task AddAsync(AdminUser admin, CancellationToken cancellationToken = default);

    Task UpdateAsync(AdminUser admin, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for roles.</summary>
public interface IAdminRoleRepository
{
    Task<AdminRole?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AdminRole?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminRole>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminRole>> FindByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task AddAsync(AdminRole role, CancellationToken cancellationToken = default);

    Task UpdateAsync(AdminRole role, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The effective permissions of an administrator, cached.
/// </summary>
/// <remarks>
/// Resolving permissions means reading the account and each of its roles, on
/// every request. Plan section 8A.2 caches that for five minutes in Redis and
/// clears the entry when a role or an assignment changes, so a revoked
/// permission stops working without waiting for the cache to expire.
/// </remarks>
public interface IPermissionResolver
{
    Task<EffectivePermissions> ResolveAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached permissions of one administrator.</summary>
    Task InvalidateAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached permissions of everyone holding a role.</summary>
    Task InvalidateRoleAsync(Guid roleId, CancellationToken cancellationToken = default);
}

/// <param name="Permissions">The union of the permissions of every role held.</param>
/// <param name="AllKiosks">Whether the account may act on every kiosk.</param>
/// <param name="KioskIds">The kiosks in scope when <paramref name="AllKiosks"/> is false.</param>
/// <param name="IsActive">
/// Whether the account may act at all. False for an account that has been
/// deactivated or deleted, which is not the same as one whose role happens to
/// grant nothing: the first must be refused outright, the second sees an empty
/// menu (plan section 8A.2).
/// </param>
public sealed record EffectivePermissions(
    IReadOnlyCollection<string> Permissions,
    bool AllKiosks,
    IReadOnlyCollection<Guid> KioskIds,
    bool IsActive = true)
{
    public bool Has(string permission) => Permissions.Contains(permission);

    public bool CanSee(Guid kioskId) => AllKiosks || KioskIds.Contains(kioskId);
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

/// <summary>What the admin panel gets back after signing in.</summary>
public sealed record AdminAuthResult(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    AdminSummary Admin);

public sealed record AdminSummary(
    Guid Id,
    string Email,
    string FullName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    bool AllKiosks,
    IReadOnlyCollection<Guid> KioskIds,
    bool MustChangePassword);

/// <summary>Signs an administrator in (plan section 2.8).</summary>
public sealed record AdminLoginCommand(string Email, string Password, string? Ip) : IRequest<AdminAuthResult>;

/// <summary>Exchanges an admin refresh token for a new pair.</summary>
public sealed record AdminRefreshCommand(string RefreshToken, string? Ip) : IRequest<AdminAuthResult>;

/// <summary>Revokes an admin refresh token.</summary>
public sealed record AdminLogoutCommand(string RefreshToken) : IRequest<Unit>;

/// <summary>
/// Changes an administrator's own password.
/// </summary>
/// <remarks>
/// Mandatory at first sign-in for the seeded SuperAdmin, whose password came
/// from a configuration value (plan section 2.8).
/// </remarks>
public sealed record ChangeAdminPasswordCommand(
    Guid AdminUserId, string CurrentPassword, string NewPassword, string? Ip)
    : IRequest<Unit>, Behaviors.ITransactionalRequest;

// ---------------------------------------------------------------------------
// Validators
// ---------------------------------------------------------------------------

public sealed class AdminLoginCommandValidator : IValidator<AdminLoginCommand>
{
    public IEnumerable<ValidationError> Validate(AdminLoginCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            yield return new ValidationError("email", "auth.emailRequired");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            yield return new ValidationError("password", "auth.passwordRequired");
        }
    }
}

public sealed class ChangeAdminPasswordCommandValidator : IValidator<ChangeAdminPasswordCommand>
{
    /// <summary>
    /// Longer than the user minimum: an admin account can refund money and
    /// reconfigure kiosks.
    /// </summary>
    public const int MinimumLength = 12;

    public IEnumerable<ValidationError> Validate(ChangeAdminPasswordCommand request)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            yield return new ValidationError("currentPassword", "adminAuth.currentPasswordRequired");
        }

        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < MinimumLength)
        {
            yield return new ValidationError(
                "newPassword", "adminAuth.newPasswordTooShort", ("min", MinimumLength));
            yield break;
        }

        if (request.NewPassword.Length > 128)
        {
            yield return new ValidationError("newPassword", "auth.passwordTooLong");
        }

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            yield return new ValidationError("newPassword", "adminAuth.newPasswordSame");
        }
    }
}
