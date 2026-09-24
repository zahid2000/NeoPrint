using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Roles and their permissions (plan section 8A.2).
/// </summary>
/// <remarks>
/// Roles are created and edited here at runtime; the set of permissions they
/// can be granted is fixed in code, so a role can never hold a permission that
/// nothing checks.
///
/// Transport only: the rules - which names are free, what a system role allows,
/// whether a role is still in use - live in the application layer
/// (<c>docs/ARCHITECTURE_RULES.md</c> section 1).
/// </remarks>
[ApiController]
[Route("api/admin/roles")]
public sealed class AdminRolesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminRolesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Every role, with how many accounts hold it.</summary>
    [HttpGet]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListRolesQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var role = await _sender.Send(new GetRoleQuery(id), cancellationToken);

        return role is null ? NotFound() : Ok(role);
    }

    [HttpPost]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Create(
        [FromBody] SaveRoleRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CreateRoleCommand(
                request.Name,
                request.Description,
                request.Permissions,
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Update(
        Guid id, [FromBody] SaveRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await _sender.Send(
            new UpdateRoleCommand(
                id,
                request.Name,
                request.Description,
                request.Permissions,
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken);

        return role is null ? NotFound() : Ok(role);
    }

    /// <summary>Copies a role, so a variant can be made without starting over.</summary>
    [HttpPost("{id:guid}/clone")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Clone(
        Guid id, [FromBody] CloneRoleRequest request, CancellationToken cancellationToken)
    {
        var clone = await _sender.Send(
            new CloneRoleCommand(id, request.Name, _currentUser.AdminUserId, _currentUser.Ip),
            cancellationToken);

        return clone is null ? NotFound() : Ok(clone);
    }

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new DeleteRoleCommand(id, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken);

        return result == DeleteRoleResult.NotFound ? NotFound() : NoContent();
    }

    public sealed record SaveRoleRequest(
        string Name, string? Description, IReadOnlyCollection<string> Permissions);

    public sealed record CloneRoleRequest(string Name);
}
