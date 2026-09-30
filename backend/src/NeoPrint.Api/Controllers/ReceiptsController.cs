using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Receipts;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>The account's receipts in the app (plan section 6, "Receipts").</summary>
[ApiController]
[Route("api/receipts")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class ReceiptsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public ReceiptsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReceiptView>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListMyReceiptsQuery(UserId), cancellationToken));

    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken cancellationToken)
    {
        var pdf = await _sender.Send(new GetReceiptPdfQuery(id, UserId), cancellationToken);
        return File(pdf.Content, "application/pdf", pdf.FileName);
    }

    /// <summary>To the account's confirmed address, or to <c>email</c> when given.</summary>
    [HttpPost("{id:guid}/email")]
    [Idempotent]
    public async Task<ActionResult<ReceiptView>> Email(Guid id, [FromBody] ReceiptEmailRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new EmailReceiptCommand(id, request?.Email, DeviceId: null, UserId), cancellationToken));
}

public sealed record ReceiptEmailRequest(string? Email);

/// <summary>
/// "Qəbzi email-ə göndər" on the kiosk's receipt screen (plan section 7.3,
/// screen 9): the customer standing there types an address. Only this kiosk's
/// receipts, and only for a short while after paying.
/// </summary>
[ApiController]
[Route("api/kiosks/me/receipts")]
[Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class KioskReceiptsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public KioskReceiptsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <param name="id">The receipt's id: the payment's id.</param>
    [HttpPost("{id:guid}/email")]
    [Idempotent]
    public async Task<ActionResult<ReceiptView>> Email(Guid id, [FromBody] ReceiptEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new EmailReceiptCommand(id, request.Email, _currentUser.DeviceId ?? throw new UnauthorizedAccessException(), UserId: null),
            cancellationToken));
    }
}

/// <summary>"Qəbzlər" in the admin panel (plan section 6: <c>GET admin/receipts</c>).</summary>
[ApiController]
[Route("api/admin/receipts")]
public sealed class AdminReceiptsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminReceiptsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpGet]
    [HasPermission(Permissions.ReceiptsView)]
    public async Task<ActionResult<ReceiptPage>> List(
        [FromQuery] string? number, [FromQuery] Guid? paymentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListReceiptsQuery(number, paymentId, page, pageSize), cancellationToken));

    [HttpGet("{id:guid}/pdf")]
    [HasPermission(Permissions.ReceiptsView)]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken cancellationToken)
    {
        var pdf = await _sender.Send(new GetReceiptPdfQuery(id, UserId: null), cancellationToken);
        return File(pdf.Content, "application/pdf", pdf.FileName);
    }

    /// <summary>Sends it again to the addresses it already went to; the admin cannot add one.</summary>
    [HttpPost("{id:guid}/resend")]
    [Idempotent]
    [HasPermission(Permissions.ReceiptsView)]
    public async Task<ActionResult<ReceiptView>> Resend(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new AdminResendReceiptCommand(_currentUser.AdminUserId ?? throw new UnauthorizedAccessException(), id, _currentUser.Ip),
            cancellationToken));
}
