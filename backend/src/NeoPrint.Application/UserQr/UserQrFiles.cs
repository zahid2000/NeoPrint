using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.UserQr;

/// <summary>
/// What a kiosk may do for the user whose QR it just read (<c>docs/DECISIONS.md</c>
/// T-144): this kiosk, this user, a few minutes, extended while in use.
/// </summary>
public sealed record UserQrGrant(Guid Id, Guid UserId, Guid KioskId, DateTime ExpiresAt);

/// <summary>Where grants live, with their time to live (Redis).</summary>
public interface IUserQrGrantStore
{
    Task SaveAsync(UserQrGrant grant, CancellationToken cancellationToken = default);

    /// <summary>The grant while it lasts; null once it expired.</summary>
    Task<UserQrGrant?> FindAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The kiosk's QR scanner read <c>NPU:...</c> (plan section 9.4, step 2): the
/// HMAC and its age are checked and the kiosk gets the user's files for
/// kiosks and a grant to act on them.
/// </summary>
public sealed record ScanUserQrCommand(Guid DeviceId, string Code) : IRequest<UserQrFilesView>;

/// <summary>The same list again (<c>GET print-jobs/pending-for-user</c>), for a grant still open.</summary>
public sealed record GetUserQrFilesQuery(Guid DeviceId, Guid Grant) : IRequest<UserQrFilesView>;

/// <summary>
/// An unpaid preparation picked at the kiosk (plan section 9.4, step 3): an
/// order at this kiosk priced by its current profile, paid on the kiosk's own
/// payment screens (9.1 / 9.2).
/// </summary>
public sealed record OrderPreparedPrintCommand(Guid DeviceId, Guid Grant, Guid PreparedPrintId, PrintOptions Options, string? PromoCode)
    : IRequest<PrintJobResponse>;

/// <summary>An order paid in the app for this kiosk, picked: into the queue.</summary>
public sealed record ReleaseHeldPrintJobCommand(Guid DeviceId, Guid Grant, Guid JobId) : IRequest<PrintJobResponse>;

public sealed record UserQrFilesView(Guid Grant, DateTime ExpiresAt, IReadOnlyList<UserQrItem> Items);

/// <param name="Kind"><c>Prepared</c> (unpaid) or <c>Paid</c>.</param>
/// <param name="Preset">A prepared one's choices; a null field is chosen here.</param>
/// <param name="Options">A paid one's options.</param>
/// <param name="Here">False for an order paid for another kiosk: shown, not printable here.</param>
/// <param name="KioskCode">The kiosk a paid order belongs to.</param>
public sealed record UserQrItem(
    string Kind,
    Guid Id,
    string FileName,
    int Pages,
    PreparedOptions? Preset,
    PrintOptions? Options,
    decimal? Amount,
    string? Currency,
    bool Here,
    string? KioskCode,
    DateTime CreatedAt);

public sealed class UserQrFileHandlers :
    IRequestHandler<ScanUserQrCommand, UserQrFilesView>,
    IRequestHandler<GetUserQrFilesQuery, UserQrFilesView>,
    IRequestHandler<OrderPreparedPrintCommand, PrintJobResponse>,
    IRequestHandler<ReleaseHeldPrintJobCommand, PrintJobResponse>
{
    /// <summary>How long a grant lasts without use; the kiosk screen closes after 60 seconds idle.</summary>
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(5);

    private readonly UserQrCodes _codes;
    private readonly IUserQrGrantStore _grants;
    private readonly IUserRepository _users;
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IPreparedPrintRepository _prepared;
    private readonly IPrintJobRepository _jobs;
    private readonly IFileRepository _files;
    private readonly PrintOrderRules _rules;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _notifier;
    private readonly IEventPublisher _events;
    private readonly Promos.PromoService _promos;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<UserQrFileHandlers> _logger;

    public UserQrFileHandlers(
        UserQrCodes codes,
        IUserQrGrantStore grants,
        IUserRepository users,
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IPreparedPrintRepository prepared,
        IPrintJobRepository jobs,
        IFileRepository files,
        PrintOrderRules rules,
        PrintQueueService queue,
        IKioskNotifier notifier,
        IEventPublisher events,
        Promos.PromoService promos,
        ISettingsReader settings,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<UserQrFileHandlers> logger)
    {
        _codes = codes;
        _grants = grants;
        _users = users;
        _devices = devices;
        _kiosks = kiosks;
        _prepared = prepared;
        _jobs = jobs;
        _files = files;
        _rules = rules;
        _queue = queue;
        _notifier = notifier;
        _events = events;
        _promos = promos;
        _settings = settings;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UserQrFilesView> Handle(ScanUserQrCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var rotation = await UserQrRules.RotationSecondsAsync(_settings, cancellationToken).ConfigureAwait(false);
        var (reading, userId) = _codes.Read(request.Code, now, rotation);

        if (reading == UserQrReading.Expired)
        {
            _logger.LogInformation("Kiosk {KioskId} read an expired personal QR.", kiosk.Id);
            throw new DomainException("userQr.expired");
        }

        var user = reading == UserQrReading.Valid
            ? await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false)
            : null;

        if (user is not { CanSignIn: true, DeletedAt: null })
        {
            // Never say whether the account exists: the screen is public.
            _logger.LogWarning("Kiosk {KioskId} read a personal QR that is not valid ({Reading}).", kiosk.Id, reading);
            throw new DomainException("userQr.invalid");
        }

        var grant = new UserQrGrant(Guid.NewGuid(), user.Id, kiosk.Id, now + GrantLifetime);
        await _grants.SaveAsync(grant, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Kiosk {KioskId} opened the files of user {UserId} by their QR.", kiosk.Id, user.Id);

        return await ViewAsync(grant, kiosk, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserQrFilesView> Handle(GetUserQrFilesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (grant, kiosk) = await UseAsync(request.DeviceId, request.Grant, cancellationToken).ConfigureAwait(false);
        return await ViewAsync(grant, kiosk, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrintJobResponse> Handle(OrderPreparedPrintCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);

        var (grant, kiosk) = await UseAsync(request.DeviceId, request.Grant, cancellationToken).ConfigureAwait(false);

        if (kiosk.Status != KioskStatus.Online)
        {
            throw new DomainException(UserMessage.Of("printJobs.kioskNotTakingOrders", ("status", kiosk.Status)));
        }

        var prepared = await _prepared.FindByIdAsync(request.PreparedPrintId, cancellationToken).ConfigureAwait(false);

        if (prepared is null || prepared.UserId != grant.UserId || prepared.IsRemoved)
        {
            throw new NotFoundException("preparedPrint");
        }

        var file = await _files.FindByIdAsync(prepared.FileId, cancellationToken).ConfigureAwait(false);

        if (file is not { Status: FileStatus.Ready, Pdf: { } pdf, DeletedAt: null } || file.UserId != grant.UserId)
        {
            throw new DomainException("preparedPrints.fileUnavailable");
        }

        var now = _clock.UtcNow;
        var price = await _rules.CheckAndPriceAsync(kiosk, file.PageCount, request.Options, now, cancellationToken).ConfigureAwait(false);

        Promos.PromoCheck? promo = null;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            // The account is known here: its own per-account limit applies (plan section 2.7).
            promo = await _promos.CheckAsync(request.PromoCode, kiosk.Id, price.ProfileId, price.Total, grant.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (promo.Refusal is { } refusal)
            {
                throw new ValidationException([new ValidationError("promoCode", refusal)]);
            }
        }

        var actor = ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId);
        var job = await _rules
            .AddWithUniqueCodeAsync(
                code => PrintJob.Create(
                    code,
                    kiosk.Id,
                    PrintJobSource.UserQr,
                    file.PageCount,
                    request.Options,
                    price,
                    now,
                    userId: grant.UserId,
                    fileId: file.Id,
                    localFileName: file.FileName,
                    printFile: pdf),
                cancellationToken)
            .ConfigureAwait(false);

        if (promo is { Promo: { } code })
        {
            job.AttachPromo(code.Id, code.Code, promo.Discount.Amount);
        }

        job.AwaitPayment(actor, now);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        prepared.Ordered(job.Id);
        await _prepared.UpdateAsync(prepared, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Kiosk {KioskId} placed order {JobId} from preparation {PreparedPrintId}: {Sheets} sheets.",
            kiosk.Id,
            job.Id,
            prepared.Id,
            job.RequiredSheets);

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrintJobResponse> Handle(ReleaseHeldPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (grant, kiosk) = await UseAsync(request.DeviceId, request.Grant, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is null || job.UserId != grant.UserId || !job.HeldForUser)
        {
            throw new NotFoundException("printJob");
        }

        if (job.KioskId != kiosk.Id)
        {
            var paidAt = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
            throw new DomainException(UserMessage.Of("printJobs.paidAtAnotherKiosk", ("kiosk", paidAt?.Code ?? string.Empty)));
        }

        if (job.Status is PrintJobStatus.Queued or PrintJobStatus.Printing)
        {
            // Picked twice (a retry after a lost answer): it is already in.
            return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
        }

        if (kiosk.Status != KioskStatus.Online)
        {
            throw new DomainException(UserMessage.Of("printJobs.kioskNotTakingOrders", ("status", kiosk.Status)));
        }

        // The printer or the paper may have changed since it was paid.
        await _rules.EnsureCanPrintAsync(kiosk, job, cancellationToken).ConfigureAwait(false);

        var now = _clock.UtcNow;
        job.Release(ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId), now);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        await _queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new Contracts.PrintJobQueued(job.Id, job.KioskId, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);
        await _notifier.JobAddedAsync(job.KioskId, job.Id, cancellationToken).ConfigureAwait(false);
        await _queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Order {JobId} paid in the app was collected at kiosk {KioskId}.", job.Id, kiosk.Id);

        return await _queue.ResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserQrFilesView> ViewAsync(UserQrGrant grant, Kiosk kiosk, CancellationToken cancellationToken)
    {
        var items = new List<UserQrItem>();

        foreach (var held in await _jobs.ListHeldForUserAsync(grant.UserId, cancellationToken).ConfigureAwait(false))
        {
            var here = held.KioskId == kiosk.Id;
            var at = here ? kiosk : await _kiosks.FindByIdAsync(held.KioskId, cancellationToken).ConfigureAwait(false);

            items.Add(new UserQrItem(
                "Paid",
                held.Id,
                held.LocalFileName ?? string.Empty,
                held.PageCount,
                Preset: null,
                held.Options,
                held.Price.Total.Amount,
                held.Price.Total.Currency,
                here,
                at?.Code,
                held.CreatedAt));
        }

        var prepared = await _prepared.ListForUserAsync(grant.UserId, cancellationToken).ConfigureAwait(false);
        var lastJobs = (await _jobs
                .ListByIdsAsync(prepared.Where(p => p.LastJobId is not null).Select(p => p.LastJobId!.Value).ToList(), cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(job => job.Id);

        foreach (var item in prepared)
        {
            var file = await _files.FindByIdAsync(item.FileId, cancellationToken).ConfigureAwait(false);
            var view = PreparedPrintHandlers.PreparedView(item, file, item.LastJobId is { } id ? lastJobs.GetValueOrDefault(id) : null);

            // Printed, printing or without its file: nothing to pick here.
            if (view.State == "Ready")
            {
                items.Add(new UserQrItem(
                    "Prepared", item.Id, view.FileName, view.Pages, item.Options, null, null, null, true, null, item.CreatedAt));
            }
        }

        return new UserQrFilesView(grant.Id, grant.ExpiresAt, items.OrderByDescending(i => i.Here).ThenByDescending(i => i.CreatedAt).ToList());
    }

    /// <summary>The grant, for this kiosk only, extended by being used.</summary>
    private async Task<(UserQrGrant Grant, Kiosk Kiosk)> UseAsync(Guid deviceId, Guid grantId, CancellationToken cancellationToken)
    {
        var kiosk = await KioskOfAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var grant = await _grants.FindAsync(grantId, cancellationToken).ConfigureAwait(false);

        if (grant is null || grant.KioskId != kiosk.Id)
        {
            throw new DomainException("userQr.grantExpired");
        }

        grant = grant with { ExpiresAt = _clock.UtcNow + GrantLifetime };
        await _grants.SaveAsync(grant, cancellationToken).ConfigureAwait(false);

        return (grant, kiosk);
    }

    private async Task<Kiosk> KioskOfAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (device?.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        return await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("kiosk");
    }
}
