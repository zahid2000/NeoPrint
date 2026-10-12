using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Incidents;

/// <summary>
/// What a kiosk's printer can be down with, and what that is called as an
/// incident (plan sections 7.4, 9.6).
/// </summary>
public static class PrinterFaults
{
    public const string Offline = "printer.offline";
    public const string OutOfPaper = "printer.outOfPaper";
    public const string PaperJam = "printer.paperJam";
    public const string NotFound = "printer.notFound";
    public const string Error = "printer.error";

    /// <summary>The incident types a fault is opened as; all of them close when the printer prints again.</summary>
    public static readonly IReadOnlyList<IncidentType> Types =
        [IncidentType.PrinterOffline, IncidentType.PaperOut, IncidentType.PaperJam, IncidentType.SelectedPrinterMissing];

    /// <summary>
    /// Whether the code says the printer itself cannot print. A file that could
    /// not be prepared (<c>printer.spoolFailed</c>) stops one order, not the kiosk.
    /// </summary>
    public static bool Stops(string? code) => code is Offline or OutOfPaper or PaperJam or NotFound or Error;

    /// <summary>A code the kiosk sent, or the general one: the kiosk is not trusted to name its own keys.</summary>
    public static string Normalize(string? code) => Stops(code) ? code! : Error;

    public static IncidentType TypeOf(string code) => code switch
    {
        OutOfPaper => IncidentType.PaperOut,
        PaperJam => IncidentType.PaperJam,
        NotFound => IncidentType.SelectedPrinterMissing,
        _ => IncidentType.PrinterOffline,
    };

    public static string CodeOf(IncidentType type) => type switch
    {
        IncidentType.PaperOut => OutOfPaper,
        IncidentType.PaperJam => PaperJam,
        IncidentType.SelectedPrinterMissing => NotFound,
        _ => Offline,
    };

    public static string DedupKey(Guid kioskId, IncidentType type) => $"kiosk-fault:{kioskId:N}:{type}";

    public static string OfflineKey(Guid kioskId) => $"kiosk-offline:{kioskId:N}";

    public static string SafeModeKey(Guid kioskId) => $"kiosk-safe-mode:{kioskId:N}";

    public static string CrashKey(Guid kioskId) => $"kiosk-crash:{kioskId:N}";

    public static string NoPrinterKey(Guid kioskId) => $"kiosk-no-printer:{kioskId:N}";

    public static string NotResumedKey(Guid jobId) => $"job-not-resumed:{jobId:N}";

    /// <summary>The last part of the code, for <c>{problem:printer}</c> in a message.</summary>
    public static string Problem(string code) => code[(code.IndexOf('.', StringComparison.Ordinal) + 1)..];
}

/// <summary>
/// The kiosk's printer cannot print (plan section 9.6, scenario A): the kiosk
/// is Paused, an incident is open, and - once per outage - the orders waiting
/// there are dealt with (<see cref="KioskIncidentRaised"/>).
/// </summary>
/// <param name="DeviceId">The kiosk's device, when the kiosk said so itself; null when the backend concluded it.</param>
/// <returns>The kiosk's status afterwards.</returns>
public sealed record DeclareKioskFaultCommand(Guid KioskId, string Code, Guid? DeviceId) : IRequest<KioskStatus>, ITransactionalRequest;

/// <summary>
/// A job stopped at the printer (plan section 7.4: "Xəta → cari job
/// Interrupted, kiosk Paused, incident"). When what stopped it was the printer
/// itself the kiosk is down now - a job that failed on it is no blip, so the
/// heartbeat's wait does not apply.
/// </summary>
public sealed record DeclareKioskFaultForJobCommand(Guid JobId) : IRequest<Unit>;

/// <summary>
/// The printer prints again: the kiosk is Online, the fault's incidents close,
/// and the interrupted orders that were not cancelled meanwhile go back into
/// the queue (plan section 7.4).
/// </summary>
public sealed record ClearKioskFaultCommand(Guid KioskId, Guid? DeviceId) : IRequest<KioskStatus>, ITransactionalRequest;

/// <summary><c>POST incidents</c> (plan section 6, Kiosk role): the kiosk reports what the backend cannot see.</summary>
/// <param name="Type">An incident type the kiosk may report (<see cref="KioskFaultHandlers.Reportable"/>).</param>
public sealed record ReportKioskIncidentCommand(Guid DeviceId, string Type) : IRequest<Unit>;

/// <summary>
/// <c>KioskHeartbeatMonitor</c> (plan section 6, every 15 seconds): a kiosk
/// whose heartbeat stopped for <c>Kiosk.OfflineAfterSeconds</c> is Offline and
/// has a Critical incident (plan section 9.6, scenario B).
/// </summary>
/// <returns>How many kiosks went offline in this run.</returns>
public sealed record MarkSilentKiosksOfflineCommand : IRequest<int>;

/// <summary>One kiosk of <see cref="MarkSilentKiosksOfflineCommand"/>, in its own transaction.</summary>
public sealed record MarkKioskOfflineCommand(Guid KioskId) : IRequest<bool>, ITransactionalRequest;

public sealed class KioskFaultHandlers :
    IRequestHandler<DeclareKioskFaultCommand, KioskStatus>,
    IRequestHandler<DeclareKioskFaultForJobCommand, Unit>,
    IRequestHandler<ClearKioskFaultCommand, KioskStatus>,
    IRequestHandler<ReportKioskIncidentCommand, Unit>,
    IRequestHandler<MarkSilentKiosksOfflineCommand, int>,
    IRequestHandler<MarkKioskOfflineCommand, bool>
{
    /// <summary>
    /// How often an order is sent to the printer by itself. One that stopped
    /// this many times is not put back in the queue when the printer returns:
    /// somebody has to look at it.
    /// </summary>
    public const int MaxAutomaticAttempts = 3;

    public const string ResumedReason = "printJobs.reason.printerRecovered";

    /// <summary>What a kiosk may report through <c>POST incidents</c>.</summary>
    public static readonly IReadOnlyList<IncidentType> Reportable =
    [
        IncidentType.PrinterOffline,
        IncidentType.PaperOut,
        IncidentType.PaperJam,
        IncidentType.SelectedPrinterMissing,
        IncidentType.AppCrash,
    ];

    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IPrintJobRepository _jobs;
    private readonly IPrintAttemptRepository _attempts;
    private readonly PrintQueueService _queue;
    private readonly IncidentReporter _reporter;
    private readonly IEventPublisher _events;
    private readonly IAdminNotifier _admins;
    private readonly ISettingsReader _settings;
    private readonly ISender _sender;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<KioskFaultHandlers> _logger;

    public KioskFaultHandlers(
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IPrintJobRepository jobs,
        IPrintAttemptRepository attempts,
        PrintQueueService queue,
        IncidentReporter reporter,
        IEventPublisher events,
        IAdminNotifier admins,
        ISettingsReader settings,
        ISender sender,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<KioskFaultHandlers> logger)
    {
        _kiosks = kiosks;
        _devices = devices;
        _jobs = jobs;
        _attempts = attempts;
        _queue = queue;
        _reporter = reporter;
        _events = events;
        _admins = admins;
        _settings = settings;
        _sender = sender;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="DeclareKioskFaultCommand" />
    public async Task<KioskStatus> Handle(DeclareKioskFaultCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("kiosk");

        // A kiosk with no printer chosen has nothing that could be down.
        if (kiosk.SelectedPrinter is null
            || kiosk.Status is KioskStatus.NotActivated or KioskStatus.NotConfigured or KioskStatus.Disabled)
        {
            return kiosk.Status;
        }

        var code = PrinterFaults.Normalize(request.Code);
        var before = kiosk.Status;
        var began = kiosk.DeclareFault(code, Actor(request.DeviceId), _clock.UtcNow);

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        var type = PrinterFaults.TypeOf(code);
        var key = PrinterFaults.DedupKey(kiosk.Id, type);
        var incident = await _reporter
            .RaiseAsync(
                type,
                IncidentSeverity.Critical,
                UserMessage.Of(
                    "incidents.kiosk.printerFault",
                    ("kiosk", kiosk.Code),
                    ("printer", kiosk.SelectedPrinter.Name),
                    ("problem", PrinterFaults.Problem(code))),
                key,
                cancellationToken,
                kiosk.Id,
                nameof(Kiosk),
                kiosk.Id)
            .ConfigureAwait(false);

        if (began)
        {
            _logger.LogWarning("Kiosk {KioskId}: the printer cannot print ({Code}); the kiosk is {Status}.", kiosk.Id, code, kiosk.Status);

            // Once per outage: the paid orders waiting at this kiosk.
            await _events
                .PublishAsync(
                    new KioskIncidentRaised(incident?.Id, kiosk.Id, type.ToString(), code, _correlation.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (kiosk.Status != before)
        {
            await _admins.KioskStatusChangedAsync(kiosk.Id, kiosk.Status.ToString(), cancellationToken).ConfigureAwait(false);
        }

        return kiosk.Status;
    }

    /// <inheritdoc cref="DeclareKioskFaultForJobCommand" />
    public async Task<Unit> Handle(DeclareKioskFaultForJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        // Cancelled for its refund meanwhile is still "stopped at the printer":
        // the reason it stopped is what counts.
        if (job?.ErrorReason is not { } stored || UserMessage.Decode(stored).Key is not { } code || !PrinterFaults.Stops(code))
        {
            return Unit.Value;
        }

        await _sender.Send(new DeclareKioskFaultCommand(job.KioskId, code, null), cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }

    /// <inheritdoc cref="ClearKioskFaultCommand" />
    public async Task<KioskStatus> Handle(ClearKioskFaultCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("kiosk");

        var now = _clock.UtcNow;
        var actor = Actor(request.DeviceId);
        var before = kiosk.Status;

        if (!kiosk.ClearFault(actor, now))
        {
            return kiosk.Status;
        }

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        foreach (var type in PrinterFaults.Types)
        {
            await _reporter
                .ResolveAsync(
                    PrinterFaults.DedupKey(kiosk.Id, type), new UserMessage("incidents.resolution.printerRecovered"), cancellationToken)
                .ConfigureAwait(false);
        }

        var resumed = await ResumeInterruptedAsync(kiosk, actor, now, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Kiosk {KioskId}: the printer prints again; the kiosk is {Status}, {Resumed} interrupted order(s) back in the queue.",
            kiosk.Id,
            kiosk.Status,
            resumed);

        if (resumed > 0)
        {
            await _queue.AnnounceAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        }

        if (kiosk.Status != before)
        {
            await _admins.KioskStatusChangedAsync(kiosk.Id, kiosk.Status.ToString(), cancellationToken).ConfigureAwait(false);
        }

        return kiosk.Status;
    }

    /// <summary>
    /// Plan section 7.4: "qeydiyyatlı istifadəçilərin ləğv olunmamış Interrupted
    /// job-ları yenidən Queued". A guest's bank payment went back by itself
    /// (plan section 2.5), so only orders with an owner are still waiting.
    /// </summary>
    private async Task<int> ResumeInterruptedAsync(Kiosk kiosk, ActorContext actor, DateTime now, CancellationToken cancellationToken)
    {
        var resumed = 0;

        foreach (var job in await _jobs.ListInterruptedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
        {
            if (job.UserId is null)
            {
                continue;
            }

            var attempts = await _attempts.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);

            if (attempts.Count >= MaxAutomaticAttempts)
            {
                // Not a fourth time by itself: the order stays stopped, its
                // owner can still cancel it, and somebody is asked to look.
                await _reporter
                    .RaiseAsync(
                        IncidentType.Other,
                        IncidentSeverity.High,
                        UserMessage.Of(
                            "incidents.job.notResumed", ("jobCode", job.JobCode), ("kiosk", kiosk.Code), ("attempts", attempts.Count)),
                        PrinterFaults.NotResumedKey(job.Id),
                        cancellationToken,
                        kiosk.Id,
                        nameof(PrintJob),
                        job.Id)
                    .ConfigureAwait(false);
                continue;
            }

            job.Enqueue(actor.Because(ResumedReason), now);

            await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            await _queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);
            await _events
                .PublishAsync(
                    new PrintJobStatusChanged(
                        job.Id,
                        job.KioskId,
                        nameof(PrintJobStatus.Interrupted),
                        nameof(PrintJobStatus.Queued),
                        _correlation.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);

            resumed++;
        }

        return resumed;
    }

    /// <inheritdoc cref="ReportKioskIncidentCommand" />
    public async Task<Unit> Handle(ReportKioskIncidentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<IncidentType>(request.Type, out var type) || !Reportable.Contains(type))
        {
            throw new ValidationException([new ValidationError("type", "incidents.typeNotReportable")]);
        }

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("device");

        if (device.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        if (type != IncidentType.AppCrash)
        {
            // The printer is down, said the kiosk: the same road as a
            // heartbeat that says so, without the wait.
            await _sender
                .Send(new DeclareKioskFaultCommand(kioskId, PrinterFaults.CodeOf(type), device.Id), cancellationToken)
                .ConfigureAwait(false);

            return Unit.Value;
        }

        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("kiosk");

        // One open incident per kiosk however often it crashes: the kiosk
        // cannot fill the list, and the count is in its own crash log.
        await _reporter
            .RaiseAsync(
                IncidentType.AppCrash,
                IncidentSeverity.High,
                UserMessage.Of("incidents.kiosk.appCrash", ("kiosk", kiosk.Code)),
                PrinterFaults.CrashKey(kiosk.Id),
                cancellationToken,
                kiosk.Id,
                nameof(Kiosk),
                kiosk.Id)
            .ConfigureAwait(false);

        return Unit.Value;
    }

    /// <inheritdoc cref="MarkSilentKiosksOfflineCommand" />
    public async Task<int> Handle(MarkSilentKiosksOfflineCommand request, CancellationToken cancellationToken)
    {
        var silentAfter = await SilentAfterAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var marked = 0;

        foreach (var kiosk in await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!IsSilent(kiosk, now, silentAfter))
            {
                continue;
            }

            try
            {
                if (await _sender.Send(new MarkKioskOfflineCommand(kiosk.Id), cancellationToken).ConfigureAwait(false))
                {
                    marked++;
                }
            }
            catch (ConcurrencyException)
            {
                // Its heartbeat arrived in the same moment: it is not silent.
            }
        }

        return marked;
    }

    /// <inheritdoc cref="MarkKioskOfflineCommand" />
    public async Task<bool> Handle(MarkKioskOfflineCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var silentAfter = await SilentAfterAsync(cancellationToken).ConfigureAwait(false);

        // Read again inside the transaction: a heartbeat may have come since.
        if (kiosk is null || !IsSilent(kiosk, now, silentAfter))
        {
            return false;
        }

        var before = kiosk.Status;

        // Maintenance is somebody's decision (an administrator's pause, safe
        // mode) and outlives the silence; the incident says the rest.
        if (kiosk.Status is KioskStatus.Online or KioskStatus.Paused or KioskStatus.NotConfigured)
        {
            kiosk.MoveTo(
                KioskStatus.Offline, ActorContext.System("kiosks.reason.heartbeatLost", _correlation.CorrelationId), now);
            await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
        }

        var silentFor = now - kiosk.LastHeartbeatAt!.Value;

        var opened = await _reporter
            .RaiseAsync(
                IncidentType.ConnectionLost,
                IncidentSeverity.Critical,
                UserMessage.Of("incidents.kiosk.offline", ("kiosk", kiosk.Code), ("seconds", (int)silentFor.TotalSeconds)),
                PrinterFaults.OfflineKey(kiosk.Id),
                cancellationToken,
                kiosk.Id,
                nameof(Kiosk),
                kiosk.Id)
            .ConfigureAwait(false);

        if (opened is null && kiosk.Status == before)
        {
            return false;
        }

        _logger.LogWarning(
            "Kiosk {KioskId} has not called in for {Seconds} s; it is {Status}.", kiosk.Id, (int)silentFor.TotalSeconds, kiosk.Status);

        await _events
            .PublishAsync(new KioskWentOffline(kiosk.Id, kiosk.LastHeartbeatAt, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        if (kiosk.Status != before)
        {
            await _admins.KioskStatusChangedAsync(kiosk.Id, kiosk.Status.ToString(), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// A kiosk with a device that has called in before and has stopped. One
    /// that is Offline already has been dealt with; one nobody activated, or
    /// that was taken out of service, is not expected to call.
    /// </summary>
    private static bool IsSilent(Kiosk kiosk, DateTime now, TimeSpan silentAfter)
        => kiosk.ActiveDeviceId is not null
           && kiosk.Status is KioskStatus.Online or KioskStatus.Paused or KioskStatus.NotConfigured or KioskStatus.Maintenance
           && kiosk.LastHeartbeatAt is { } heartbeat
           && now - heartbeat > silentAfter;

    private async Task<TimeSpan> SilentAfterAsync(CancellationToken cancellationToken)
        => TimeSpan.FromSeconds(Math.Max(
            1, await _settings.GetIntAsync(SettingKeys.KioskOfflineAfterSeconds, 60, cancellationToken).ConfigureAwait(false)));

    private ActorContext Actor(Guid? deviceId)
        => deviceId is { } device
            ? ActorContext.Kiosk(device, correlationId: _correlation.CorrelationId)
            : ActorContext.System(correlationId: _correlation.CorrelationId);
}
