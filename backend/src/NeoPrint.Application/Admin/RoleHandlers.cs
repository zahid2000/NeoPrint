using System.Text.Json;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Admin;

/// <summary>
/// Reading roles (plan section 8A.2).
/// </summary>
public sealed class ListRolesHandler : IRequestHandler<ListRolesQuery, IReadOnlyList<RoleResponse>>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminUserRepository _admins;

    public ListRolesHandler(IAdminRoleRepository roles, IAdminUserRepository admins)
    {
        _roles = roles;
        _admins = admins;
    }

    public async Task<IReadOnlyList<RoleResponse>> Handle(
        ListRolesQuery request, CancellationToken cancellationToken)
    {
        var roles = await _roles.ListAsync(cancellationToken).ConfigureAwait(false);
        var responses = new List<RoleResponse>(roles.Count);

        foreach (var role in roles)
        {
            var holders = await _admins
                .CountActiveWithRoleAsync(role.Id, cancellationToken)
                .ConfigureAwait(false);

            responses.Add(RoleMapping.ToResponse(role, holders));
        }

        return responses;
    }
}

/// <inheritdoc cref="GetRoleQuery" />
public sealed class GetRoleHandler : IRequestHandler<GetRoleQuery, RoleResponse?>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminUserRepository _admins;

    public GetRoleHandler(IAdminRoleRepository roles, IAdminUserRepository admins)
    {
        _roles = roles;
        _admins = admins;
    }

    public async Task<RoleResponse?> Handle(GetRoleQuery request, CancellationToken cancellationToken)
    {
        var role = await _roles.FindByIdAsync(request.RoleId, cancellationToken).ConfigureAwait(false);

        if (role is null)
        {
            return null;
        }

        var holders = await _admins
            .CountActiveWithRoleAsync(role.Id, cancellationToken)
            .ConfigureAwait(false);

        return RoleMapping.ToResponse(role, holders);
    }
}

/// <inheritdoc cref="CreateRoleCommand" />
public sealed class CreateRoleHandler : IRequestHandler<CreateRoleCommand, RoleResponse>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAuditWriter _audit;

    public CreateRoleHandler(IAdminRoleRepository roles, IAuditWriter audit)
    {
        _roles = roles;
        _audit = audit;
    }

    public async Task<RoleResponse> Handle(CreateRoleCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();

        if (await _roles.FindByNameAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException($"'{request.Name}' adlı rol artıq mövcuddur.");
        }

        var role = new AdminRole
        {
            Name = name,
            Description = request.Description,
            CreatedBy = request.CreatedBy,
        };

        // Throws if any permission is outside the catalogue, so a role can
        // never hold a permission that nothing checks.
        role.SetPermissions(request.Permissions);

        await _roles.AddAsync(role, cancellationToken).ConfigureAwait(false);

        await RoleMapping
            .AuditAsync(_audit, "roles.create", role, before: null, request.CreatedBy, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return RoleMapping.ToResponse(role, holders: 0);
    }
}

/// <inheritdoc cref="UpdateRoleCommand" />
public sealed class UpdateRoleHandler : IRequestHandler<UpdateRoleCommand, RoleResponse?>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IAdminNotifier _notifier;
    private readonly IAuditWriter _audit;

    public UpdateRoleHandler(
        IAdminRoleRepository roles,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        IAdminNotifier notifier,
        IAuditWriter audit)
    {
        _roles = roles;
        _admins = admins;
        _permissions = permissions;
        _notifier = notifier;
        _audit = audit;
    }

    public async Task<RoleResponse?> Handle(UpdateRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await _roles.FindByIdAsync(request.RoleId, cancellationToken).ConfigureAwait(false);

        if (role is null)
        {
            return null;
        }

        if (role.IsSystem)
        {
            throw new DomainException($"'{role.Name}' sistem roludur və redaktə edilə bilməz.");
        }

        var before = RoleMapping.Snapshot(role);
        var renamed = request.Name.Trim();

        if (!string.Equals(renamed, role.Name, StringComparison.Ordinal) &&
            await _roles.FindByNameAsync(renamed, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException($"'{renamed}' adlı rol artıq mövcuddur.");
        }

        role.Name = renamed;
        role.Description = request.Description;
        role.SetPermissions(request.Permissions);

        await _roles.UpdateAsync(role, cancellationToken).ConfigureAwait(false);

        // Everyone holding this role gets the change at once, without signing
        // in again (plan section 8A.2).
        await _permissions.InvalidateRoleAsync(role.Id, cancellationToken).ConfigureAwait(false);
        await _notifier.PermissionsChangedAsync(role.Id, cancellationToken).ConfigureAwait(false);

        await RoleMapping
            .AuditAsync(_audit, "roles.update", role, before, request.UpdatedBy, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        var holders = await _admins
            .CountActiveWithRoleAsync(role.Id, cancellationToken)
            .ConfigureAwait(false);

        return RoleMapping.ToResponse(role, holders);
    }
}

/// <inheritdoc cref="CloneRoleCommand" />
public sealed class CloneRoleHandler : IRequestHandler<CloneRoleCommand, RoleResponse?>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAuditWriter _audit;

    public CloneRoleHandler(IAdminRoleRepository roles, IAuditWriter audit)
    {
        _roles = roles;
        _audit = audit;
    }

    public async Task<RoleResponse?> Handle(CloneRoleCommand request, CancellationToken cancellationToken)
    {
        var source = await _roles
            .FindByIdAsync(request.SourceRoleId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return null;
        }

        var name = request.Name.Trim();

        if (await _roles.FindByNameAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException($"'{request.Name}' adlı rol artıq mövcuddur.");
        }

        var clone = new AdminRole
        {
            Name = name,
            Description = source.Description,
            CreatedBy = request.CreatedBy,
        };

        // From EffectivePermissions, so cloning SuperAdmin yields a real,
        // editable role holding everything rather than an empty list.
        clone.SetPermissions(source.EffectivePermissions);

        await _roles.AddAsync(clone, cancellationToken).ConfigureAwait(false);

        await RoleMapping
            .AuditAsync(_audit, "roles.clone", clone, before: null, request.CreatedBy, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return RoleMapping.ToResponse(clone, holders: 0);
    }
}

/// <inheritdoc cref="DeleteRoleCommand" />
public sealed class DeleteRoleHandler : IRequestHandler<DeleteRoleCommand, DeleteRoleResult>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminUserRepository _admins;
    private readonly IAuditWriter _audit;

    public DeleteRoleHandler(
        IAdminRoleRepository roles, IAdminUserRepository admins, IAuditWriter audit)
    {
        _roles = roles;
        _admins = admins;
        _audit = audit;
    }

    public async Task<DeleteRoleResult> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await _roles.FindByIdAsync(request.RoleId, cancellationToken).ConfigureAwait(false);

        if (role is null)
        {
            return DeleteRoleResult.NotFound;
        }

        if (role.IsSystem)
        {
            throw new DomainException($"'{role.Name}' sistem roludur və silinə bilməz.");
        }

        var holders = await _admins
            .CountActiveWithRoleAsync(role.Id, cancellationToken)
            .ConfigureAwait(false);

        if (holders > 0)
        {
            // Deleting a role in use would silently strip permissions from
            // people who are working (plan section 8A.2).
            throw new DomainException(
                $"Bu rol {holders} istifadəçiyə verilib və silinə bilməz. Əvvəlcə rolu onlardan götürün.");
        }

        var snapshot = RoleMapping.Snapshot(role);

        await _roles.DeleteAsync(role.Id, cancellationToken).ConfigureAwait(false);

        await RoleMapping
            .AuditAsync(_audit, "roles.delete", role, snapshot, request.DeletedBy, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return DeleteRoleResult.Deleted;
    }
}

/// <summary>Shared mapping and auditing for the role handlers.</summary>
internal static class RoleMapping
{
    internal static RoleResponse ToResponse(AdminRole role, long holders) => new(
        role.Id,
        role.Name,
        role.Description,
        [.. role.EffectivePermissions],
        role.IsSystem,
        holders,
        role.UpdatedAt);

    /// <summary>
    /// What the role looked like, for the audit diff.
    /// </summary>
    /// <remarks>
    /// Name, description and permissions only: enough to answer "what changed"
    /// without copying the whole document.
    /// </remarks>
    internal static string Snapshot(AdminRole role)
        => JsonSerializer.Serialize(new
        {
            role.Name,
            role.Description,
            role.Permissions,
        });

    internal static Task AuditAsync(
        IAuditWriter audit,
        string action,
        AdminRole role,
        string? before,
        Guid? adminUserId,
        string? ip,
        CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                adminUserId,
                action,
                "AdminRole",
                role.Id.ToString(),
                before,
                Snapshot(role),
                ip,
                Reason: null,
                ActorType.Admin),
            cancellationToken);
}

/// <inheritdoc cref="GetSignedInAdminQuery" />
public sealed class GetSignedInAdminHandler : IRequestHandler<GetSignedInAdminQuery, AdminSummary?>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IPermissionResolver _permissions;

    public GetSignedInAdminHandler(
        IAdminUserRepository admins, IAdminRoleRepository roles, IPermissionResolver permissions)
    {
        _admins = admins;
        _roles = roles;
        _permissions = permissions;
    }

    public async Task<AdminSummary?> Handle(
        GetSignedInAdminQuery request, CancellationToken cancellationToken)
    {
        var admin = await _admins
            .FindByIdAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (admin is null)
        {
            return null;
        }

        var held = await _roles.FindByIdsAsync(admin.RoleIds, cancellationToken).ConfigureAwait(false);

        // Resolved rather than read from the token, so the panel sees a
        // permission change without signing in again (plan section 8A.2).
        var effective = await _permissions
            .ResolveAsync(admin.Id, cancellationToken)
            .ConfigureAwait(false);

        return new AdminSummary(
            admin.Id,
            admin.Email,
            admin.FullName,
            [.. held.Select(role => role.Name)],
            effective.Permissions,
            effective.AllKiosks,
            effective.KioskIds,
            admin.MustChangePassword);
    }
}
