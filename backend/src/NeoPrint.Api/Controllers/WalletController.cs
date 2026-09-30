using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Wallets;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The user's wallet (plan section 6, "Wallet"): balance, history and card
/// top-ups. There is no endpoint that pays a balance out - by design (plan
/// section 2.14).
/// </summary>
[ApiController]
[Route("api/wallet")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class WalletController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public WalletController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<ActionResult<WalletView>> Get(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetWalletQuery(UserId), cancellationToken));

    [HttpGet("transactions")]
    public async Task<ActionResult<WalletTransactionPage>> Transactions(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListWalletTransactionsQuery(UserId, page, pageSize), cancellationToken));

    /// <summary>Puts money on the wallet from a card. What is put on is never paid back (plan section 2.14).</summary>
    [HttpPost("top-up")]
    [Idempotent]
    public async Task<ActionResult<TopUpResponse>> TopUp([FromBody] TopUpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = Request.Headers["Idempotency-Key"].ToString();

        return Ok(await _sender.Send(new TopUpWalletCommand(UserId, request.Amount, key.Length > 0 ? key : null), cancellationToken));
    }
}

public sealed record TopUpRequest(decimal Amount);

/// <summary>Wallet corrections in the admin panel (plan section 6, "Admin Users": <c>POST admin/wallets/{userId}/adjust</c>).</summary>
[ApiController]
[Route("api/admin/wallets")]
public sealed class AdminWalletsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminWalletsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    [HttpGet("{userId:guid}")]
    [HasPermission(Permissions.UsersView)]
    public async Task<ActionResult<WalletView>> Get(Guid userId, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetWalletQuery(userId), cancellationToken));

    [HttpGet("{userId:guid}/transactions")]
    [HasPermission(Permissions.UsersView)]
    public async Task<ActionResult<WalletTransactionPage>> Transactions(
        Guid userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListWalletTransactionsQuery(userId, page, pageSize), cancellationToken));

    /// <summary>
    /// A correction: the reason is mandatory and audited; it can never take
    /// the balance below zero, so it cannot move money out.
    /// </summary>
    [HttpPost("{userId:guid}/adjust")]
    [Idempotent]
    [HasPermission(Permissions.WalletsAdjust)]
    public async Task<ActionResult<WalletView>> Adjust(Guid userId, [FromBody] AdjustWalletRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = Request.Headers["Idempotency-Key"].ToString();

        return Ok(await _sender.Send(
            new AdjustWalletCommand(
                AdminId,
                userId,
                request.Amount,
                request.BonusPages,
                request.Reason,
                key.Length > 0 ? key : null,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken));
    }
}

/// <param name="Amount">Signed: positive adds, negative corrects down (never below zero).</param>
public sealed record AdjustWalletRequest(decimal Amount, int BonusPages, string? Reason);
