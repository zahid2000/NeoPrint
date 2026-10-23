using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Customers;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The app's customers in the admin panel (plan section 8A.3, "İstifadəçilər"):
/// search, detail, block and unblock. Staff accounts are <c>api/admin/admins</c>.
/// </summary>
[ApiController]
[Route("api/admin/users")]
public sealed class AdminCustomersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminCustomersController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet]
    [HasPermission(Permissions.UsersView)]
    public async Task<ActionResult<CustomerPage>> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListCustomersQuery(search, status, page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.UsersView)]
    public async Task<ActionResult<CustomerDetail>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetCustomerQuery(id), cancellationToken));

    /// <summary>Ends every session now; the wallet is kept. The reason is audited.</summary>
    [HttpPost("{id:guid}/block")]
    [Idempotent]
    [HasPermission(Permissions.UsersBlock)]
    public async Task<ActionResult<CustomerRow>> Block(Guid id, [FromBody] CustomerReasonRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new BlockCustomerCommand(AdminId, id, request?.Reason, Ip), cancellationToken));

    [HttpPost("{id:guid}/unblock")]
    [Idempotent]
    [HasPermission(Permissions.UsersBlock)]
    public async Task<ActionResult<CustomerRow>> Unblock(Guid id, [FromBody] CustomerReasonRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new UnblockCustomerCommand(AdminId, id, request?.Reason, Ip), cancellationToken));

    /// <summary>A test account, or not (T-300): its orders leave the pilot and revenue reports. The reason is required and audited.</summary>
    [HttpPost("{id:guid}/test-account")]
    [HasPermission(Permissions.UsersMarkTest)]
    public async Task<ActionResult<CustomerRow>> TestAccount(Guid id, [FromBody] CustomerTestAccountRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetCustomerTestAccountCommand(AdminId, id, request?.IsTest ?? false, request?.Reason, Ip), cancellationToken));
}

public sealed record CustomerReasonRequest(string? Reason);

public sealed record CustomerTestAccountRequest(bool IsTest, string? Reason);
