using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.UserQr;
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

    /// <summary>The grant a scanned personal QR gave this kiosk.</summary>
    public const string GrantHeader = "X-User-Qr-Grant";

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

    /// <summary>
    /// The PDF of a paid order placed in the app (plan section 7.4): the kiosk
    /// keeps it in its encrypted spool and prints from there.
    /// </summary>
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> PrintFile(Guid id, CancellationToken cancellationToken)
    {
        var file = await _sender.Send(new GetKioskPrintFileQuery(DeviceId, id), cancellationToken);
        return File(file.Content, "application/pdf", file.FileName);
    }

    /// <summary>
    /// The QR scanner read a personal QR (plan section 9.4, step 2): the
    /// user's files for kiosks and a grant (<c>X-User-Qr-Grant</c>) for this
    /// kiosk to act on them for a few minutes. POST, not GET: the code must
    /// not end up in an address that gets logged.
    /// </summary>
    [HttpPost("pending-for-user")]
    public async Task<ActionResult<UserQrFilesView>> ScanUserQr([FromBody] ScanUserQrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await _sender.Send(new ScanUserQrCommand(DeviceId, request.Code ?? string.Empty), cancellationToken));
    }

    /// <summary>The same list again while the grant lasts; using it extends it.</summary>
    [HttpGet("pending-for-user")]
    public async Task<ActionResult<UserQrFilesView>> UserQrFiles(
        [FromHeader(Name = GrantHeader)] Guid grant, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetUserQrFilesQuery(DeviceId, grant), cancellationToken));

    /// <summary>
    /// An unpaid preparation picked: an order at this kiosk with the options
    /// chosen on its screen, paid as any kiosk order. Idempotent.
    /// </summary>
    [HttpPost("pending-for-user/prepared/{id:guid}")]
    [Idempotent]
    public async Task<ActionResult<PrintJobResponse>> OrderPrepared(
        Guid id, [FromHeader(Name = GrantHeader)] Guid grant, [FromBody] OrderPreparedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new OrderPreparedPrintCommand(DeviceId, grant, id, request.Options ?? PrintOptions.Default, request.PromoCode),
            cancellationToken));
    }

    /// <summary>An order paid in the app for this kiosk, picked: into the queue. Picking it again changes nothing.</summary>
    [HttpPost("pending-for-user/paid/{jobId:guid}/release")]
    public async Task<ActionResult<PrintJobResponse>> ReleaseHeld(
        Guid jobId, [FromHeader(Name = GrantHeader)] Guid grant, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ReleaseHeldPrintJobCommand(DeviceId, grant, jobId), cancellationToken));

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
public sealed record ScanUserQrRequest(string? Code);

public sealed record OrderPreparedRequest(PrintOptions? Options, string? PromoCode);

public sealed record CreatePrintJobRequest(string? LocalFileName, int PageCount, PrintOptions? Options, bool Dev = false, string? PromoCode = null);

/// <param name="Attempt">The try this report belongs to (plan section 2.5, <c>print_attempts</c>).</param>
public sealed record PrintJobStatusRequest(
    string? Status, int PrintedSides, int PrintedSheets, string? ErrorCode, PrintAttemptInput? Attempt = null);

/// <summary>
/// The customer's own jobs in the app (plan section 6: <c>GET print-jobs/my</c>).
/// </summary>
/// <remarks>
/// Its own class because the kiosk controller above takes kiosk tokens only;
/// "my" never matches the kiosk's <c>{id:guid}</c> routes.
/// </remarks>
[ApiController]
[Route("api/print-jobs")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class MyPrintJobsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public MyPrintJobsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpGet("my")]
    public async Task<ActionResult<IReadOnlyList<MyPrintJobView>>> Mine(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListMyPrintJobsQuery(_currentUser.UserId ?? throw new UnauthorizedAccessException()), cancellationToken));
}

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
        => Ok(await _sender.Send(
            new AdminCancelPrintJobCommand(AdminId, id, request?.Reason, _currentUser.Ip),
            cancellationToken));

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
