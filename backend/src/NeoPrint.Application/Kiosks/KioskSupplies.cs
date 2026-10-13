using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

/// <summary>The movements of a kiosk's paper counters (plan section 2.15, <c>supply_events</c>).</summary>
public interface ISupplyEventRepository
{
    /// <summary>Adds a movement; its <see cref="SupplyEvent.DedupKey"/> is unique.</summary>
    Task AddAsync(SupplyEvent supplyEvent, CancellationToken cancellationToken = default);

    /// <summary>The newest first.</summary>
    Task<IReadOnlyList<SupplyEvent>> ListForKioskAsync(Guid kioskId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// "Kağız doldur" (plan section 2.15): sheets added, the tray filled to its
/// capacity, or the tray counted. The operator panel's own screen is plan
/// step 7; this is the admin panel's.
/// </summary>
public sealed record RefillKioskPaperCommand(Guid AdminUserId, Guid KioskId, string Paper, PaperRefillMode Mode, int Sheets)
    : IRequest<KioskSuppliesView>, ITransactionalRequest;

/// <summary>The supplies tab of the kiosk detail (plan section 8A.3, "Sərf").</summary>
public sealed record GetKioskSuppliesQuery(Guid AdminUserId, Guid KioskId, int Limit = 50) : IRequest<KioskSuppliesView>;

/// <param name="Paper">The counters by size: capacity, sheets in the tray, last refill.</param>
/// <param name="Events">The latest movements, newest first.</param>
/// <param name="Levels">What the selected printer last said about its toner and the like.</param>
/// <param name="Forecasts">When each tray and each cartridge is expected to run out (plan section 2.15).</param>
/// <param name="Alerts">What is below its rule now.</param>
public sealed record KioskSuppliesView(
    IReadOnlyDictionary<string, PaperStockDto> Paper,
    IReadOnlyList<SupplyEventView> Events,
    IReadOnlyList<Supplies.SupplyLevelView> Levels,
    IReadOnlyList<Supplies.SupplyForecastView> Forecasts,
    IReadOnlyList<Supplies.SupplyAlertView> Alerts);

public sealed record SupplyEventView(string Resource, string Type, int Delta, int Before, int After, Guid? JobId, Guid? By, DateTime At);

/// <inheritdoc cref="RefillKioskPaperCommand" />
public sealed class RefillKioskPaperValidator : IValidator<RefillKioskPaperCommand>
{
    public IEnumerable<ValidationError> Validate(RefillKioskPaperCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Paper is not ("A4" or "A3"))
        {
            yield return new ValidationError("paper", "printJobs.paperInvalid");
        }

        if (!Enum.IsDefined(request.Mode))
        {
            yield return new ValidationError("mode", "kiosks.paperRefillModeInvalid");
        }

        if (request.Sheets is < 0 or > 100_000)
        {
            yield return new ValidationError("sheets", "kiosks.paperSheetsInvalid");
        }
    }
}

/// <summary>Paper refills and the supplies tab.</summary>
public sealed class KioskSupplyHandlers :
    IRequestHandler<RefillKioskPaperCommand, KioskSuppliesView>,
    IRequestHandler<GetKioskSuppliesQuery, KioskSuppliesView>
{
    private readonly IPermissionResolver _permissions;
    private readonly IKioskRepository _kiosks;
    private readonly ISupplyEventRepository _supplies;
    private readonly IKioskNotifier _kiosk;
    private readonly Supplies.SupplyOutlook _outlook;
    private readonly Supplies.ISupplyAlertRepository _alerts;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public KioskSupplyHandlers(
        IPermissionResolver permissions,
        IKioskRepository kiosks,
        ISupplyEventRepository supplies,
        IKioskNotifier kiosk,
        Supplies.SupplyOutlook outlook,
        Supplies.ISupplyAlertRepository alerts,
        IAuditWriter audit,
        IClock clock)
    {
        _permissions = permissions;
        _kiosks = kiosks;
        _supplies = supplies;
        _kiosk = kiosk;
        _outlook = outlook;
        _alerts = alerts;
        _audit = audit;
        _clock = clock;
    }

    /// <inheritdoc cref="RefillKioskPaperCommand" />
    public async Task<KioskSuppliesView> Handle(RefillKioskPaperCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var movement = kiosk.RefillPaper(request.Paper, request.Mode, request.Sheets, request.AdminUserId, _clock.UtcNow);

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
        await _supplies.AddAsync(movement, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "paper.refill",
                    nameof(Kiosk),
                    kiosk.Id.ToString(),
                    $"{request.Paper}:{movement.Before}",
                    $"{request.Paper}:{movement.After} ({request.Mode})",
                    null,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        // The kiosk checks orders against the tray; it fetches the new count now.
        await _kiosk.KioskConfigChangedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(kiosk, 50, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="GetKioskSuppliesQuery" />
    public async Task<KioskSuppliesView> Handle(GetKioskSuppliesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(kiosk, Math.Clamp(request.Limit, 1, 200), cancellationToken).ConfigureAwait(false);
    }

    private async Task<KioskSuppliesView> ViewAsync(Kiosk kiosk, int limit, CancellationToken cancellationToken)
    {
        var events = await _supplies.ListForKioskAsync(kiosk.Id, limit, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var alerts = await _alerts.ListActiveForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return new KioskSuppliesView(
            kiosk.PaperStock.ToDictionary(
                pair => pair.Key,
                pair => PaperStockDto.Of(pair.Value),
                StringComparer.Ordinal),
            [
                .. events.Select(movement => new SupplyEventView(
                    movement.Resource, movement.Type.ToString(), movement.Delta, movement.Before, movement.After, movement.JobId, movement.By, movement.At)),
            ],
            await _outlook.LevelsAsync(kiosk, now, cancellationToken).ConfigureAwait(false),
            await _outlook.ForecastAsync(kiosk, now, cancellationToken).ConfigureAwait(false),
            [.. alerts.OrderByDescending(alert => alert.Level).Select(alert => Supplies.SupplyAlertView.Of(alert, kiosk.Code))]);
    }

    private async Task<Kiosk> VisibleKioskAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null || !effective.CanSee(kiosk.Id) ? throw new NotFoundException("kiosk") : kiosk;
    }
}
