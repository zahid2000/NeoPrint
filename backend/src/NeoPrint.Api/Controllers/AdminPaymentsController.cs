using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Money in the admin panel (plan section 8A.3): payments, vouchers, the
/// revenue report and the dashboard's money card. Refunds, fiscal receipts and
/// receipts have their own controllers.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminPaymentsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminPaymentsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    /// <summary>Newest first, narrowed to the admin's kiosks; <c>refund</c> is <c>pending</c> or <c>failed</c>.</summary>
    [HttpGet("payments")]
    [HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<AdminPaymentPage>> List(
        [FromQuery(Name = "status")] string[]? statuses,
        [FromQuery(Name = "method")] string[]? methods,
        [FromQuery] string? channel,
        [FromQuery] string? purpose,
        [FromQuery] Guid? kioskId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] bool? hadError,
        [FromQuery] string? refund,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(
            new ListPaymentsQuery(AdminId, statuses, methods, channel, purpose, kioskId, from, to, hadError, refund, search, page, pageSize),
            cancellationToken));

    [HttpGet("payments/{id:guid}")]
    [HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<AdminPaymentDetail>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetAdminPaymentQuery(AdminId, id), cancellationToken));

    [HttpGet("vouchers")]
    [HasPermission(Permissions.VouchersView)]
    public async Task<ActionResult<VoucherPage>> Vouchers(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListVouchersQuery(page, pageSize), cancellationToken));

    /// <summary>Baku days, both included; at most a year.</summary>
    [HttpGet("reports/revenue")]
    [HasPermission(Permissions.ReportsView)]
    public async Task<ActionResult<RevenueReport>> Revenue(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? kioskId, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetRevenueQuery(AdminId, from, to, kioskId), cancellationToken));

    /// <summary>Today, the last seven days and the money problems waiting for an admin.</summary>
    [HttpGet("dashboard/payments")]
    [HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<PaymentsDashboard>> Dashboard(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPaymentsDashboardQuery(AdminId), cancellationToken));
}
