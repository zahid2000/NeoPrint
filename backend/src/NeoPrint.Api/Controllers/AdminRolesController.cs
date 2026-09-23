using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Roles and their permissions (plan section 8A.2).
/// </summary>
/// <remarks>
/// Roles are created and edited here at runtime; the set of permissions they
/// can be granted is fixed in code, so a role can never hold a permission that
/// nothing checks.
/// </remarks>
[ApiController]
[Route("api/admin/roles")]
public sealed class AdminRolesController : ControllerBase
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IAdminNotifier _notifier;

    public AdminRolesController(
        IAdminRoleRepository roles,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        IAuditWriter audit,
        ICurrentUser currentUser,
        IAdminNotifier notifier)
    {
        _roles = roles;
        _admins = admins;
        _permissions = permissions;
        _audit = audit;
        _currentUser = currentUser;
        _notifier = notifier;
    }

    /// <summary>Every role, with how many accounts hold it.</summary>
    [HttpGet]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> List(CancellationToken cancellationToken)
    {
        var roles = await _roles.ListAsync(cancellationToken);
        var responses = new List<RoleResponse>(roles.Count);

        foreach (var role in roles)
        {
            var holders = await _admins.CountActiveWithRoleAsync(role.Id, cancellationToken);
            responses.Add(ToResponse(role, holders));
        }

        return Ok(responses);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var role = await _roles.FindByIdAsync(id, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        return Ok(ToResponse(role, await _admins.CountActiveWithRoleAsync(role.Id, cancellationToken)));
    }

    [HttpPost]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Create(
        [FromBody] SaveRoleRequest request, CancellationToken cancellationToken)
    {
        if (await _roles.FindByNameAsync(request.Name, cancellationToken) is not null)
        {
            throw new DomainException($"'{request.Name}' adlı rol artıq mövcuddur.");
        }

        var role = new AdminRole
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            CreatedBy = _currentUser.AdminUserId,
        };

        // Throws if any permission is outside the catalogue.
        role.SetPermissions(request.Permissions);

        await _roles.AddAsync(role, cancellationToken);
        await WriteAuditAsync("roles.create", role, null, cancellationToken);

        return Ok(ToResponse(role, 0));
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Update(
        Guid id, [FromBody] SaveRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await _roles.FindByIdAsync(id, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystem)
        {
            throw new DomainException($"'{role.Name}' sistem roludur və redaktə edilə bilməz.");
        }

        var before = Snapshot(role);

        var renamed = request.Name.Trim();
        if (!string.Equals(renamed, role.Name, StringComparison.Ordinal) &&
            await _roles.FindByNameAsync(renamed, cancellationToken) is not null)
        {
            throw new DomainException($"'{renamed}' adlı rol artıq mövcuddur.");
        }

        role.Name = renamed;
        role.Description = request.Description;
        role.SetPermissions(request.Permissions);

        await _roles.UpdateAsync(role, cancellationToken);

        // Everyone holding this role gets the change at once, without signing
        // in again (plan section 8A.2).
        await _permissions.InvalidateRoleAsync(role.Id, cancellationToken);
        await _notifier.PermissionsChangedAsync(role.Id, cancellationToken);

        await WriteAuditAsync("roles.update", role, before, cancellationToken);

        return Ok(ToResponse(role, await _admins.CountActiveWithRoleAsync(role.Id, cancellationToken)));
    }

    /// <summary>Copies a role, so a variant can be made without starting over.</summary>
    [HttpPost("{id:guid}/clone")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Clone(
        Guid id, [FromBody] CloneRoleRequest request, CancellationToken cancellationToken)
    {
        var source = await _roles.FindByIdAsync(id, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        if (await _roles.FindByNameAsync(request.Name, cancellationToken) is not null)
        {
            throw new DomainException($"'{request.Name}' adlı rol artıq mövcuddur.");
        }

        var clone = new AdminRole
        {
            Name = request.Name.Trim(),
            Description = source.Description,
            CreatedBy = _currentUser.AdminUserId,
        };

        // From EffectivePermissions, so cloning SuperAdmin yields a real,
        // editable role holding everything rather than an empty list.
        clone.SetPermissions(source.EffectivePermissions);

        await _roles.AddAsync(clone, cancellationToken);
        await WriteAuditAsync("roles.clone", clone, null, cancellationToken);

        return Ok(ToResponse(clone, 0));
    }

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var role = await _roles.FindByIdAsync(id, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystem)
        {
            throw new DomainException($"'{role.Name}' sistem roludur və silinə bilməz.");
        }

        var holders = await _admins.CountActiveWithRoleAsync(role.Id, cancellationToken);
        if (holders > 0)
        {
            // Deleting a role in use would silently strip permissions from
            // people who are working (plan section 8A.2).
            throw new DomainException(
                $"Bu rol {holders} istifadəçiyə verilib və silinə bilməz. Əvvəlcə rolu onlardan götürün.");
        }

        await _roles.DeleteAsync(role.Id, cancellationToken);
        await WriteAuditAsync("roles.delete", role, Snapshot(role), cancellationToken);

        return NoContent();
    }

    private Task WriteAuditAsync(string action, AdminRole role, string? before, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(
                _currentUser.AdminUserId,
                action,
                "AdminRole",
                role.Id.ToString(),
                before,
                Snapshot(role),
                _currentUser.Ip,
                null),
            cancellationToken);

    private static string Snapshot(AdminRole role)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            role.Name,
            role.Description,
            role.Permissions,
        });

    private static RoleResponse ToResponse(AdminRole role, long holders) => new(
        role.Id,
        role.Name,
        role.Description,
        [.. role.EffectivePermissions],
        role.IsSystem,
        holders,
        role.UpdatedAt);

    public sealed record RoleResponse(
        Guid Id,
        string Name,
        string? Description,
        IReadOnlyCollection<string> Permissions,
        bool IsSystem,
        long UserCount,
        DateTime UpdatedAt);

    public sealed record SaveRoleRequest(
        string Name, string? Description, IReadOnlyCollection<string> Permissions);

    public sealed record CloneRoleRequest(string Name);
}
