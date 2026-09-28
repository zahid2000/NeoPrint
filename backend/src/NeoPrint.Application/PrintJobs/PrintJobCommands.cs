using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// A kiosk places an order for a file on a flash drive (plan sections 7.3
/// screen 5, 9.1).
/// </summary>
/// <remarks>
/// The price comes from the kiosk's profile here, on the server, and is kept
/// as a snapshot; the kiosk shows a quote but never sets the price.
/// </remarks>
/// <param name="SkipPayment">Development only: queue it without a payment (plan step 3).</param>
public sealed record CreateKioskPrintJobCommand(
    Guid DeviceId,
    string? LocalFileName,
    int PageCount,
    PrintOptions Options,
    bool SkipPayment) : IRequest<PrintJobResponse>;

/// <summary>The kiosk looks up one of its own jobs.</summary>
public sealed record GetKioskPrintJobQuery(Guid DeviceId, Guid JobId) : IRequest<PrintJobResponse>;

/// <summary>The cancel screen: a job code typed at this kiosk (plan section 7.3, screen 11).</summary>
public sealed record FindKioskPrintJobByCodeQuery(Guid DeviceId, string JobCode) : IRequest<PrintJobResponse>;

/// <summary>The customer cancels at the kiosk.</summary>
public sealed record CancelKioskPrintJobCommand(Guid DeviceId, Guid JobId) : IRequest<PrintJobResponse>;

/// <summary>
/// The kiosk reports how printing went (plan section 7.4): started, finished,
/// or stopped with a reason.
/// </summary>
/// <param name="Status">Printing, Completed or Interrupted.</param>
/// <param name="ErrorCode">For Interrupted: a message key such as <c>printer.outOfPaper</c>.</param>
public sealed record ReportPrintJobStatusCommand(
    Guid DeviceId,
    Guid JobId,
    string Status,
    int PrintedSides,
    int PrintedSheets,
    string? ErrorCode) : IRequest<PrintJobResponse>;

/// <summary>The kiosk's own queue, for the panel and for picking the next job.</summary>
public sealed record GetKioskQueueQuery(Guid DeviceId) : IRequest<IReadOnlyList<QueueEntry>>;

/// <summary>The admin page (plan section 8A.3, "Print job-lar").</summary>
public sealed record ListPrintJobsQuery(
    Guid AdminUserId,
    Guid? KioskId,
    IReadOnlyCollection<PrintJobStatus>? Statuses,
    DateTime? From,
    DateTime? To,
    string? JobCode,
    int Page,
    int PageSize) : IRequest<PrintJobPage>;

/// <summary>One job with its history, for the admin.</summary>
public sealed record GetPrintJobDetailQuery(Guid AdminUserId, Guid JobId) : IRequest<PrintJobDetail>;

/// <summary>A kiosk's live queue, for the admin (plan section 8A.3, kiosk detail → Queue).</summary>
public sealed record GetAdminKioskQueueQuery(Guid AdminUserId, Guid KioskId) : IRequest<IReadOnlyList<QueueEntry>>;

/// <summary>An admin takes a job out of the queue.</summary>
public sealed record AdminCancelPrintJobCommand(Guid AdminUserId, Guid JobId, string? Reason) : IRequest<PrintJobResponse>;

/// <summary>A job as the kiosk, the app and the panel see it.</summary>
public sealed record PrintJobResponse(
    Guid Id,
    string JobCode,
    Guid KioskId,
    string Status,
    string Source,
    string? FileName,
    int PageCount,
    int SelectedPages,
    PrintOptions Options,
    PriceView Price,
    int RequiredSheets,
    int? QueuePosition,
    int PrintedSides,
    int PrintedSheets,
    string? ErrorReason,
    string? CancelReason,
    bool PaymentSkipped,
    DateTime CreatedAt,
    DateTime? QueuedAt,
    DateTime? FinishedAt);

/// <summary>The price snapshot as shown: what was charged and how it was reached.</summary>
public sealed record PriceView(
    decimal Total,
    string Currency,
    decimal BasePrice,
    decimal VolumeDiscount,
    decimal DiscountPercent,
    bool MinimumApplied,
    int Sides,
    int Sheets,
    Guid ProfileId,
    string ProfileName,
    DateTime TakenAt);

/// <summary>A page of the admin list.</summary>
public sealed record PrintJobPage(IReadOnlyList<PrintJobListItem> Items, long Total, int Page, int PageSize);

/// <summary>A row of the admin list.</summary>
public sealed record PrintJobListItem(
    Guid Id,
    string JobCode,
    Guid KioskId,
    string? KioskCode,
    string Status,
    string Source,
    string? FileName,
    int SelectedPages,
    int Copies,
    decimal Total,
    string Currency,
    bool PaymentSkipped,
    DateTime CreatedAt,
    DateTime? FinishedAt);

/// <summary>A job and every status it went through.</summary>
public sealed record PrintJobDetail(PrintJobResponse Job, string? KioskCode, IReadOnlyList<PrintJobTransitionView> History);

/// <summary>One step of a job's history.</summary>
public sealed record PrintJobTransitionView(string From, string To, string ActorType, string? Reason, DateTime At);

/// <inheritdoc cref="CreateKioskPrintJobCommand" />
public sealed class CreateKioskPrintJobValidator : IValidator<CreateKioskPrintJobCommand>
{
    public IEnumerable<ValidationError> Validate(CreateKioskPrintJobCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PageCount < 1)
        {
            yield return new ValidationError("pageCount", "printJobs.pageCountInvalid");
        }

        if (request.LocalFileName is { Length: > 255 })
        {
            yield return new ValidationError("localFileName", "printJobs.fileNameTooLong");
        }

        if (request.Options is null)
        {
            yield return new ValidationError("options", "printJobs.optionsInvalid");
            yield break;
        }

        foreach (var (field, message) in request.Options.Problems(Math.Max(request.PageCount, 1)))
        {
            yield return new ValidationError(field, message);
        }
    }
}

/// <inheritdoc cref="ReportPrintJobStatusCommand" />
public sealed class ReportPrintJobStatusValidator : IValidator<ReportPrintJobStatusCommand>
{
    public static readonly IReadOnlyList<string> Reportable = ["Printing", "Completed", "Interrupted"];

    public IEnumerable<ValidationError> Validate(ReportPrintJobStatusCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Reportable.Contains(request.Status))
        {
            yield return new ValidationError("status", "printJobs.statusNotReportable");
        }

        if (request.PrintedSides < 0 || request.PrintedSheets < 0)
        {
            yield return new ValidationError("printedSheets", "printJobs.countsInvalid");
        }

        if (request.ErrorCode is { } code && !Domain.Common.UserMessage.LooksLikeKey(code))
        {
            yield return new ValidationError("errorCode", "printJobs.errorCodeInvalid");
        }
    }
}
