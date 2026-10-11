using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Localization;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

/// <summary>The <c>kiosk_commands</c> collection (plan section 7.8).</summary>
public interface IKioskCommandRepository
{
    Task<KioskCommand?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(KioskCommand command, CancellationToken cancellationToken = default);

    Task UpdateAsync(KioskCommand command, CancellationToken cancellationToken = default);

    /// <summary>Commands still waiting for this kiosk's answer, oldest first.</summary>
    Task<IReadOnlyList<KioskCommand>> ListOpenAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>The most recent commands of a kiosk, newest first.</summary>
    Task<IReadOnlyList<KioskCommand>> ListRecentAsync(Guid kioskId, int take, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/// <summary>The kiosk reports the printers it can see (plan section 7.7).</summary>
public sealed record ReportKioskPrintersCommand(Guid DeviceId, IReadOnlyCollection<ReportedPrinterDto> Printers) : IRequest<Unit>;

/// <summary>The commands a kiosk has not answered yet: missed while it was offline.</summary>
public sealed record GetOpenKioskCommandsQuery(Guid DeviceId) : IRequest<IReadOnlyList<KioskCommandView>>;

/// <summary>The kiosk's answer to a command.</summary>
/// <param name="MessageKey">What happened, as a message key: <c>printer.outOfPaper</c>.</param>
/// <param name="SheetsUsed">Paper a test page took; it comes off the counter (plan section 2.15).</param>
public sealed record CompleteKioskCommandCommand(
    Guid DeviceId, Guid CommandId, bool Succeeded, string? MessageKey, int SheetsUsed) : IRequest<KioskCommandView>, Behaviors.ITransactionalRequest;

/// <summary>The printers tab of the kiosk detail (plan section 8A.3).</summary>
public sealed record GetKioskPrintersQuery(Guid AdminUserId, Guid KioskId) : IRequest<KioskPrintersView>;

/// <summary>"Default et": the printer the kiosk prints on.</summary>
public sealed record SelectKioskPrinterCommand(Guid AdminUserId, Guid KioskId, string PrinterName) : IRequest<KioskPrintersView>;

/// <summary>
/// Switches capabilities off (never on) for the selected printer: null
/// follows what was detected.
/// </summary>
public sealed record SetCapabilityOverridesCommand(Guid AdminUserId, Guid KioskId, bool? Color, bool? Duplex, bool? A3)
    : IRequest<KioskPrintersView>;

/// <summary>Sends a printer command: refresh the list, or print the test page.</summary>
public sealed record SendKioskCommandCommand(Guid AdminUserId, Guid KioskId, KioskCommandType Type)
    : IRequest<KioskCommandView>, Behaviors.ITransactionalRequest;

/// <summary>
/// The kiosk has a command and will carry it out; the result follows later -
/// for a restart, from the application that comes up afterwards (plan section 7.8).
/// </summary>
public sealed record AcknowledgeKioskCommandCommand(Guid DeviceId, Guid CommandId)
    : IRequest<KioskCommandView>, Behaviors.ITransactionalRequest;

/// <summary>The kiosk's recent commands, for the panel.</summary>
public sealed record ListKioskCommandsQuery(Guid AdminUserId, Guid KioskId) : IRequest<IReadOnlyList<KioskCommandView>>;

/// <summary>A command as the kiosk and the panel see it.</summary>
public sealed record KioskCommandView(
    Guid Id, Guid KioskId, string Type, string Status, string? Result, DateTime CreatedAt, DateTime? CompletedAt);

/// <summary>The printers tab: what was found, what is chosen, what may be offered.</summary>
public sealed record KioskPrintersView(
    Guid KioskId,
    string Status,
    string? SelectedPrinter,
    IReadOnlyList<AvailablePrinterDto> Printers,
    PrinterCapabilitiesDto Detected,
    CapabilityOverridesDto Overrides,
    PrinterCapabilitiesDto Effective);

public sealed record CapabilityOverridesDto(bool? Color, bool? Duplex, bool? A3);

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

/// <summary>Printers and commands, from the kiosk's side and the panel's (plan sections 7.7, 7.8).</summary>
public sealed class KioskPrinterHandlers :
    IRequestHandler<ReportKioskPrintersCommand, Unit>,
    IRequestHandler<GetOpenKioskCommandsQuery, IReadOnlyList<KioskCommandView>>,
    IRequestHandler<CompleteKioskCommandCommand, KioskCommandView>,
    IRequestHandler<AcknowledgeKioskCommandCommand, KioskCommandView>,
    IRequestHandler<GetKioskPrintersQuery, KioskPrintersView>,
    IRequestHandler<SelectKioskPrinterCommand, KioskPrintersView>,
    IRequestHandler<SetCapabilityOverridesCommand, KioskPrintersView>,
    IRequestHandler<SendKioskCommandCommand, KioskCommandView>,
    IRequestHandler<ListKioskCommandsQuery, IReadOnlyList<KioskCommandView>>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskCommandRepository _commands;
    private readonly ISupplyEventRepository _supplies;
    private readonly IPermissionResolver _permissions;
    private readonly IKioskNotifier _kiosk;
    private readonly IAdminNotifier _admins;
    private readonly IAuditWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<KioskPrinterHandlers> _logger;

    public KioskPrinterHandlers(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IKioskCommandRepository commands,
        ISupplyEventRepository supplies,
        IPermissionResolver permissions,
        IKioskNotifier kiosk,
        IAdminNotifier admins,
        IAuditWriter audit,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<KioskPrinterHandlers> logger)
    {
        _devices = devices;
        _kiosks = kiosks;
        _commands = commands;
        _supplies = supplies;
        _permissions = permissions;
        _kiosk = kiosk;
        _admins = admins;
        _audit = audit;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="ReportKioskPrintersCommand" />
    public async Task<Unit> Handle(ReportKioskPrintersCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var reported = request.Printers
            .Where(printer => !string.IsNullOrWhiteSpace(printer.Name))
            .GroupBy(printer => printer.Name.Trim(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(50)
            .Select(printer => new AvailablePrinter
            {
                Name = printer.Name.Trim(),
                DriverName = printer.DriverName,
                IsOnline = printer.IsOnline,
                DetectedCapabilities = new PrinterCapabilities { Color = printer.Color, Duplex = printer.Duplex, A3 = printer.A3 },
                LastSeenAt = now,
            })
            .ToList();

        var changed = !Same(kiosk.AvailablePrinters, reported);
        kiosk.AvailablePrinters = reported;

        if (kiosk.SelectedPrinter is { } selected && reported.All(printer => printer.Name != selected.Name))
        {
            // Plan section 7.7: an incident and a paused kiosk, with the
            // incident module in step 7. The panel sees it now.
            _logger.LogWarning("Kiosk {KioskId}: the selected printer {Printer} is not in the list any more.", kiosk.Id, selected.Name);
        }

        if (changed)
        {
            // The effective capabilities may have changed with the list.
            kiosk.ConfigurationChangedAt = now;
        }

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            await _admins.KioskPrintersUpdatedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }

    /// <inheritdoc cref="GetOpenKioskCommandsQuery" />
    public async Task<IReadOnlyList<KioskCommandView>> Handle(GetOpenKioskCommandsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var open = new List<KioskCommandView>();

        foreach (var command in await _commands.ListOpenAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
        {
            if (command.IsOverdue(now))
            {
                // A day late is too late: "print a test page" tomorrow morning
                // is not what anybody asked for (plan section 7.8).
                command.Expire(now);
                await _commands.UpdateAsync(command, cancellationToken).ConfigureAwait(false);
                continue;
            }

            open.Add(View(command));
        }

        return open;
    }

    /// <inheritdoc cref="CompleteKioskCommandCommand" />
    public async Task<KioskCommandView> Handle(CompleteKioskCommandCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var command = await _commands.FindByIdAsync(request.CommandId, cancellationToken).ConfigureAwait(false);

        if (command is null || command.KioskId != kiosk.Id)
        {
            throw new NotFoundException("kioskCommand");
        }

        if (!command.IsOpen)
        {
            // The same answer again after a lost response: nothing more to do.
            return View(command);
        }

        var now = _clock.UtcNow;
        var actor = ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId);
        var result = request.MessageKey is { } key && Messages.Has(key)
            ? new UserMessage(key)
            : new UserMessage(request.Succeeded ? "kioskCommands.done" : "printer.error");

        command.Complete(request.Succeeded, result, actor, now);
        await _commands.UpdateAsync(command, cancellationToken).ConfigureAwait(false);

        if (command.Type == KioskCommandType.TestPrint)
        {
            await ApplyTestPrintAsync(kiosk, request, actor, now, cancellationToken).ConfigureAwait(false);
        }

        await _admins.CommandResultAsync(kiosk.Id, View(command), cancellationToken).ConfigureAwait(false);

        return View(command);
    }

    /// <inheritdoc cref="AcknowledgeKioskCommandCommand" />
    public async Task<KioskCommandView> Handle(AcknowledgeKioskCommandCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var command = await _commands.FindByIdAsync(request.CommandId, cancellationToken).ConfigureAwait(false);

        if (command is null || command.KioskId != kiosk.Id)
        {
            throw new NotFoundException("kioskCommand");
        }

        if (command.Status != KioskCommandStatus.Sent)
        {
            // Acknowledged before, or answered already: the same reply.
            return View(command);
        }

        command.Acknowledge(ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId), _clock.UtcNow);
        await _commands.UpdateAsync(command, cancellationToken).ConfigureAwait(false);

        await _admins.CommandResultAsync(kiosk.Id, View(command), cancellationToken).ConfigureAwait(false);

        return View(command);
    }

    /// <inheritdoc cref="GetKioskPrintersQuery" />
    public async Task<KioskPrintersView> Handle(GetKioskPrintersQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PrintersView(await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="SelectKioskPrinterCommand" />
    public async Task<KioskPrintersView> Handle(SelectKioskPrinterCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var printer = kiosk.AvailablePrinters.FirstOrDefault(printer => printer.Name == request.PrinterName)
                      ?? throw new ValidationException([new ValidationError("printerName", "printers.notReported")]);

        var before = kiosk.SelectedPrinter?.Name;
        var now = _clock.UtcNow;

        kiosk.SelectedPrinter = new SelectedPrinter { Name = printer.Name, SelectedBy = request.AdminUserId, SelectedAt = now };

        // Overrides belong to the printer they were set for; a different
        // printer starts from what it can actually do.
        if (before != printer.Name)
        {
            kiosk.CapabilityOverrides = new CapabilityOverrides();
        }

        kiosk.ConfigurationChangedAt = now;
        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "printers.select", nameof(Kiosk), kiosk.Id.ToString(), before, printer.Name, null, null),
                cancellationToken)
            .ConfigureAwait(false);

        // The kiosk applies it once the job at the printer is done (plan 7.7).
        await _kiosk.PrinterConfigChangedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return PrintersView(kiosk);
    }

    /// <inheritdoc cref="SetCapabilityOverridesCommand" />
    public async Task<KioskPrintersView> Handle(SetCapabilityOverridesCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var detected = Detected(kiosk);

        // Plan section 7.7: an override can only switch a capability off. A
        // printer does not learn colour because a form says so.
        var problems = new List<ValidationError>();

        if (request.Color == true && !detected.Color)
        {
            problems.Add(new ValidationError("color", "printers.cannotEnable"));
        }

        if (request.Duplex == true && !detected.Duplex)
        {
            problems.Add(new ValidationError("duplex", "printers.cannotEnable"));
        }

        if (request.A3 == true && !detected.A3)
        {
            problems.Add(new ValidationError("a3", "printers.cannotEnable"));
        }

        if (problems.Count > 0)
        {
            throw new ValidationException(problems);
        }

        var before = kiosk.CapabilityOverrides;

        // "true" on a capability the printer has is the same as following it.
        kiosk.CapabilityOverrides = new CapabilityOverrides
        {
            Color = request.Color == false ? false : null,
            Duplex = request.Duplex == false ? false : null,
            A3 = request.A3 == false ? false : null,
        };

        kiosk.ConfigurationChangedAt = _clock.UtcNow;
        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "printers.capabilities",
                    nameof(Kiosk),
                    kiosk.Id.ToString(),
                    $"{before.Color}/{before.Duplex}/{before.A3}",
                    $"{kiosk.CapabilityOverrides.Color}/{kiosk.CapabilityOverrides.Duplex}/{kiosk.CapabilityOverrides.A3}",
                    null,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        await _kiosk.PrinterConfigChangedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return PrintersView(kiosk);
    }

    /// <inheritdoc cref="SendKioskCommandCommand" />
    public async Task<KioskCommandView> Handle(SendKioskCommandCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk.ActiveDeviceId is null)
        {
            throw new DomainException("kioskCommands.noDevice");
        }

        if (request.Type == KioskCommandType.TestPrint && kiosk.SelectedPrinter is null)
        {
            throw new DomainException("printer.notSelected");
        }

        var now = _clock.UtcNow;

        // Pausing and resuming are the backend's own state first: orders are
        // refused from this moment, whether or not the kiosk has heard. The
        // command is how the kiosk is told to change its screen.
        if (request.Type is KioskCommandType.Pause or KioskCommandType.Resume)
        {
            var actor = ActorContext.Admin(request.AdminUserId, correlationId: _correlation.CorrelationId);

            var changed = request.Type == KioskCommandType.Pause
                ? kiosk.PauseByAdmin(actor, now)
                : kiosk.ResumeByAdmin(actor, now);

            if (changed)
            {
                await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
            }
        }

        // Pressed twice, or sent again after a lost answer: the command that
        // is still open is the answer - not a second restart behind the first.
        var open = await _commands.ListOpenAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        if (open.LastOrDefault(existing => existing.Type == request.Type && !existing.IsOverdue(now)) is { } pending)
        {
            return View(pending);
        }

        var command = new KioskCommand
        {
            KioskId = kiosk.Id,
            Type = request.Type,
            CreatedBy = request.AdminUserId,
            CreatedAt = now,
        };

        // Stored first: a kiosk that is offline now reads it when it is back.
        await _commands.AddAsync(command, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, $"kiosks.command.{request.Type}", nameof(Kiosk), kiosk.Id.ToString(), null, command.Id.ToString(), null, null),
                cancellationToken)
            .ConfigureAwait(false);

        await _kiosk.KioskCommandAsync(
                kiosk.Id, new KioskCommandMessage(command.Id, command.Type.ToString(), null), cancellationToken)
            .ConfigureAwait(false);

        return View(command);
    }

    /// <inheritdoc cref="ListKioskCommandsQuery" />
    public async Task<IReadOnlyList<KioskCommandView>> Handle(ListKioskCommandsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        return [.. (await _commands.ListRecentAsync(kiosk.Id, 20, cancellationToken).ConfigureAwait(false)).Select(View)];
    }

    /// <summary>
    /// A test page came out (or did not). Paper comes off the counter either
    /// way it used some, and a kiosk waiting for its first good print goes
    /// into service (plan section 7.7: NotConfigured → Online).
    /// </summary>
    private async Task ApplyTestPrintAsync(
        Kiosk kiosk, CompleteKioskCommandCommand request, ActorContext actor, DateTime now, CancellationToken cancellationToken)
    {
        var changed = false;

        // A test page is one sheet or two; a report of more is capped.
        if (kiosk.UsePaper("A4", Math.Min(request.SheetsUsed, 10), SupplyEventType.TestPrint, now, commandId: request.CommandId) is { } movement)
        {
            await _supplies.AddAsync(movement, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        if (request.Succeeded && kiosk.Status == KioskStatus.NotConfigured && kiosk.SelectedPrinter is not null)
        {
            kiosk.MoveTo(kiosk.WorkingStatus, actor.Because("kiosks.reason.testPrintPassed"), now);
            changed = true;
        }

        if (changed)
        {
            kiosk.ConfigurationChangedAt = now;
            await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
            await _kiosk.KioskConfigChangedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Same(IReadOnlyList<AvailablePrinter> before, IReadOnlyList<AvailablePrinter> after)
        => before.Count == after.Count
           && before.Zip(after).All(pair =>
               pair.First.Name == pair.Second.Name
               && pair.First.IsOnline == pair.Second.IsOnline
               && pair.First.DriverName == pair.Second.DriverName
               && pair.First.DetectedCapabilities.Color == pair.Second.DetectedCapabilities.Color
               && pair.First.DetectedCapabilities.Duplex == pair.Second.DetectedCapabilities.Duplex
               && pair.First.DetectedCapabilities.A3 == pair.Second.DetectedCapabilities.A3);

    private static PrinterCapabilities Detected(Kiosk kiosk)
        => kiosk.SelectedPrinter is null
            ? new PrinterCapabilities()
            : kiosk.AvailablePrinters.FirstOrDefault(printer => printer.Name == kiosk.SelectedPrinter.Name)?.DetectedCapabilities
              ?? new PrinterCapabilities();

    private static KioskPrintersView PrintersView(Kiosk kiosk)
    {
        var detected = Detected(kiosk);
        var effective = kiosk.EffectiveCapabilities();

        return new KioskPrintersView(
            kiosk.Id,
            kiosk.Status.ToString(),
            kiosk.SelectedPrinter?.Name,
            [
                .. kiosk.AvailablePrinters.Select(printer => new AvailablePrinterDto(
                    printer.Name,
                    printer.DriverName,
                    printer.IsOnline,
                    new PrinterCapabilitiesDto(
                        printer.DetectedCapabilities.Color,
                        printer.DetectedCapabilities.Duplex,
                        printer.DetectedCapabilities.A3))),
            ],
            new PrinterCapabilitiesDto(detected.Color, detected.Duplex, detected.A3),
            new CapabilityOverridesDto(kiosk.CapabilityOverrides.Color, kiosk.CapabilityOverrides.Duplex, kiosk.CapabilityOverrides.A3),
            new PrinterCapabilitiesDto(effective.Color, effective.Duplex, effective.A3));
    }

    private static KioskCommandView View(KioskCommand command) => new(
        command.Id,
        command.KioskId,
        command.Type.ToString(),
        command.Status.ToString(),
        Messages.WriteStored(command.Result),
        command.CreatedAt,
        command.CompletedAt);

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

    private async Task<Kiosk> VisibleKioskAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null || !effective.CanSee(kiosk.Id) ? throw new NotFoundException("kiosk") : kiosk;
    }
}
