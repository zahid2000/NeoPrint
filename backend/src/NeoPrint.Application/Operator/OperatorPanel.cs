using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Incidents;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Supplies;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Operator;

/// <summary>
/// "Mənim kiosklarım" (plan section 8B): one health card for each kiosk the
/// account looks after, the ones that need somebody first.
/// </summary>
/// <remarks>
/// One request for the whole screen. An operator stands at a kiosk with a
/// phone and a weak signal; the same picture pieced together from five requests
/// a kiosk would be most of a minute of waiting.
/// </remarks>
public sealed record ListOperatorKiosksQuery(Guid AdminUserId) : IRequest<IReadOnlyList<OperatorKioskCard>>;

/// <summary>"Kiosk (qısa detal)": the card, what the machine last reported, and what is open for it.</summary>
public sealed record GetOperatorKioskQuery(Guid AdminUserId, Guid KioskId) : IRequest<OperatorKioskDetail>;

/// <param name="Reasons">Why the printer is not printing, as it says itself (<c>media-empty</c>, <c>door-open</c>).</param>
/// <param name="Stale">The reading is old: the kiosk has not reported its printers for a while.</param>
public sealed record OperatorPrinterState(
    string Name, bool? Online, string? State, IReadOnlyList<string> Reasons, string? LastError, DateTime ReadAt, bool Stale);

/// <param name="Printer">The selected printer as last reported; null when none is chosen or nothing was reported.</param>
/// <param name="Paper">The counters by size; <c>Counted = false</c> while nobody has set one.</param>
/// <param name="Attention">
/// How much the kiosk needs somebody, for the order of the list: the higher,
/// the further up. Nought is "nothing to do here".
/// </param>
/// <remarks>
/// A part the account may not read - the queue without <c>jobs.view</c>, the
/// incidents without <c>incidents.view</c> - is left empty rather than refused:
/// the card is one answer, and the rest of it is still theirs.
/// </remarks>
public sealed record OperatorKioskCard(
    Guid Id,
    string Code,
    string Name,
    string Address,
    string Status,
    DateTime? SafeModeSince,
    DateTime? LastHeartbeatAt,
    OperatorPrinterState? Printer,
    IReadOnlyDictionary<string, PaperStockDto> Paper,
    IReadOnlyList<SupplyLevelView> Levels,
    IReadOnlyList<SupplyForecastView> Forecasts,
    IReadOnlyList<SupplyAlertView> Alerts,
    int QueueLength,
    int OpenIncidents,
    string? WorstIncident,
    int OpenTasks,
    int Attention);

public sealed record OperatorKioskDetail(
    OperatorKioskCard Kiosk,
    KioskTelemetryView? Telemetry,
    IReadOnlyList<IncidentView> Incidents,
    IReadOnlyList<ServiceTaskView> Tasks);

public sealed class OperatorPanelHandlers :
    IRequestHandler<ListOperatorKiosksQuery, IReadOnlyList<OperatorKioskCard>>,
    IRequestHandler<GetOperatorKioskQuery, OperatorKioskDetail>
{
    private static readonly string[] OpenIncidents = [nameof(IncidentStatus.Open), nameof(IncidentStatus.Acknowledged)];

    private static readonly string[] OpenTasks =
        [nameof(ServiceTaskStatus.Open), nameof(ServiceTaskStatus.Assigned), nameof(ServiceTaskStatus.InProgress)];

    private readonly IPermissionResolver _permissions;
    private readonly IKioskRepository _kiosks;
    private readonly SupplyOutlook _outlook;
    private readonly ISupplyAlertRepository _alerts;
    private readonly IIncidentRepository _incidents;
    private readonly IServiceTaskRepository _tasks;
    private readonly PrintQueueService _queue;
    private readonly ISender _sender;
    private readonly IClock _clock;

    public OperatorPanelHandlers(
        IPermissionResolver permissions,
        IKioskRepository kiosks,
        SupplyOutlook outlook,
        ISupplyAlertRepository alerts,
        IIncidentRepository incidents,
        IServiceTaskRepository tasks,
        PrintQueueService queue,
        ISender sender,
        IClock clock)
    {
        _permissions = permissions;
        _kiosks = kiosks;
        _outlook = outlook;
        _alerts = alerts;
        _incidents = incidents;
        _tasks = tasks;
        _queue = queue;
        _sender = sender;
        _clock = clock;
    }

    /// <inheritdoc cref="ListOperatorKiosksQuery" />
    public async Task<IReadOnlyList<OperatorKioskCard>> Handle(ListOperatorKiosksQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var active = await ActiveTasksAsync(effective, cancellationToken).ConfigureAwait(false);
        var cards = new List<OperatorKioskCard>();

        foreach (var kiosk in (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).Where(kiosk => effective.CanSee(kiosk.Id)))
        {
            cards.Add(await CardAsync(request.AdminUserId, effective, kiosk, active, cancellationToken).ConfigureAwait(false));
        }

        // The ones that need somebody first; then the ones in service before the ones that
        // are not (a kiosk without a device has nothing to look at); between equals, by code,
        // so the list does not jump about.
        return
        [
            .. cards
                .OrderByDescending(card => card.Attention)
                .ThenBy(card => card.Status is nameof(KioskStatus.NotActivated) or nameof(KioskStatus.Disabled))
                .ThenBy(card => card.Code, StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc cref="GetOperatorKioskQuery" />
    public async Task<OperatorKioskDetail> Handle(GetOperatorKioskQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }

        var active = await ActiveTasksAsync(effective, cancellationToken).ConfigureAwait(false);
        var card = await CardAsync(request.AdminUserId, effective, kiosk, active, cancellationToken).ConfigureAwait(false);

        var telemetry = effective.Has(Permissions.KiosksView)
            ? await _sender.Send(new GetKioskTelemetryQuery(request.AdminUserId, kiosk.Id), cancellationToken).ConfigureAwait(false)
            : null;

        var incidents = effective.Has(Permissions.IncidentsView)
            ? (await _sender.Send(new ListIncidentsQuery(request.AdminUserId, OpenIncidents, null, kiosk.Id, 1, 20), cancellationToken).ConfigureAwait(false)).Items
            : [];

        var tasks = effective.Has(Permissions.ServiceTasksView)
            ? (await _sender.Send(new ListServiceTasksQuery(request.AdminUserId, OpenTasks, kiosk.Id, Mine: false, 1, 20), cancellationToken).ConfigureAwait(false)).Items
            : [];

        return new OperatorKioskDetail(card, telemetry, incidents, tasks);
    }

    private async Task<ILookup<Guid, ServiceTask>> ActiveTasksAsync(EffectivePermissions effective, CancellationToken cancellationToken)
        => (effective.Has(Permissions.ServiceTasksView)
                ? await _tasks.ListActiveAsync(cancellationToken).ConfigureAwait(false)
                : [])
            .ToLookup(task => task.KioskId);

    private async Task<OperatorKioskCard> CardAsync(
        Guid adminUserId, EffectivePermissions effective, Kiosk kiosk, ILookup<Guid, ServiceTask> activeTasks, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var supplies = effective.Has(Permissions.SuppliesView);

        IReadOnlyList<SupplyLevelView> levels = supplies ? await _outlook.LevelsAsync(kiosk, now, cancellationToken).ConfigureAwait(false) : [];
        IReadOnlyList<SupplyForecastView> forecasts = supplies ? await _outlook.ForecastAsync(kiosk, now, cancellationToken).ConfigureAwait(false) : [];
        IReadOnlyList<SupplyAlert> alerts = supplies ? await _alerts.ListActiveForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false) : [];
        IReadOnlyList<Incident> incidents = effective.Has(Permissions.IncidentsView)
            ? await _incidents.ListOpenForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false)
            : [];
        var queue = effective.Has(Permissions.JobsView) && kiosk.ActiveDeviceId is not null
            ? (await _queue.EntriesAsync(kiosk.Id, cancellationToken).ConfigureAwait(false)).Count
            : 0;
        var printer = effective.Has(Permissions.PrintersView) && kiosk.SelectedPrinter is { } selected && kiosk.ActiveDeviceId is not null
            ? await PrinterAsync(adminUserId, kiosk.Id, selected.Name, cancellationToken).ConfigureAwait(false)
            : null;

        var tasks = activeTasks[kiosk.Id].Count();
        var worst = incidents.Count == 0 ? (IncidentSeverity?)null : incidents.Min(incident => incident.Severity);

        return new OperatorKioskCard(
            kiosk.Id,
            kiosk.Code,
            kiosk.Name,
            kiosk.Location.Address,
            kiosk.Status.ToString(),
            kiosk.SafeModeSince,
            kiosk.LastHeartbeatAt,
            printer,
            supplies
                ? kiosk.PaperStock.ToDictionary(pair => pair.Key, pair => PaperStockDto.Of(pair.Value), StringComparer.Ordinal)
                : new Dictionary<string, PaperStockDto>(StringComparer.Ordinal),
            levels,
            forecasts,
            [.. alerts.OrderByDescending(alert => alert.Level).Select(alert => SupplyAlertView.Of(alert, kiosk.Code))],
            queue,
            incidents.Count,
            worst?.ToString(),
            tasks,
            AttentionOf(kiosk, printer, alerts, worst, tasks, supplies));
    }

    private async Task<OperatorPrinterState?> PrinterAsync(Guid adminUserId, Guid kioskId, string name, CancellationToken cancellationToken)
    {
        var readings = await _sender.Send(new GetPrinterTelemetryQuery(adminUserId, kioskId), cancellationToken).ConfigureAwait(false);
        var reading = readings.FirstOrDefault(view => string.Equals(view.Sample.Printer.PrinterName, name, StringComparison.Ordinal));

        return reading is null
            ? null
            : new OperatorPrinterState(
                name,
                reading.Sample.Online,
                reading.Sample.State,
                reading.Sample.StateReasons,
                reading.Sample.LastError,
                reading.Sample.Timestamp,
                reading.Stale);
    }

    /// <summary>
    /// How far up the list a kiosk belongs. The steps are apart enough that a
    /// kiosk nobody can print at is always above one that is merely running
    /// low; within a step the list falls back to the code.
    /// </summary>
    public static int AttentionOf(
        Kiosk kiosk, OperatorPrinterState? printer, IReadOnlyList<SupplyAlert> alerts, IncidentSeverity? worstIncident, int openTasks, bool supplies)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(alerts);

        // Not in service at all: nothing here is the operator's to fix today.
        if (kiosk.Status is KioskStatus.NotActivated or KioskStatus.Disabled)
        {
            return 0;
        }

        var score = 0;

        if (kiosk.Status == KioskStatus.Offline || kiosk.SafeModeSince is not null)
        {
            score = Math.Max(score, 100);
        }

        if (worstIncident == IncidentSeverity.Critical)
        {
            score = Math.Max(score, 90);
        }

        if (alerts.Any(alert => alert.Level == SupplyAlertLevel.Critical))
        {
            score = Math.Max(score, 80);
        }

        if (printer is { Stale: false } && (printer.Online == false || printer.Reasons.Count > 0))
        {
            score = Math.Max(score, 70);
        }

        if (kiosk.Status == KioskStatus.NotConfigured)
        {
            score = Math.Max(score, 60);
        }

        if (worstIncident == IncidentSeverity.High || alerts.Any(alert => alert.Level == SupplyAlertLevel.Warning))
        {
            score = Math.Max(score, 50);
        }

        if (supplies && kiosk.PaperStock.Values.Any(tray => tray.Capacity > 0 && !tray.IsCounted))
        {
            score = Math.Max(score, 40);
        }

        if (kiosk.Status is KioskStatus.Maintenance or KioskStatus.Paused)
        {
            score = Math.Max(score, 30);
        }

        if (worstIncident == IncidentSeverity.Medium)
        {
            score = Math.Max(score, 20);
        }

        return openTasks > 0 ? Math.Max(score, 10) : score;
    }
}
