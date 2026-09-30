using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Promos;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Promokodu yoxla" (plan section 6: <c>POST promos/validate</c>) for the
/// kiosk's options screen and the app. Any signed-in caller, as the quote is;
/// an account's own per-account limit is taken into account.
/// </summary>
[ApiController]
[Route("api/promos")]
[Authorize]
public sealed class PromosController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public PromosController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpPost("validate")]
    public async Task<ActionResult<PromoValidation>> Validate([FromBody] ValidatePromoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new ValidatePromoQuery(request.Code, request.KioskId, request.Amount, _currentUser.UserId), cancellationToken));
    }
}

/// <param name="Amount">What is left to pay: the price after the volume discount and bonus pages.</param>
public sealed record ValidatePromoRequest(string? Code, Guid? KioskId, decimal Amount);

/// <summary>"Promokodlar" in the admin panel (plan section 6: <c>CRUD admin/promos</c>).</summary>
/// <remarks>
/// No delete: a code that was used is part of payments' history. A code is
/// switched off instead, and a code nobody used can be switched off the same way.
/// </remarks>
[ApiController]
[Route("api/admin/promos")]
public sealed class AdminPromosController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminPromosController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    [HttpGet]
    [HasPermission(Permissions.PromosManage)]
    public async Task<ActionResult<PromoPage>> List(
        [FromQuery] string? code, [FromQuery] bool? active, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListPromosQuery(code, active, page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.PromosManage)]
    public async Task<ActionResult<PromoView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPromoQuery(id), cancellationToken));

    [HttpPost]
    [Idempotent]
    [HasPermission(Permissions.PromosManage)]
    public async Task<ActionResult<PromoView>> Create([FromBody] PromoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await _sender.Send(Command(null, request), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>The terms; the code itself does not change.</summary>
    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.PromosManage)]
    public async Task<ActionResult<PromoView>> Update(Guid id, [FromBody] PromoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(Command(id, request), cancellationToken));
    }

    [HttpPost("{id:guid}/activate")]
    [HasPermission(Permissions.PromosManage)]
    public async Task<ActionResult<PromoView>> Activate(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetPromoActiveCommand(AdminId, id, true, _currentUser.Ip), cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [HasPermission(Permissions.PromosManage)]
    public async Task<ActionResult<PromoView>> Deactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetPromoActiveCommand(AdminId, id, false, _currentUser.Ip), cancellationToken));

    private SavePromoCommand Command(Guid? id, PromoRequest request)
        => new(
            AdminId,
            id,
            request.Code,
            request.Kind,
            request.Value,
            request.StartsAt?.ToUniversalTime(),
            request.EndsAt?.ToUniversalTime(),
            request.TotalLimit,
            request.PerUserLimit,
            request.MinAmount,
            request.KioskIds,
            request.ProfileIds,
            request.Description,
            _currentUser.Ip);
}

/// <param name="Kind"><c>Percent</c> or <c>Fixed</c>.</param>
/// <param name="Value">Percent (0-100] or AZN.</param>
public sealed record PromoRequest(
    string? Code,
    string? Kind,
    decimal Value,
    DateTime? StartsAt,
    DateTime? EndsAt,
    int? TotalLimit,
    int? PerUserLimit,
    decimal MinAmount,
    IReadOnlyList<Guid>? KioskIds,
    IReadOnlyList<Guid>? ProfileIds,
    string? Description);
