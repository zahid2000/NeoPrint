using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Fiscal;
using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Fiscal;

namespace NeoPrint.Api.Controllers;

/// <summary>"Fiskal çeklər" in the admin panel (plan section 6, "Admin Fiscal").</summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminFiscalController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminFiscalController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    [HttpGet("fiscal-receipts")]
    [HasPermission(Permissions.FiscalView)]
    public async Task<ActionResult<FiscalReceiptPage>> List(
        [FromQuery] string[]? status,
        [FromQuery] string[]? type,
        [FromQuery] Guid? paymentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListFiscalReceiptsQuery(_currentUser.AdminUserId ?? throw new UnauthorizedAccessException(), status, type, paymentId, page, pageSize), cancellationToken));

    [HttpGet("fiscal-receipts/{id:guid}")]
    [KioskScoped(RecordKind.FiscalReceipt)]
    [HasPermission(Permissions.FiscalView)]
    public async Task<ActionResult<FiscalReceiptView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetFiscalReceiptQuery(id), cancellationToken));

    /// <summary>"Təkrarla": a failed or waiting receipt is tried now, with a fresh count.</summary>
    [HttpPost("fiscal-receipts/{id:guid}/retry")]
    [KioskScoped(RecordKind.FiscalReceipt)]
    [Idempotent]
    [HasPermission(Permissions.FiscalManage)]
    public async Task<ActionResult<FiscalReceiptView>> Retry(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new AdminRetryFiscalReceiptCommand(AdminId, id, _currentUser.Ip), cancellationToken);
        await _sender.Send(new IssueFiscalReceiptCommand(id), cancellationToken);

        return Ok(await _sender.Send(new GetFiscalReceiptQuery(id), cancellationToken));
    }

    [HttpGet("fiscal/settings")]
    [HasPermission(Permissions.FiscalManage)]
    public async Task<ActionResult<IReadOnlyList<SettingResponse>>> Settings(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetFiscalSettingsQuery(), cancellationToken));

    [HttpPut("fiscal/settings")]
    [HasPermission(Permissions.FiscalManage)]
    public async Task<ActionResult<IReadOnlyList<SettingResponse>>> UpdateSettings(
        [FromBody] FiscalSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new UpdateFiscalSettingsCommand(request.Values ?? new Dictionary<string, string>(), AdminId, _currentUser.Ip), cancellationToken));
    }
}

public sealed record FiscalSettingsRequest(IReadOnlyDictionary<string, string>? Values);

/// <summary>An e-kassa that fails on purpose, in Development (plan section 6, "Dev").</summary>
[ApiController]
[Route("api/dev")]
[AllowAnonymous]
[DevelopmentOnly]
public sealed class DevFiscalController : ControllerBase
{
    /// <summary>This receipt times out on every try (plan section 6: <c>dev/fiscal/{id}/simulate-fail</c>); <c>?fails=false</c> lets it through.</summary>
    [HttpPost("fiscal/{id:guid}/simulate-fail")]
    public async Task<IActionResult> SimulateFail(Guid id, [FromQuery] bool fails = true, CancellationToken cancellationToken = default)
    {
        await Mock.FailAsync(id.ToString("N"), fails, cancellationToken);
        return Ok(new { receiptId = id, fails });
    }

    /// <summary>The whole e-kassa: <c>Ok</c>, <c>Down</c> (timeouts) or <c>Reject</c>.</summary>
    [HttpPost("mock-fiscal/mode")]
    public async Task<IActionResult> Mode([FromQuery] MockFiscalMode mode, CancellationToken cancellationToken)
    {
        await Mock.SetModeAsync(mode, cancellationToken);
        return Ok(new { mode = mode.ToString() });
    }

    private MockFiscalProvider Mock => HttpContext.RequestServices.GetRequiredService<MockFiscalProvider>();
}
