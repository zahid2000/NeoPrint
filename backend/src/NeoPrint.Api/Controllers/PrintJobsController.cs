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
                request.Dev,
                request.PromoCode),
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
    /// What goes back for this job (RefundInfo, plan section 7.3): amount,
    /// destination and the status page's address for the QR.
    /// </summary>
    [HttpGet("{id:guid}/refunds")]
    public async Task<ActionResult<IReadOnlyList<Application.Refunds.KioskRefundView>>> Refunds(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new Application.Refunds.GetKioskJobRefundsQuery(DeviceId, id), cancellationToken));

    /// <summary>
    /// A flash order paid with the app: the file off the drive joins the
    /// account's files (plan section 9.2, step 5). The same job again returns
    /// the file already kept.
    /// </summary>
    [HttpPost("{id:guid}/flash-upload")]
    [RequestSizeLimit(200L * 1024 * 1024)]
    public async Task<ActionResult<FlashUploadResult>> FlashUpload(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new Application.Common.ValidationException([new Application.Common.ValidationError("file", "files.noFileSent")]);
        }

        await using var content = file.OpenReadStream();

        return Ok(await _sender.Send(new FlashUploadCommand(DeviceId, id, file.FileName, content, file.Length), cancellationToken));
    }

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
                DeviceId, id, request.Status ?? string.Empty, request.PrintedSides, request.PrintedSheets, request.ErrorCode, request.Attempt),
            cancellationToken));
    }
}

/// <param name="Dev">Development only: queue without a payment (plan step 3). Refused elsewhere.</param>
/// <param name="PromoCode">Optional; one per order (plan section 2.7).</param>
public sealed record CreatePrintJobRequest(string? LocalFileName, int PageCount, PrintOptions? Options, bool Dev = false, string? PromoCode = null);

/// <param name="Attempt">The try this report belongs to (plan section 2.5, <c>print_attempts</c>).</param>
public sealed record PrintJobStatusRequest(
    string? Status, int PrintedSides, int PrintedSheets, string? ErrorCode, PrintAttemptInput? Attempt = null);

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

    /// <summary>"Baxış gözləyən job-lar": suspected jobs, the longest-waiting first (plan section 8A.3).</summary>
    [HttpGet("print-jobs/suspected")]
    [HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<IReadOnlyList<SuspectedJobItem>>> Suspected(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListSuspectedJobsQuery(AdminId), cancellationToken));

    /// <summary>The investigation card: the job, its payment, the kiosk around that time and the customer's claims.</summary>
    [HttpGet("print-jobs/{id:guid}/investigation")]
    [HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<SuspectedJobInvestigation>> Investigation(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetSuspectedJobQuery(AdminId, id), cancellationToken));

    /// <summary>
    /// The decision on a suspected job: <c>FullRefund</c>, <c>PartialRefund</c>
    /// (with an amount), <c>NoRefund</c> or <c>Reprint</c>; reason and note are
    /// mandatory and audited. A money decision, so it needs <c>payments.refund</c>.
    /// </summary>
    [HttpPost("print-jobs/{id:guid}/resolve")]
    [Idempotent]
    [HasPermission(Permissions.PaymentsRefund)]
    public async Task<ActionResult<SuspectedJobResolution>> Resolve(
        Guid id, [FromBody] ResolveSuspectedJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new ResolveSuspectedJobCommand(
                AdminId,
                id,
                request.Decision,
                request.Amount,
                request.Destination,
                request.Reason,
                request.Note,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken));
    }
}

public sealed record AdminCancelRequest(string? Reason);

public sealed record ResolveSuspectedJobRequest(string? Decision, decimal? Amount, string? Destination, string? Reason, string? Note);
