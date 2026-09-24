using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Administrator and operator accounts (plan sections 8A.2, 8A.3).
/// </summary>
/// <remarks>
/// Accounts are deactivated rather than deleted: an account that did things is
/// referenced by its audit entries, and an audit trail pointing at nothing is
/// worse than one pointing at a disabled account
/// (<c>docs/DATA_RETENTION.md</c>).
/// </remarks>
[ApiController]
[Route("api/admin/admins")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminUsersController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Every account, with its roles and kiosk scope.</summary>
    [HttpGet]
    [HasPermission(Permissions.AdminsManage)]
    public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> List(
        CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListAdminUsersQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.AdminsManage)]
    public async Task<ActionResult<AdminUserResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var admin = await _sender.Send(new GetAdminUserQuery(id), cancellationToken);

        return admin is null ? NotFound() : Ok(admin);
    }

    [HttpPost]
    [HasPermission(Permissions.AdminsManage)]
    [Idempotent]
    public async Task<ActionResult<AdminUserResponse>> Create(
        [FromBody] CreateAdminUserRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CreateAdminUserCommand(
                request.Email,
                request.FullName,
                request.Password,
                request.RoleIds,
                request.AllKiosks,
                request.KioskIds ?? [],
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.AdminsManage)]
    public async Task<ActionResult<AdminUserResponse>> Update(
        Guid id, [FromBody] UpdateAdminUserRequest request, CancellationToken cancellationToken)
    {
        var admin = await _sender.Send(
            new UpdateAdminUserCommand(
                id,
                request.FullName,
                request.RoleIds,
                request.AllKiosks,
                request.KioskIds ?? [],
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken);

        return admin is null ? NotFound() : Ok(admin);
    }

    /// <summary>Activates or deactivates an account.</summary>
    [HttpPost("{id:guid}/active")]
    [HasPermission(Permissions.AdminsManage)]
    public async Task<IActionResult> SetActive(
        Guid id, [FromBody] SetActiveRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new SetAdminUserActiveCommand(
                id, request.IsActive, request.Reason, _currentUser.AdminUserId, _currentUser.Ip),
            cancellationToken);

        return result == AdminUserResult.NotFound ? NotFound() : NoContent();
    }

    /// <summary>Sets a new password, which the account holder must then change.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [HasPermission(Permissions.AdminsManage)]
    public async Task<IActionResult> ResetPassword(
        Guid id, [FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ResetAdminPasswordCommand(
                id, request.NewPassword, _currentUser.AdminUserId, _currentUser.Ip),
            cancellationToken);

        return result == AdminUserResult.NotFound ? NotFound() : NoContent();
    }

    public sealed record CreateAdminUserRequest(
        string Email,
        string FullName,
        string Password,
        IReadOnlyCollection<Guid> RoleIds,
        bool AllKiosks,
        IReadOnlyCollection<Guid>? KioskIds);

    public sealed record UpdateAdminUserRequest(
        string FullName,
        IReadOnlyCollection<Guid> RoleIds,
        bool AllKiosks,
        IReadOnlyCollection<Guid>? KioskIds);

    public sealed record SetActiveRequest(bool IsActive, string? Reason);

    public sealed record ResetPasswordRequest(string NewPassword);
}
