using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;

namespace NeoPrint.Application.Admin;

/// <summary>
/// An administrator account as the panel lists it (plan section 8A.3).
/// </summary>
/// <remarks>
/// No password hash and no lockout counter: neither is any use to the panel,
/// and a hash has no business leaving the server.
/// </remarks>
public sealed record AdminUserResponse(
    Guid Id,
    string Email,
    string FullName,
    IReadOnlyCollection<Guid> RoleIds,
    IReadOnlyCollection<string> RoleNames,
    bool AllKiosks,
    IReadOnlyCollection<Guid> KioskIds,
    bool IsActive,
    bool MustChangePassword,
    bool IsLockedOut,
    DateTime? LastLoginAt,
    DateTime CreatedAt);

public sealed record ListAdminUsersQuery : IRequest<IReadOnlyList<AdminUserResponse>>;

public sealed record GetAdminUserQuery(Guid AdminUserId) : IRequest<AdminUserResponse?>;

/// <summary>
/// Creates an account (plan section 8A.3).
/// </summary>
/// <remarks>
/// The password is set here and the account must change it at first sign-in,
/// so whoever created it does not keep a password somebody else is using.
/// </remarks>
public sealed record CreateAdminUserCommand(
    string Email,
    string FullName,
    string Password,
    IReadOnlyCollection<Guid> RoleIds,
    bool AllKiosks,
    IReadOnlyCollection<Guid> KioskIds,
    Guid? CreatedBy,
    string? Ip) : IRequest<AdminUserResponse>, ITransactionalRequest;

public sealed record UpdateAdminUserCommand(
    Guid AdminUserId,
    string FullName,
    IReadOnlyCollection<Guid> RoleIds,
    bool AllKiosks,
    IReadOnlyCollection<Guid> KioskIds,
    Guid? UpdatedBy,
    string? Ip) : IRequest<AdminUserResponse?>, ITransactionalRequest;

/// <summary>Activates or deactivates an account.</summary>
/// <remarks>
/// Deactivating rather than deleting: an account that did things stays
/// referenced by its audit entries, and an audit trail pointing at nothing is
/// worse than one pointing at a disabled account
/// (<c>docs/DATA_RETENTION.md</c>).
/// </remarks>
public sealed record SetAdminUserActiveCommand(
    Guid AdminUserId,
    bool IsActive,
    string? Reason,
    Guid? ChangedBy,
    string? Ip) : IRequest<AdminUserResult>, ITransactionalRequest;

/// <summary>
/// Resets somebody else's password.
/// </summary>
/// <remarks>
/// The new password is set by the administrator doing the reset and has to be
/// changed at the next sign-in. Every session of the account is revoked, so a
/// stolen session cannot outlive the reset.
/// </remarks>
public sealed record ResetAdminPasswordCommand(
    Guid AdminUserId,
    string NewPassword,
    Guid? ChangedBy,
    string? Ip) : IRequest<AdminUserResult>, ITransactionalRequest;

/// <summary>What happened to the account.</summary>
public enum AdminUserResult
{
    Changed = 0,
    NotFound = 1,
}

/// <summary>
/// Checks a new account before it is created.
/// </summary>
/// <remarks>
/// The password rule matches the one for changing a password: an admin account
/// can refund money and reconfigure kiosks, so twelve characters is the floor
/// (plan section 2.8).
/// </remarks>
public sealed class CreateAdminUserCommandValidator : IValidator<CreateAdminUserCommand>
{
    public IEnumerable<ValidationError> Validate(CreateAdminUserCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var error in AdminUserValidation.Email(request.Email))
        {
            yield return error;
        }

        foreach (var error in AdminUserValidation.FullName(request.FullName))
        {
            yield return error;
        }

        foreach (var error in AdminUserValidation.Password(request.Password, "password"))
        {
            yield return error;
        }

        foreach (var error in AdminUserValidation.Scope(request.AllKiosks, request.KioskIds))
        {
            yield return error;
        }
    }
}

public sealed class UpdateAdminUserCommandValidator : IValidator<UpdateAdminUserCommand>
{
    public IEnumerable<ValidationError> Validate(UpdateAdminUserCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var error in AdminUserValidation.FullName(request.FullName))
        {
            yield return error;
        }

        foreach (var error in AdminUserValidation.Scope(request.AllKiosks, request.KioskIds))
        {
            yield return error;
        }
    }
}

public sealed class ResetAdminPasswordCommandValidator : IValidator<ResetAdminPasswordCommand>
{
    public IEnumerable<ValidationError> Validate(ResetAdminPasswordCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return AdminUserValidation.Password(request.NewPassword, "newPassword");
    }
}

/// <summary>The rules the admin-user validators share.</summary>
internal static class AdminUserValidation
{
    /// <summary>
    /// The password floor, the same as for changing one
    /// (<see cref="ChangeAdminPasswordCommandValidator.MinimumLength"/>).
    /// </summary>
    private const int MinimumPasswordLength = ChangeAdminPasswordCommandValidator.MinimumLength;

    internal static IEnumerable<ValidationError> Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            yield return new ValidationError("email", "Email tələb olunur.");
            yield break;
        }

        // Deliberately shallow: the only real test of an address is whether
        // mail to it arrives, and a stricter pattern rejects valid addresses.
        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at == trimmed.Length - 1 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            yield return new ValidationError("email", "Email düzgün formatda deyil.");
        }

        if (trimmed.Length > 254)
        {
            yield return new ValidationError("email", "Email 254 simvoldan uzun ola bilməz.");
        }
    }

    internal static IEnumerable<ValidationError> FullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            yield return new ValidationError("fullName", "Ad və soyad tələb olunur.");
        }
        else if (fullName.Trim().Length > 120)
        {
            yield return new ValidationError("fullName", "Ad 120 simvoldan uzun ola bilməz.");
        }
    }

    internal static IEnumerable<ValidationError> Password(string? password, string field)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
        {
            yield return new ValidationError(
                field, $"Şifrə ən azı {MinimumPasswordLength} simvol olmalıdır.");
        }
        else if (password.Length > 128)
        {
            yield return new ValidationError(field, "Şifrə 128 simvoldan uzun ola bilməz.");
        }
    }

    internal static IEnumerable<ValidationError> Scope(bool allKiosks, IReadOnlyCollection<Guid> kioskIds)
    {
        if (!allKiosks && kioskIds.Count == 0)
        {
            // A scoped account with an empty scope sees nothing, which is not
            // a useful thing to create (plan section 8A.2).
            yield return new ValidationError(
                "kioskIds", "Əhatəsi məhdud hesab üçün ən azı bir kiosk seçilməlidir.");
        }
    }
}
