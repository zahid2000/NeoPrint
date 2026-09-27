using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Dashboard;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>The dashboard's figures (plan section 6: <c>GET admin/dashboard</c>).</summary>
[ApiController]
[Route("api/admin/dashboard")]
[HasPermission(Permissions.DashboardView)]
public sealed class AdminDashboardController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionResolver _permissions;

    public AdminDashboardController(ISender sender, ICurrentUser currentUser, IPermissionResolver permissions)
    {
        _sender = sender;
        _currentUser = currentUser;
        _permissions = permissions;
    }

    [HttpGet]
    public async Task<ActionResult<AdminDashboardResponse>> Get(CancellationToken cancellationToken)
    {
        var effective = await _permissions
            .ResolveAsync(_currentUser.AdminUserId!.Value, cancellationToken)
            .ConfigureAwait(false);

        return Ok(await _sender.Send(
            new GetAdminDashboardQuery(effective.Has(Permissions.StorageManage)), cancellationToken));
    }
}
