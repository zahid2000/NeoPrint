using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

/// <summary>
/// <c>SupplyEvaluator</c> (plan sections 2.15, 9.9): a kiosk's paper and
/// toner against the rules. A level that fell below its rule raises an alert -
/// once per level - and, when the rule asks, a service task; a level that is
/// back above it clears the alert.
/// </summary>
/// <remarks>
/// Run when a level moved (<c>SupplyLevelChanged</c>) and for every kiosk
/// every five minutes: evaluating twice changes nothing the second time.
/// </remarks>
public sealed record EvaluateKioskSuppliesCommand(Guid KioskId) : IRequest<SupplyEvaluationResult>, ITransactionalRequest;

/// <param name="Raised">Alerts opened now.</param>
/// <param name="Changed">Alerts whose level changed.</param>
/// <param name="Cleared">Alerts that are over.</param>
public sealed record SupplyEvaluationResult(int Raised, int Changed, int Cleared)
{
    public static readonly SupplyEvaluationResult Nothing = new(0, 0, 0);
}

/// <summary>The five-minute sweep: every kiosk that has a device.</summary>
/// <returns>How many kiosks were looked at.</returns>
public sealed record EvaluateAllSuppliesCommand : IRequest<int>;

/// <summary>One supply of a kiosk as it stands now.</summary>
/// <param name="Sheets">For paper: the sheets in the tray.</param>
/// <param name="Percent">What the printer reports, or for paper the tray's fill; null when unknown.</param>
public sealed record SupplyReading(SupplyResource Resource, string? Variant, decimal? Sheets, decimal? Percent)
{
    public string Key(Guid kioskId) => SupplyAlert.KeyOf(kioskId, Resource, Variant);

    public decimal? In(SupplyUnit unit) => unit == SupplyUnit.Sheets ? Sheets : Percent;
}

public sealed class SupplyEvaluationHandlers :
    IRequestHandler<EvaluateKioskSuppliesCommand, SupplyEvaluationResult>,
    IRequestHandler<EvaluateAllSuppliesCommand, int>
{
    /// <summary>A printer's reading older than this says nothing about its toner today.</summary>
    public static readonly TimeSpan ReadingGoodFor = TimeSpan.FromHours(24);

    private readonly IKioskRepository _kiosks;
    private readonly ISupplyAlertRuleRepository _rules;
    private readonly ISupplyAlertRepository _alerts;
    private readonly IServiceTaskRepository _tasks;
    private readonly IPrinterTelemetryStore _telemetry;
    private readonly ServiceTaskDesk _desk;
    private readonly IAdminNotifier _panels;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IEmailSender _email;
    private readonly ISender _sender;
    private readonly IClock _clock;
    private readonly ILogger<SupplyEvaluationHandlers> _logger;

    public SupplyEvaluationHandlers(
        IKioskRepository kiosks,
        ISupplyAlertRuleRepository rules,
        ISupplyAlertRepository alerts,
        IServiceTaskRepository tasks,
        IPrinterTelemetryStore telemetry,
        ServiceTaskDesk desk,
        IAdminNotifier panels,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        IEmailSender email,
        ISender sender,
        IClock clock,
        ILogger<SupplyEvaluationHandlers> logger)
    {
        _kiosks = kiosks;
        _rules = rules;
        _alerts = alerts;
        _tasks = tasks;
        _telemetry = telemetry;
        _desk = desk;
        _panels = panels;
        _admins = admins;
        _permissions = permissions;
        _email = email;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="EvaluateAllSuppliesCommand" />
    public async Task<int> Handle(EvaluateAllSuppliesCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var looked = 0;

        foreach (var kiosk in (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).Where(HasSupplies))
        {
            try
            {
                // Each kiosk in a transaction of its own: one that fails does not hold up the rest.
                await _sender.Send(new EvaluateKioskSuppliesCommand(kiosk.Id), cancellationToken).ConfigureAwait(false);
                looked++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The supplies of kiosk {KioskId} could not be evaluated; the next sweep tries again.", kiosk.Id);
            }
        }

        return looked;
    }

    /// <inheritdoc cref="EvaluateKioskSuppliesCommand" />
    public async Task<SupplyEvaluationResult> Handle(EvaluateKioskSuppliesCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is null)
        {
            return SupplyEvaluationResult.Nothing;
        }

        var now = _clock.UtcNow;
        var active = (await _alerts.ListActiveForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
            .ToDictionary(alert => alert.Key, StringComparer.Ordinal);

        // A kiosk nobody serves at - never activated, or switched off - has nothing to refill.
        if (!HasSupplies(kiosk))
        {
            var over = 0;

            foreach (var alert in active.Values)
            {
                await ClearAsync(kiosk, alert, alert.Value, now, cancellationToken).ConfigureAwait(false);
                over++;
            }

            return new SupplyEvaluationResult(0, 0, over);
        }

        var rules = (await _rules.ListAsync(cancellationToken).ConfigureAwait(false)).Where(rule => rule.IsActive).ToList();
        var (readings, printerKnown) = await ReadAsync(kiosk, now, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int raised = 0, changed = 0, cleared = 0;

        foreach (var reading in readings)
        {
            var key = reading.Key(kiosk.Id);
            seen.Add(key);

            active.TryGetValue(key, out var alert);

            var rule = RuleFor(rules, kiosk.Id, reading);
            var value = rule is null ? null : reading.In(rule.Unit);
            var level = rule is null || value is null ? null : rule.LevelOf(value.Value);

            if (level is null)
            {
                if (alert is not null)
                {
                    await ClearAsync(kiosk, alert, value ?? alert.Value, now, cancellationToken).ConfigureAwait(false);
                    cleared++;
                }

                continue;
            }

            if (alert is null)
            {
                if (await RaiseAsync(kiosk, rule!, reading, level.Value, value!.Value, now, cancellationToken).ConfigureAwait(false))
                {
                    raised++;
                }

                continue;
            }

            var before = alert.Value;

            if (alert.Observe(level.Value, value!.Value, now))
            {
                await EnsureTaskAsync(kiosk, rule!, alert, now, cancellationToken).ConfigureAwait(false);
                await _alerts.UpdateAsync(alert, cancellationToken).ConfigureAwait(false);
                await TellAsync(kiosk, rule!, alert, cancellationToken).ConfigureAwait(false);
                changed++;
            }
            else if (before != alert.Value)
            {
                // The same level, another number: kept current for the panel, nobody is told.
                await _alerts.UpdateAsync(alert, cancellationToken).ConfigureAwait(false);
            }
        }

        // An alert about something that is no longer read. A tray that was taken
        // away is over; a toner the printer has stopped reporting is not - no
        // news from the printer is not good news.
        foreach (var alert in active.Values.Where(alert => !seen.Contains(alert.Key)))
        {
            var paper = alert.Resource is SupplyResource.PaperA4 or SupplyResource.PaperA3;

            if (paper || printerKnown)
            {
                await ClearAsync(kiosk, alert, alert.Value, now, cancellationToken).ConfigureAwait(false);
                cleared++;
            }
        }

        return new SupplyEvaluationResult(raised, changed, cleared);
    }

    private static bool HasSupplies(Kiosk kiosk)
        => kiosk.ActiveDeviceId is not null && kiosk.Status is not (KioskStatus.NotActivated or KioskStatus.Disabled);

    /// <summary>
    /// The trays the kiosk has, and what its selected printer last said about
    /// its toner - when that was said recently enough to mean anything.
    /// </summary>
    private async Task<(List<SupplyReading> Readings, bool PrinterKnown)> ReadAsync(
        Kiosk kiosk, DateTime now, CancellationToken cancellationToken)
    {
        var readings = new List<SupplyReading>();

        // A tray nobody has counted yet has no level to judge (T-258): a newly
        // installed kiosk would otherwise open a Critical alert and a task at once.
        foreach (var (paper, stock) in kiosk.PaperStock.Where(tray => tray.Value.Capacity > 0 && tray.Value.IsCounted))
        {
            if (Enum.TryParse<SupplyResource>(SupplyEvent.PaperResource(paper), out var resource))
            {
                readings.Add(new SupplyReading(resource, null, stock.Current, Math.Round(stock.Current * 100m / stock.Capacity, 1)));
            }
        }

        if (kiosk.SelectedPrinter is not { } printer)
        {
            return (readings, false);
        }

        var sample = await _telemetry.LatestAsync(kiosk.Id, printer.Name, cancellationToken).ConfigureAwait(false);

        if (sample is null || now - sample.Timestamp > ReadingGoodFor)
        {
            return (readings, false);
        }

        // Two cartridges of one colour (some printers have them) are one reading: the lower.
        readings.AddRange(sample.Supplies
            .Where(supply => supply.LevelPercent is not null)
            .GroupBy(supply => (Resource: PrinterSupplies.ResourceOf(supply), Variant: PrinterSupplies.VariantOf(supply).ToLowerInvariant()))
            .Select(group => new SupplyReading(
                group.Key.Resource, PrinterSupplies.VariantOf(group.First()), null, group.Min(supply => supply.LevelPercent!.Value))));

        return (readings, true);
    }

    /// <summary>
    /// The rule a reading is judged by: one for this kiosk replaces the
    /// global one, and among those one that names the colour is closer than
    /// one that does not (plan section 2.15).
    /// </summary>
    public static SupplyAlertRule? RuleFor(IEnumerable<SupplyAlertRule> rules, Guid kioskId, SupplyReading reading)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(reading);

        return rules
            .Where(rule => rule.IsActive
                           && rule.Watches(reading.Resource, reading.Variant)
                           && (rule.IsGlobal || rule.KioskIds.Contains(kioskId)))
            .OrderByDescending(rule => !rule.IsGlobal)
            .ThenByDescending(rule => !string.IsNullOrWhiteSpace(rule.ResourceVariant))
            .ThenBy(rule => rule.CreatedAt)
            .ThenBy(rule => rule.Id)
            .FirstOrDefault();
    }

    private async Task<bool> RaiseAsync(
        Kiosk kiosk, SupplyAlertRule rule, SupplyReading reading, SupplyAlertLevel level, decimal value, DateTime now, CancellationToken cancellationToken)
    {
        var alert = SupplyAlert.Raise(kiosk.Id, rule, reading.Variant, level, value, now);

        await EnsureTaskAsync(kiosk, rule, alert, now, cancellationToken).ConfigureAwait(false);

        if (!await _alerts.TryAddAsync(alert, cancellationToken).ConfigureAwait(false))
        {
            // Another evaluation of the same kiosk got here first.
            return false;
        }

        _logger.LogWarning(
            "Kiosk {KioskId}: {Resource} {Variant} is at {Value} ({Unit}) - {Level}.",
            kiosk.Id, alert.Resource, alert.Variant, alert.Value, alert.Unit, alert.Level);

        await TellAsync(kiosk, rule, alert, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// The rule's service task: opened with the alert, and made Critical when
    /// the alert becomes Critical (plan section 9.9, step 3).
    /// </summary>
    private async Task EnsureTaskAsync(Kiosk kiosk, SupplyAlertRule rule, SupplyAlert alert, DateTime now, CancellationToken cancellationToken)
    {
        if (!rule.CreateServiceTask)
        {
            return;
        }

        var priority = alert.Level == SupplyAlertLevel.Critical ? TaskPriority.Critical : rule.TaskPriority;
        var wanted = ServiceTask.Open(
            kiosk.Id,
            ServiceTask.TypeFor(alert.Resource),
            priority,
            ServiceTaskSource.AlertRule,
            now,
            alert.Resource,
            alert.Variant,
            UserMessage.Of(
                    "serviceTasks.note.supplyLow",
                    ("supply", alert.Resource.ToString()),
                    ("variant", VariantText(alert.Variant)),
                    ("value", alert.Value),
                    ("unit", alert.Unit.ToString()))
                .Encode());

        var (task, _) = await _desk.EnsureAsync(kiosk, wanted, now, cancellationToken).ConfigureAwait(false);

        alert.ServiceTaskId = task.Id;
    }

    private async Task ClearAsync(Kiosk kiosk, SupplyAlert alert, decimal value, DateTime now, CancellationToken cancellationToken)
    {
        alert.Clear(value, now);
        await _alerts.UpdateAsync(alert, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Kiosk {KioskId}: {Resource} {Variant} is back above its rule.", kiosk.Id, alert.Resource, alert.Variant);

        await _panels.SupplyAlertClearedAsync(SupplyAlertView.Of(alert, kiosk.Code), cancellationToken).ConfigureAwait(false);

        // Its task, when nobody has started it: there is nothing left to do.
        // One an operator is at stays theirs to finish.
        if (alert.ServiceTaskId is { } taskId
            && await _tasks.FindByIdAsync(taskId, cancellationToken).ConfigureAwait(false) is { } task
            && task.Status is ServiceTaskStatus.Open or ServiceTaskStatus.Assigned
            && task.Source == ServiceTaskSource.AlertRule)
        {
            task.Cancel(new UserMessage("serviceTasks.reason.levelRecovered").Encode(), ActorContext.System("serviceTasks.reason.levelRecovered"), now);
            await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
            await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The panels always; e-mail when the rule lists it, to those who look after the kiosk's supplies.</summary>
    private async Task TellAsync(Kiosk kiosk, SupplyAlertRule rule, SupplyAlert alert, CancellationToken cancellationToken)
    {
        await _panels.SupplyAlertRaisedAsync(SupplyAlertView.Of(alert, kiosk.Code), cancellationToken).ConfigureAwait(false);

        if (!rule.Channels.HasFlag(AlertChannels.Email))
        {
            return;
        }

        var place = kiosk.Code + " — " + kiosk.Name;

        foreach (var admin in (await _admins.ListAsync(cancellationToken).ConfigureAwait(false)).Where(admin => admin.IsActive && !string.IsNullOrWhiteSpace(admin.Email)))
        {
            var effective = await _permissions.ResolveAsync(admin.Id, cancellationToken).ConfigureAwait(false);

            if (!effective.IsActive || !effective.Has(Permissions.SuppliesView) || !effective.CanSee(kiosk.Id))
            {
                continue;
            }

            try
            {
                await _email
                    .SendAsync(
                        admin.Email,
                        Messages.Write(UserMessage.Of(
                            "email.supplyAlert.subject",
                            ("level", alert.Level.ToString()),
                            ("kiosk", kiosk.Code),
                            ("supply", alert.Resource.ToString()),
                            ("variant", VariantText(alert.Variant)))),
                        Messages.Write(UserMessage.Of(
                            "email.supplyAlert.body",
                            ("level", alert.Level.ToString()),
                            ("kiosk", System.Net.WebUtility.HtmlEncode(place)),
                            ("supply", alert.Resource.ToString()),
                            ("variant", System.Net.WebUtility.HtmlEncode(VariantText(alert.Variant))),
                            ("value", alert.Value),
                            ("unit", alert.Unit.ToString()))),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The alert is in the panel; a mail server that is down does not undo it.
                _logger.LogWarning(exception, "Supply alert {AlertId}: an e-mail was not sent.", alert.Id);
            }
        }
    }

    /// <summary>
    /// Which one of its kind, as it follows the supply's name in a message:
    /// " (black)", or nothing for paper. The name itself goes into a message
    /// as the resource's name and the catalog writes it in the reader's words.
    /// </summary>
    public static string VariantText(string? variant)
        => string.IsNullOrWhiteSpace(variant) ? string.Empty : $" ({variant.Trim()})";
}
