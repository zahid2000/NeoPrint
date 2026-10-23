using MediatR;
using NeoPrint.Application.Admin;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Wallets;
using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Payments;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The refund status page an anonymous customer opens from the QR on the
/// kiosk's screen or receipt (plan section 2.9: <c>GET refunds/{id}/public</c>).
/// The id is a random GUID; the answer carries no personal data.
/// </summary>
[ApiController]
[Route("api/refunds")]
[AllowAnonymous]
public sealed class RefundsController : ControllerBase
{
    private readonly ISender _sender;

    public RefundsController(ISender sender) => _sender = sender;

    [HttpGet("{id:guid}/public")]
    public async Task<ActionResult<PublicRefundView>> Public(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPublicRefundQuery(id), cancellationToken));
}

/// <summary>A voucher into the wallet (plan section 6: <c>POST wallet/vouchers/redeem</c>).</summary>
[ApiController]
[Route("api/wallet/vouchers")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class VouchersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public VouchersController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpPost("redeem")]
    [Idempotent]
    public async Task<ActionResult<WalletView>> Redeem([FromBody] RedeemVoucherRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new RedeemVoucherCommand(_currentUser.UserId ?? throw new UnauthorizedAccessException(), request.Code), cancellationToken));
    }
}

public sealed record RedeemVoucherRequest(string? Code);

/// <summary>Refunds in the admin panel (plan sections 6 "Admin Payments", 8A.3).</summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminRefundsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminRefundsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("payments/{id:guid}/refund-options")]
    [KioskScoped(RecordKind.Payment)]
    [HasPermission(Permissions.PaymentsRefund)]
    public async Task<ActionResult<RefundOptionsView>> Options(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetRefundOptionsQuery(id), cancellationToken));

    /// <summary>"Qaytar" / "Job-u ləğv et və qaytar": the reason is mandatory; planned at once and carried out, a provider refund that does not answer goes on in the background.</summary>
    [HttpPost("payments/{id:guid}/refund")]
    [KioskScoped(RecordKind.Payment)]
    [Idempotent]
    [HasPermission(Permissions.PaymentsRefund)]
    public async Task<ActionResult<AdminRefundResult>> Refund(Guid id, [FromBody] AdminRefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = Request.Headers["Idempotency-Key"].ToString();

        return Ok(await _sender.Send(
            new AdminRefundCommand(AdminId, id, request.Amount, request.Destination, request.Reason, request.CancelJob, key.Length > 0 ? key : null, Ip),
            cancellationToken));
    }

    [HttpGet("refunds")]
    [HasPermission(Permissions.RefundsView)]
    public async Task<ActionResult<RefundPage>> List(
        [FromQuery] string[]? status,
        [FromQuery] string[]? reason,
        [FromQuery] Guid? paymentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListRefundsQuery(_currentUser.AdminUserId ?? throw new UnauthorizedAccessException(), status, reason, paymentId, page, pageSize), cancellationToken));

    [HttpGet("refunds/{id:guid}")]
    [KioskScoped(RecordKind.Refund)]
    [HasPermission(Permissions.RefundsView)]
    public async Task<ActionResult<RefundView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetRefundQuery(id), cancellationToken));

    /// <summary>A failed refund again, the same way or to another destination ("Təkrarla" / "İstiqaməti dəyiş").</summary>
    [HttpPost("refunds/{id:guid}/retry")]
    [KioskScoped(RecordKind.Refund)]
    [Idempotent]
    [HasPermission(Permissions.RefundsRetry)]
    public async Task<ActionResult<RefundView>> Retry(Guid id, [FromBody] RetryRefundRequest? request, CancellationToken cancellationToken)
    {
        await _sender.Send(new AdminRetryRefundCommand(AdminId, id, request?.Destination, request?.Note, Ip), cancellationToken);

        // Carried out now rather than at the next minute's run.
        await _sender.Send(new ProcessRefundCommand(id), cancellationToken);

        return Ok(await _sender.Send(new GetRefundQuery(id), cancellationToken));
    }
}

/// <param name="Amount">Null for everything still refundable.</param>
/// <param name="Destination">For the bank or card part: <c>Wallet</c> or <c>OriginalMethod</c>, as the options allow.</param>
public sealed record AdminRefundRequest(decimal? Amount, string? Destination, string? Reason, bool CancelJob);

public sealed record RetryRefundRequest(string? Destination, string? Note);

/// <summary>Refunds that fail on purpose, in Development (plan section 6, "Dev").</summary>
[ApiController]
[Route("api/dev")]
[AllowAnonymous]
[DevelopmentOnly]
public sealed class DevRefundsController : ControllerBase
{
    /// <summary>This refund fails on every provider try (plan section 6: <c>dev/refunds/{id}/simulate-fail</c>).</summary>
    [HttpPost("refunds/{id:guid}/simulate-fail")]
    public async Task<IActionResult> SimulateFail(Guid id, CancellationToken cancellationToken)
    {
        await HttpContext.RequestServices.GetRequiredService<MockRefunds>().FailAsync(id.ToString("N"), cancellationToken);
        return Ok(new { refundId = id, fails = true });
    }

    /// <summary>Every refund fails until turned off: a bank that refuses refunds.</summary>
    [HttpPost("mock-bank/refunds-fail")]
    public async Task<IActionResult> AllFail([FromQuery] bool fails, CancellationToken cancellationToken)
    {
        await HttpContext.RequestServices.GetRequiredService<MockRefunds>().SetAllFailAsync(fails, cancellationToken);
        return Ok(new { refundsFail = fails });
    }
}
