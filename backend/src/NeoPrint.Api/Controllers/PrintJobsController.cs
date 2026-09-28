using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Print jobs as the kiosk places and reports them (plan section 6,
/// "PrintJobs"; step 3).
/// </summary>
/// <remarks>
/// Every endpoint needs a kiosk token and acts on that kiosk's jobs only; a
/// job of another kiosk reads as not found. Mobile jobs arrive with steps 4–5.
/// </remarks>
[ApiController]
[Route("api/print-jobs")]
[Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class PrintJobsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public PrintJobsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid DeviceId => _currentUser.DeviceId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Places an order for a file on the kiosk's flash drive. The server
    /// prices it from the kiosk's profile and keeps that price.
    /// </summary>
    /// <remarks>
    /// Idempotent: the kiosk sends one key per order, so a retry after a lost
    /// response returns the same job rather than a second one.
    /// </remarks>
    [HttpPost]
    [Idempotent]
    public async Task<ActionResult<PrintJobResponse>> Create(
        [FromBody] CreatePrintJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _sender.Send(
            new CreateKioskPrintJobCommand(
                DeviceId,
                request.LocalFileName,
                request.PageCount,
                request.Options ?? PrintOptions.Default,
                request.Dev),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PrintJobResponse>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskPrintJobQuery(DeviceId, id), cancellationToken));

    /// <summary>The cancel screen: a job code typed at this kiosk (plan section 7.3, screen 11).</summary>
    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<PrintJobResponse>> ByCode(string code, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new FindKioskPrintJobByCodeQuery(DeviceId, code), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Idempotent]
    public async Task<ActionResult<PrintJobResponse>> Cancel(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new CancelKioskPrintJobCommand(DeviceId, id), cancellationToken));

    /// <summary>
    /// How printing went: Printing, Completed or Interrupted, with what the
    /// printer reported (plan section 7.4). Repeating a report changes nothing.
    /// </summary>
    [HttpPost("{id:guid}/status")]
    public async Task<ActionResult<PrintJobResponse>> Status(
        Guid id, [FromBody] PrintJobStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new ReportPrintJobStatusCommand(
                DeviceId, id, request.Status ?? string.Empty, request.PrintedSides, request.PrintedSheets, request.ErrorCode),
            cancellationToken));
    }
}

/// <param name="Dev">Development only: queue without a payment (plan step 3). Refused elsewhere.</param>
public sealed record CreatePrintJobRequest(string? LocalFileName, int PageCount, PrintOptions? Options, bool Dev = false);

public sealed record PrintJobStatusRequest(string? Status, int PrintedSides, int PrintedSheets, string? ErrorCode);

/// <summary>
/// Print jobs in the admin panel (plan sections 6 "Admin Queue / Jobs", 8A.3).
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminPrintJobsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminPrintJobsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    /// <summary>The "Print job-lar" page: filters by kiosk, status, time and code, newest first.</summary>
    [HttpGet("print-jobs")]
    [HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<PrintJobPage>> List(
        [FromQuery] Guid? kioskId,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? code,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var statuses = status?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<PrintJobStatus>(value, ignoreCase: true, out var parsed) ? parsed : (PrintJobStatus?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToList();

        return Ok(await _sender.Send(
            new ListPrintJobsQuery(AdminId, kioskId, statuses, from?.ToUniversalTime(), to?.ToUniversalTime(), code, page, pageSize),
            cancellationToken));
    }

    [HttpGet("print-jobs/{id:guid}")]
    [HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<PrintJobDetail>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPrintJobDetailQuery(AdminId, id), cancellationToken));

    /// <summary>Takes a job out of the queue. A job at the printer cannot be taken back.</summary>
    [HttpPost("print-jobs/{id:guid}/cancel")]
    [Idempotent]
    [HasPermission(Permissions.JobsCancel)]
    public async Task<ActionResult<PrintJobResponse>> Cancel(
        Guid id, [FromBody] AdminCancelRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new AdminCancelPrintJobCommand(AdminId, id, request?.Reason), cancellationToken));

    /// <summary>The kiosk's live queue (kiosk detail → Queue).</summary>
    [HttpGet("kiosks/{kioskId:guid}/queue")]
    [HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<IReadOnlyList<QueueEntry>>> Queue(Guid kioskId, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetAdminKioskQueueQuery(AdminId, kioskId), cancellationToken));
}

public sealed record AdminCancelRequest(string? Reason);
