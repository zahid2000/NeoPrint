using System.Text.Json;

using MediatR;

using Microsoft.AspNetCore.Identity;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Admin;

/// <inheritdoc cref="ListAdminUsersQuery" />
public sealed class ListAdminUsersHandler
    : IRequestHandler<ListAdminUsersQuery, IReadOnlyList<AdminUserResponse>>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IClock _clock;

    public ListAdminUsersHandler(
        IAdminUserRepository admins, IAdminRoleRepository roles, IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AdminUserResponse>> Handle(
        ListAdminUsersQuery request, CancellationToken cancellationToken)
    {
        var admins = await _admins.ListAsync(cancellationToken).ConfigureAwait(false);
        var roles = await _roles.ListAsync(cancellationToken).ConfigureAwait(false);
        var byId = roles.ToDictionary(role => role.Id, role => role.Name);
        var now = _clock.UtcNow;

        return admins
            .OrderBy(admin => admin.FullName, StringComparer.CurrentCulture)
            .Select(admin => AdminUserMapping.ToResponse(admin, byId, now))
            .ToList();
    }
}

/// <inheritdoc cref="GetAdminUserQuery" />
public sealed class GetAdminUserHandler : IRequestHandler<GetAdminUserQuery, AdminUserResponse?>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IClock _clock;

    public GetAdminUserHandler(IAdminUserRepository admins, IAdminRoleRepository roles, IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _clock = clock;
    }

    public async Task<AdminUserResponse?> Handle(
        GetAdminUserQuery request, CancellationToken cancellationToken)
    {
        var admin = await _admins
            .FindByIdAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (admin is null)
        {
            return null;
        }

        var held = await _roles.FindByIdsAsync(admin.RoleIds, cancellationToken).ConfigureAwait(false);

        return AdminUserMapping.ToResponse(
            admin, held.ToDictionary(role => role.Id, role => role.Name), _clock.UtcNow);
    }
}

/// <inheritdoc cref="CreateAdminUserCommand" />
public sealed class CreateAdminUserHandler : IRequestHandler<CreateAdminUserCommand, AdminUserResponse>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IPasswordHasher<AdminUser> _passwordHasher;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public CreateAdminUserHandler(
        IAdminUserRepository admins,
        IAdminRoleRepository roles,
        IPasswordHasher<AdminUser> passwordHasher,
        IAuditWriter audit,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _clock = clock;
    }

    public async Task<AdminUserResponse> Handle(
        CreateAdminUserCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await _admins.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException(UserMessage.Of("adminUsers.emailTaken", ("email", request.Email)));
        }

        var held = await AdminUserMapping
            .ResolveRolesAsync(_roles, request.RoleIds, cancellationToken)
            .ConfigureAwait(false);

        var admin = new AdminUser
        {
            Email = email,
            FullName = request.FullName.Trim(),
            RoleIds = [.. request.RoleIds],
            KioskScope = request.AllKiosks
                ? KioskScope.Everything()
                : KioskScope.Only([.. request.KioskIds]),
            IsActive = true,
            // Whoever created the account chose this password, so the account
            // holder replaces it before doing anything (plan section 2.8).
            MustChangePassword = true,
            CreatedAt = _clock.UtcNow,
        };

        admin.PasswordHash = _passwordHasher.HashPassword(admin, request.Password);

        await _admins.AddAsync(admin, cancellationToken).ConfigureAwait(false);

        await AdminUserMapping
            .AuditAsync(
                _audit, "admins.create", admin, before: null, request.CreatedBy, request.Ip,
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        return AdminUserMapping.ToResponse(admin, held, _clock.UtcNow);
    }
}

/// <inheritdoc cref="UpdateAdminUserCommand" />
public sealed class UpdateAdminUserHandler : IRequestHandler<UpdateAdminUserCommand, AdminUserResponse?>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public UpdateAdminUserHandler(
        IAdminUserRepository admins,
        IAdminRoleRepository roles,
        IPermissionResolver permissions,
        IAuditWriter audit,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _permissions = permissions;
        _audit = audit;
        _clock = clock;
    }

    public async Task<AdminUserResponse?> Handle(
        UpdateAdminUserCommand request, CancellationToken cancellationToken)
    {
        var admin = await _admins
            .FindByIdAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (admin is null)
        {
            return null;
        }

        var before = AdminUserMapping.Snapshot(admin);

        var held = await AdminUserMapping
            .ResolveRolesAsync(_roles, request.RoleIds, cancellationToken)
            .ConfigureAwait(false);

        // Taking SuperAdmin away from the last account that holds it would
        // leave the installation with nobody who can put it back
        // (plan section 8A.2).
        await AdminUserMapping
            .EnsureASuperAdminRemainsAsync(
                _admins, _roles, admin, request.RoleIds, keepingActive: admin.IsActive, cancellationToken)
            .ConfigureAwait(false);

        admin.FullName = request.FullName.Trim();
        admin.RoleIds = [.. request.RoleIds];
        admin.KioskScope = request.AllKiosks
            ? KioskScope.Everything()
            : KioskScope.Only([.. request.KioskIds]);

        await _admins.UpdateAsync(admin, cancellationToken).ConfigureAwait(false);

        // Their permissions and kiosk scope just changed, so the cached answer
        // is wrong until it is cleared (plan section 8A.2).
        await _permissions.InvalidateAsync(admin.Id, cancellationToken).ConfigureAwait(false);

        await AdminUserMapping
            .AuditAsync(
                _audit, "admins.update", admin, before, request.UpdatedBy, request.Ip,
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        return AdminUserMapping.ToResponse(admin, held, _clock.UtcNow);
    }
}

/// <inheritdoc cref="SetAdminUserActiveCommand" />
public sealed class SetAdminUserActiveHandler : IRequestHandler<SetAdminUserActiveCommand, AdminUserResult>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IRefreshTokenRepository _tokens;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public SetAdminUserActiveHandler(
        IAdminUserRepository admins,
        IAdminRoleRepository roles,
        IRefreshTokenRepository tokens,
        IPermissionResolver permissions,
        IAuditWriter audit,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _tokens = tokens;
        _permissions = permissions;
        _audit = audit;
        _clock = clock;
    }

    public async Task<AdminUserResult> Handle(
        SetAdminUserActiveCommand request, CancellationToken cancellationToken)
    {
        var admin = await _admins
            .FindByIdAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (admin is null)
        {
            return AdminUserResult.NotFound;
        }

        if (admin.IsActive == request.IsActive)
        {
            // Already in the state being asked for; the panel may well send
            // the command twice.
            return AdminUserResult.Changed;
        }

        if (!request.IsActive)
        {
            await AdminUserMapping
                .EnsureASuperAdminRemainsAsync(
                    _admins, _roles, admin, admin.RoleIds, keepingActive: false, cancellationToken)
                .ConfigureAwait(false);
        }

        var before = AdminUserMapping.Snapshot(admin);

        admin.IsActive = request.IsActive;

        await _admins.UpdateAsync(admin, cancellationToken).ConfigureAwait(false);

        if (!request.IsActive)
        {
            // A deactivated account keeps no live session: its token would
            // otherwise work until it expired.
            await _tokens
                .RevokeAllForUserAsync(admin.Id, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }

        await _permissions.InvalidateAsync(admin.Id, cancellationToken).ConfigureAwait(false);

        await AdminUserMapping
            .AuditAsync(
                _audit,
                request.IsActive ? "admins.activate" : "admins.deactivate",
                admin,
                before,
                request.ChangedBy,
                request.Ip,
                request.Reason,
                cancellationToken)
            .ConfigureAwait(false);

        return AdminUserResult.Changed;
    }
}

/// <inheritdoc cref="ResetAdminPasswordCommand" />
public sealed class ResetAdminPasswordHandler : IRequestHandler<ResetAdminPasswordCommand, AdminUserResult>
{
    private readonly IAdminUserRepository _admins;
    private readonly IRefreshTokenRepository _tokens;
    private readonly IPasswordHasher<AdminUser> _passwordHasher;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public ResetAdminPasswordHandler(
        IAdminUserRepository admins,
        IRefreshTokenRepository tokens,
        IPasswordHasher<AdminUser> passwordHasher,
        IAuditWriter audit,
        IClock clock)
    {
        _admins = admins;
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _clock = clock;
    }

    public async Task<AdminUserResult> Handle(
        ResetAdminPasswordCommand request, CancellationToken cancellationToken)
    {
        var admin = await _admins
            .FindByIdAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (admin is null)
        {
            return AdminUserResult.NotFound;
        }

        admin.PasswordHash = _passwordHasher.HashPassword(admin, request.NewPassword);

        // Somebody else chose it, so the account holder replaces it before
        // doing anything else (plan section 2.8).
        admin.MustChangePassword = true;

        // A reset also clears a lockout: the point is to let them back in.
        admin.ClearLockout();

        await _admins.UpdateAsync(admin, cancellationToken).ConfigureAwait(false);

        // Every session goes, so a stolen one cannot outlive the reset.
        await _tokens
            .RevokeAllForUserAsync(admin.Id, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        await AdminUserMapping
            .AuditAsync(
                _audit, "admins.resetPassword", admin, before: null, request.ChangedBy, request.Ip,
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        return AdminUserResult.Changed;
    }
}

/// <summary>Shared mapping, validation and auditing for the admin-user handlers.</summary>
internal static class AdminUserMapping
{
    internal static AdminUserResponse ToResponse(
        AdminUser admin, IReadOnlyDictionary<Guid, string> roleNames, DateTime now) => new(
        admin.Id,
        admin.Email,
        admin.FullName,
        [.. admin.RoleIds],
        [.. admin.RoleIds.Select(id => roleNames.TryGetValue(id, out var name) ? name : "?")],
        admin.KioskScope.All,
        [.. admin.KioskScope.KioskIds],
        admin.IsActive,
        admin.MustChangePassword,
        admin.IsLockedOut(now),
        admin.LastLoginAt,
        admin.CreatedAt);

    /// <summary>
    /// Checks every role exists before it is assigned.
    /// </summary>
    /// <exception cref="DomainException">One of the roles does not exist.</exception>
    internal static async Task<Dictionary<Guid, string>> ResolveRolesAsync(
        IAdminRoleRepository roles,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
        {
            // An account with no role can sign in and do nothing, which is a
            // confusing thing to create by accident.
            throw new DomainException("adminUsers.roleRequired");
        }

        var held = await roles.FindByIdsAsync(roleIds, cancellationToken).ConfigureAwait(false);

        if (held.Count != roleIds.Distinct().Count())
        {
            throw new DomainException("adminUsers.roleUnknown");
        }

        return held.ToDictionary(role => role.Id, role => role.Name);
    }

    /// <summary>
    /// Refuses a change that would leave no active SuperAdmin
    /// (plan section 8A.2).
    /// </summary>
    /// <remarks>
    /// Without this an administrator can lock the whole installation out of its
    /// own panel - with no way back in short of editing the database.
    /// </remarks>
    /// <exception cref="DomainException">This is the last active SuperAdmin.</exception>
    internal static async Task EnsureASuperAdminRemainsAsync(
        IAdminUserRepository admins,
        IAdminRoleRepository roles,
        AdminUser admin,
        IReadOnlyCollection<Guid> newRoleIds,
        bool keepingActive,
        CancellationToken cancellationToken)
    {
        var superAdmin = await roles
            .FindByNameAsync(AdminRole.SuperAdminName, cancellationToken)
            .ConfigureAwait(false);

        if (superAdmin is null || !admin.RoleIds.Contains(superAdmin.Id))
        {
            // Not a SuperAdmin, so nothing about this change can take the last
            // one away.
            return;
        }

        var stillSuperAdmin = keepingActive && newRoleIds.Contains(superAdmin.Id);

        if (stillSuperAdmin)
        {
            return;
        }

        var holders = await admins
            .CountActiveWithRoleAsync(superAdmin.Id, cancellationToken)
            .ConfigureAwait(false);

        if (holders <= 1)
        {
            throw new DomainException("adminUsers.lastSuperAdmin");
        }
    }

    /// <summary>
    /// What the account looked like, for the audit diff.
    /// </summary>
    /// <remarks>
    /// The email is masked and the password hash is absent: an audit entry is
    /// read by people and does not need either
    /// (<c>docs/SECURITY_THREAT_MODEL.md</c>).
    /// </remarks>
    internal static string Snapshot(AdminUser admin)
        => JsonSerializer.Serialize(new
        {
            Email = LogMasking.Email(admin.Email),
            admin.FullName,
            admin.RoleIds,
            AllKiosks = admin.KioskScope.All,
            admin.KioskScope.KioskIds,
            admin.IsActive,
        });

    internal static Task AuditAsync(
        IAuditWriter audit,
        string action,
        AdminUser admin,
        string? before,
        Guid? actorId,
        string? ip,
        string? reason,
        CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                actorId,
                action,
                "AdminUser",
                admin.Id.ToString(),
                before,
                Snapshot(admin),
                ip,
                reason,
                ActorType.Admin),
            cancellationToken);
}
