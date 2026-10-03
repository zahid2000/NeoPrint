using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.PrintJobs;

/// <summary>The <c>prepared_prints</c> collection: unpaid preparations (<see cref="PreparedPrint"/>).</summary>
public interface IPreparedPrintRepository
{
    Task AddAsync(PreparedPrint prepared, CancellationToken cancellationToken = default);

    Task<PreparedPrint?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(PreparedPrint prepared, CancellationToken cancellationToken = default);

    /// <summary>The user's preparations not taken off the list, newest first.</summary>
    Task<IReadOnlyList<PreparedPrint>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>"Kiosk üçün hazırla" without paying (plan section 9.4, step 1): the file and the options chosen so far.</summary>
public sealed record CreatePreparedPrintCommand(Guid UserId, Guid FileId, PreparedOptions Options) : IRequest<MyPreparedPrintView>;

/// <summary>Takes an unpaid preparation off the list.</summary>
public sealed record RemovePreparedPrintCommand(Guid UserId, Guid PreparedPrintId) : IRequest;

/// <summary>What the user got ready for a kiosk: unpaid preparations and orders paid for later.</summary>
public sealed record ListMyPreparedPrintsQuery(Guid UserId) : IRequest<IReadOnlyList<MyPreparedPrintView>>;

/// <summary>
/// "Kiosk üçün hazırla" paid in advance at a chosen kiosk (<c>docs/DECISIONS.md</c>
/// T-144): an order at that kiosk priced by its current profile, paid in the
/// app (<c>payments/{id}/pay-with-app</c>) and held until its owner shows
/// their QR there. <paramref name="ExpectedAmount"/> is the price the app
/// showed; when the profile changed since, the order is refused with
/// <c>printJobs.priceChanged</c> and the app asks again with the new one.
/// </summary>
public sealed record CreatePaidPreparationCommand(
    Guid UserId, string Kiosk, Guid FileId, PrintOptions Options, decimal ExpectedAmount) : IRequest<SessionPrintJobResponse>;

/// <summary>The owner cancels an order paid for later; the money goes back to the wallet.</summary>
public sealed record CancelHeldPrintJobCommand(Guid UserId, Guid JobId) : IRequest;

/// <summary>Cancels orders paid for later that nobody collected within <c>PreparedPrint.AutoCancelDays</c>.</summary>
public sealed record CancelUncollectedPrintJobsCommand : IRequest<int>;

/// <param name="Kind"><c>Prepared</c> (unpaid, any kiosk) or <c>Paid</c> (one kiosk).</param>
/// <param name="Preset">What a <c>Prepared</c> one fixes; a null field is chosen at the kiosk.</param>
/// <param name="Options">What a <c>Paid</c> one prints.</param>
/// <param name="State">
/// <c>Ready</c>; <c>Printing</c> or <c>Printed</c> once a kiosk took it; <c>FileUnavailable</c> when the file
/// is gone or not ready; <c>Paid</c> for one waiting at its kiosk.
/// </param>
/// <param name="CollectBy">A paid one is cancelled and refunded to the wallet after this.</param>
public sealed record MyPreparedPrintView(
    string Kind,
    Guid Id,
    Guid FileId,
    string FileName,
    int Pages,
    PreparedOptions? Preset,
    PrintOptions? Options,
    string State,
    Guid? KioskId,
    string? KioskCode,
    string? KioskName,
    decimal? Amount,
    string? Currency,
    string? JobCode,
    DateTime CreatedAt,
    DateTime? CollectBy);

/// <inheritdoc cref="CreatePreparedPrintCommand" />
public sealed class CreatePreparedPrintValidator : IValidator<CreatePreparedPrintCommand>
{
    public IEnumerable<ValidationError> Validate(CreatePreparedPrintCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The range needs the page count; it is checked against the file in the handler.
        foreach (var (field, message) in (request.Options ?? PreparedOptions.None).ProblemsBesidesRange())
        {
            yield return new ValidationError(field, message);
        }
    }
}

/// <inheritdoc cref="CreatePaidPreparationCommand" />
public sealed class CreatePaidPreparationValidator : IValidator<CreatePaidPreparationCommand>
{
    public IEnumerable<ValidationError> Validate(CreatePaidPreparationCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Kiosk) || request.Kiosk.Trim().Length > 64)
        {
            yield return new ValidationError("kiosk", "sessions.kioskRequired");
        }

        if (request.ExpectedAmount <= 0)
        {
            yield return new ValidationError("expectedAmount", "printJobs.expectedAmountRequired");
        }
    }
}

public sealed class PreparedPrintHandlers :
    IRequestHandler<CreatePreparedPrintCommand, MyPreparedPrintView>,
    IRequestHandler<RemovePreparedPrintCommand>,
    IRequestHandler<ListMyPreparedPrintsQuery, IReadOnlyList<MyPreparedPrintView>>,
    IRequestHandler<CreatePaidPreparationCommand, SessionPrintJobResponse>,
    IRequestHandler<CancelHeldPrintJobCommand>,
    IRequestHandler<CancelUncollectedPrintJobsCommand, int>
{
    /// <summary>Uncollected orders cancelled per run; the next run takes the rest.</summary>
    private const int CancelBatch = 100;

    /// <summary>The reason an uncollected order is cancelled with; the refund module maps it to its own reason.</summary>
    public const string NotCollectedReason = "printJobs.reason.notCollected";

    private readonly IPreparedPrintRepository _prepared;
    private readonly IFileRepository _files;
    private readonly IKioskRepository _kiosks;
    private readonly IPrintJobRepository _jobs;
    private readonly AppPaidOrders _orders;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _notifier;
    private readonly IEventPublisher _events;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<PreparedPrintHandlers> _logger;

    public PreparedPrintHandlers(
        IPreparedPrintRepository prepared,
        IFileRepository files,
        IKioskRepository kiosks,
        IPrintJobRepository jobs,
        AppPaidOrders orders,
        PrintQueueService queue,
        IKioskNotifier notifier,
        IEventPublisher events,
        ISettingsReader settings,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<PreparedPrintHandlers> logger)
    {
        _prepared = prepared;
        _files = files;
        _kiosks = kiosks;
        _jobs = jobs;
        _orders = orders;
        _queue = queue;
        _notifier = notifier;
        _events = events;
        _settings = settings;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<MyPreparedPrintView> Handle(CreatePreparedPrintCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await _orders.ReadyFileAsync(request.UserId, request.FileId, cancellationToken).ConfigureAwait(false);
        var options = request.Options ?? PreparedOptions.None;

        if (options.Problems(file.PageCount) is [var (field, message), ..])
        {
            throw new ValidationException([new ValidationError(field, message)]);
        }

        var prepared = PreparedPrint.Create(request.UserId, file.Id, options, _clock.UtcNow);
        await _prepared.AddAsync(prepared, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("File {FileId} prepared for a kiosk as {PreparedPrintId}.", file.Id, prepared.Id);

        return PreparedView(prepared, file, lastJob: null);
    }

    public async Task Handle(RemovePreparedPrintCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = await _prepared.FindByIdAsync(request.PreparedPrintId, cancellationToken).ConfigureAwait(false);

        if (prepared is null || prepared.UserId != request.UserId)
        {
            throw new NotFoundException("preparedPrint");
        }

        prepared.Remove(_clock.UtcNow);
        await _prepared.UpdateAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MyPreparedPrintView>> Handle(ListMyPreparedPrintsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = await _prepared.ListForUserAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var lastJobs = (await _jobs
                .ListByIdsAsync(prepared.Where(p => p.LastJobId is not null).Select(p => p.LastJobId!.Value).ToList(), cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(job => job.Id);
        var held = await _jobs.ListHeldForUserAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var days = await AutoCancelDaysAsync(cancellationToken).ConfigureAwait(false);

        var views = new List<MyPreparedPrintView>();

        foreach (var item in prepared)
        {
            var file = await _files.FindByIdAsync(item.FileId, cancellationToken).ConfigureAwait(false);
            views.Add(PreparedView(item, file, item.LastJobId is { } jobId ? lastJobs.GetValueOrDefault(jobId) : null));
        }

        foreach (var job in held)
        {
            var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
            views.Add(new MyPreparedPrintView(
                "Paid",
                job.Id,
                job.FileId ?? Guid.Empty,
                job.LocalFileName ?? string.Empty,
                job.PageCount,
                Preset: null,
                job.Options,
                "Paid",
                job.KioskId,
                kiosk?.Code,
                kiosk?.Name,
                job.Price.Total.Amount,
                job.Price.Total.Currency,
                job.JobCode,
                job.CreatedAt,
                job.PaidAt?.AddDays(days)));
        }

        return views.OrderByDescending(view => view.CreatedAt).ToList();
    }

    public async Task<SessionPrintJobResponse> Handle(CreatePaidPreparationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _orders.ChoosableKioskAsync(request.Kiosk, cancellationToken).ConfigureAwait(false);
        var file = await _orders.ReadyFileAsync(request.UserId, request.FileId, cancellationToken).ConfigureAwait(false);
        var (job, payment) = await _orders
            .PlaceAsync(request.UserId, kiosk, file, request.Options, request.ExpectedAmount, PrintJobSource.UserQr, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Paid preparation {JobId} placed at kiosk {KioskId} for file {FileId}, payment {PaymentId}.",
            job.Id,
            kiosk.Id,
            file.Id,
            payment.Id);

        return AppPaidOrders.Response(job, payment);
    }

    public async Task Handle(CancelHeldPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        // A scheduled print is cancelled through its schedule, which keeps its own status.
        if (job is null || job.UserId != request.UserId || !job.HeldForUser || job.Source == PrintJobSource.Scheduled)
        {
            throw new NotFoundException("printJob");
        }

        if (job.Status == PrintJobStatus.Cancelled)
        {
            return;
        }

        if (!job.IsHeld)
        {
            // Collected (queued or printed) or never paid: not this button's.
            throw new DomainException("printJobs.notHeld");
        }

        await PrintJobCancellation
            .CancelAsync(
                job,
                new UserMessage("printJobs.reason.cancelledByOwner"),
                ActorContext.User(request.UserId, correlationId: _correlation.CorrelationId),
                _clock.UtcNow,
                _jobs,
                _queue,
                _notifier,
                _events,
                _correlation,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Paid preparation {JobId} cancelled by its owner; the money goes back.", job.Id);
    }

    public async Task<int> Handle(CancelUncollectedPrintJobsCommand request, CancellationToken cancellationToken)
    {
        var days = await AutoCancelDaysAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var due = await _jobs.ListHeldPaidBeforeAsync(now.AddDays(-days), CancelBatch, cancellationToken).ConfigureAwait(false);
        var cancelled = 0;

        foreach (var job in due)
        {
            try
            {
                await PrintJobCancellation
                    .CancelAsync(
                        job,
                        new UserMessage(NotCollectedReason),
                        ActorContext.System(correlationId: _correlation.CorrelationId),
                        now,
                        _jobs,
                        _queue,
                        _notifier,
                        _events,
                        _correlation,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancelled++;
            }
            catch (ConcurrencyException)
            {
                // Collected or cancelled at this moment by someone else: theirs.
                _logger.LogInformation("Uncollected order {JobId} changed while being cancelled; left as it is.", job.Id);
            }
        }

        if (cancelled > 0)
        {
            _logger.LogInformation("{Count} order(s) paid in the app and not collected in {Days} days were cancelled.", cancelled, days);
        }

        return cancelled;
    }

    private async Task<int> AutoCancelDaysAsync(CancellationToken cancellationToken)
        => Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.PreparedPrintAutoCancelDays, 7, cancellationToken).ConfigureAwait(false), 1, 90);

    internal static MyPreparedPrintView PreparedView(PreparedPrint prepared, StoredFile? file, PrintJob? lastJob)
    {
        var usable = file is { Status: FileStatus.Ready, Pdf: not null, DeletedAt: null };
        var state = !usable
            ? "FileUnavailable"
            : lastJob?.Status switch
            {
                PrintJobStatus.Queued or PrintJobStatus.Printing => "Printing",
                PrintJobStatus.Completed => "Printed",
                _ => "Ready",
            };

        return new MyPreparedPrintView(
            "Prepared",
            prepared.Id,
            prepared.FileId,
            file?.FileName ?? string.Empty,
            file?.PageCount ?? 0,
            prepared.Options,
            Options: null,
            state,
            KioskId: null,
            KioskCode: null,
            KioskName: null,
            Amount: null,
            Currency: null,
            JobCode: null,
            prepared.CreatedAt,
            CollectBy: null);
    }
}
