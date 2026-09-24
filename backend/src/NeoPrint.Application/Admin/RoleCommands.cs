using MediatR;

using NeoPrint.Application.Behaviors;

namespace NeoPrint.Application.Admin;

/// <summary>
/// A role as the panel shows it (plan section 8A.2).
/// </summary>
/// <param name="Permissions">
/// The effective permissions, so cloning SuperAdmin yields a real, editable
/// role holding everything rather than an empty list.
/// </param>
/// <param name="UserCount">How many active accounts hold it.</param>
public sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyCollection<string> Permissions,
    bool IsSystem,
    long UserCount,
    DateTime UpdatedAt);

/// <summary>Every role, with how many accounts hold each.</summary>
public sealed record ListRolesQuery : IRequest<IReadOnlyList<RoleResponse>>;

/// <summary>One role, or <c>null</c> when there is no such role.</summary>
public sealed record GetRoleQuery(Guid RoleId) : IRequest<RoleResponse?>;

/// <param name="CreatedBy">The administrator doing it, for the audit entry.</param>
public sealed record CreateRoleCommand(
    string Name,
    string? Description,
    IReadOnlyCollection<string> Permissions,
    Guid? CreatedBy,
    string? Ip) : IRequest<RoleResponse>, ITransactionalRequest;

/// <summary>
/// Renames a role and replaces its permissions.
/// </summary>
/// <remarks>
/// Transactional: saving the role and clearing the cached permissions of
/// everyone holding it have to happen together, or somebody keeps acting on
/// permissions the role no longer grants.
/// </remarks>
public sealed record UpdateRoleCommand(
    Guid RoleId,
    string Name,
    string? Description,
    IReadOnlyCollection<string> Permissions,
    Guid? UpdatedBy,
    string? Ip) : IRequest<RoleResponse?>, ITransactionalRequest;

/// <summary>Copies a role, so a variant can be made without starting over.</summary>
public sealed record CloneRoleCommand(
    Guid SourceRoleId,
    string Name,
    Guid? CreatedBy,
    string? Ip) : IRequest<RoleResponse?>, ITransactionalRequest;

public sealed record DeleteRoleCommand(
    Guid RoleId,
    Guid? DeletedBy,
    string? Ip) : IRequest<DeleteRoleResult>, ITransactionalRequest;

/// <summary>Whether the role was there to delete.</summary>
public enum DeleteRoleResult
{
    Deleted = 0,
    NotFound = 1,
}

/// <summary>
/// The signed-in administrator, with their effective permissions and kiosk
/// scope (plan section 8A.2).
/// </summary>
/// <remarks>
/// Resolved rather than read from the token, so the panel sees a permission
/// change without signing in again. Returns <c>null</c> when the account is
/// gone, which the caller answers with a refusal rather than an empty profile.
/// </remarks>
public sealed record GetSignedInAdminQuery(Guid AdminUserId) : IRequest<AdminSummary?>;
