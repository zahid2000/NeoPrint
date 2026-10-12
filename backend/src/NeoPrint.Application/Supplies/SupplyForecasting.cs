using System.Globalization;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

/// <summary>What a printer last said about one of its supplies.</summary>
public sealed record SupplyLevelView(string Resource, string? Variant, string Name, int Percent, DateTime ReadAt);

/// <summary>When a supply is expected to run out (plan section 2.15, "Proqnoz").</summary>
/// <param name="PerDay">Sheets, or percent of a cartridge, used in a day - the weighted average of the last fourteen.</param>
/// <param name="RunsOutAt">Null when nothing is being used or the end is more than a year away.</param>
public sealed record SupplyForecastView(string Resource, string? Variant, string Unit, decimal Current, decimal PerDay, DateTime? RunsOutAt);

/// <summary>
/// <c>SupplyForecastJob</c> (plan section 6, hourly): a supply that is
/// expected to run out within <c>Service.ForecastLeadHours</c> gets its
/// service task now, whether or not a threshold has been crossed.
/// </summary>
/// <returns>How many tasks were opened.</returns>
public sealed record RunSupplyForecastCommand : IRequest<int>;

/// <summary>
/// A kiosk's supplies as they stand and as they are going: the printer's
/// levels, and the forecast for each tray and each cartridge.
/// </summary>
public sealed class SupplyOutlook
{
    /// <summary>One reading per this many hours is enough to see a cartridge emptying.</summary>
    private const int SliceSeconds = 6 * 60 * 60;

    private readonly ISupplyUsageReader _usage;
    private readonly IPrinterTelemetryStore _telemetry;

    public SupplyOutlook(ISupplyUsageReader usage, IPrinterTelemetryStore telemetry)
    {
        _usage = usage;
        _telemetry = telemetry;
    }

    /// <summary>The selected printer's supplies, from its latest reading; empty when it has said nothing lately.</summary>
    public async Task<IReadOnlyList<SupplyLevelView>> LevelsAsync(Kiosk kiosk, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);

        var sample = await LatestAsync(kiosk, now, cancellationToken).ConfigureAwait(false);

        return sample is null
            ? []
            :
            [
                .. sample.Supplies
                    .Where(supply => supply.LevelPercent is not null)
                    .Select(supply => new SupplyLevelView(
                        PrinterSupplies.ResourceOf(supply).ToString(),
                        PrinterSupplies.VariantOf(supply),
                        supply.Name,
                        supply.LevelPercent!.Value,
                        sample.Timestamp)),
            ];
    }

    /// <summary>A forecast for each tray that has a capacity and each supply the printer reports a level for.</summary>
    public async Task<IReadOnlyList<SupplyForecastView>> ForecastAsync(Kiosk kiosk, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);

        var forecasts = new List<SupplyForecastView>();

        foreach (var (paper, stock) in kiosk.PaperStock.Where(tray => tray.Value.Capacity > 0).OrderBy(tray => tray.Key, StringComparer.Ordinal))
        {
            var resource = SupplyEvent.PaperResource(paper);
            var daily = await _usage.DailySheetsAsync(kiosk.Id, resource, now, SupplyForecaster.Days, cancellationToken).ConfigureAwait(false);
            var forecast = SupplyForecaster.ForPaper(stock.Current, daily, now);

            forecasts.Add(new SupplyForecastView(resource, null, SupplyUnit.Sheets.ToString(), stock.Current, Math.Round(forecast.PerDay, 1), forecast.RunsOutAt));
        }

        var sample = await LatestAsync(kiosk, now, cancellationToken).ConfigureAwait(false);

        if (sample is null || kiosk.SelectedPrinter is not { } printer)
        {
            return forecasts;
        }

        // A cartridge empties with every sheet, whatever its size.
        var sheets = await _usage.DailySheetsAsync(kiosk.Id, null, now, SupplyForecaster.Days, cancellationToken).ConfigureAwait(false);
        var history = await _telemetry
            .SlicesAsync(kiosk.Id, printer.Name, now.AddDays(-SupplyForecaster.Days), now, SliceSeconds, cancellationToken)
            .ConfigureAwait(false);

        foreach (var supply in sample.Supplies.Where(supply => supply.LevelPercent is not null))
        {
            var levels = history
                .Select(reading => reading.Supplies.FirstOrDefault(other => string.Equals(other.Name, supply.Name, StringComparison.OrdinalIgnoreCase))?.LevelPercent)
                .Where(level => level is not null)
                .Select(level => level!.Value)
                .Append(supply.LevelPercent!.Value)
                .ToList();
            var forecast = SupplyForecaster.ForPercent(supply.LevelPercent.Value, levels, sheets.Sum(), sheets, now);

            forecasts.Add(new SupplyForecastView(
                PrinterSupplies.ResourceOf(supply).ToString(),
                PrinterSupplies.VariantOf(supply),
                SupplyUnit.Percent.ToString(),
                supply.LevelPercent.Value,
                Math.Round(forecast.PerDay, 2),
                forecast.RunsOutAt));
        }

        return forecasts;
    }

    private async Task<PrinterTelemetrySample?> LatestAsync(Kiosk kiosk, DateTime now, CancellationToken cancellationToken)
    {
        if (kiosk.SelectedPrinter is not { } printer)
        {
            return null;
        }

        var sample = await _telemetry.LatestAsync(kiosk.Id, printer.Name, cancellationToken).ConfigureAwait(false);

        return sample is null || now - sample.Timestamp > SupplyEvaluationHandlers.ReadingGoodFor ? null : sample;
    }
}

public sealed class SupplyForecastHandler : IRequestHandler<RunSupplyForecastCommand, int>
{
    private const string WhenFormat = "yyyy-MM-dd HH:mm";

    private readonly IKioskRepository _kiosks;
    private readonly SupplyOutlook _outlook;
    private readonly ServiceTaskDesk _desk;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<SupplyForecastHandler> _logger;

    public SupplyForecastHandler(
        IKioskRepository kiosks,
        SupplyOutlook outlook,
        ServiceTaskDesk desk,
        ISettingsReader settings,
        IClock clock,
        ILogger<SupplyForecastHandler> logger)
    {
        _kiosks = kiosks;
        _outlook = outlook;
        _desk = desk;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> Handle(RunSupplyForecastCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lead = TimeSpan.FromHours(Math.Max(
            1,
            await _settings.GetIntAsync(SettingKeys.ServiceForecastLeadHours, 24, cancellationToken).ConfigureAwait(false)));
        var now = _clock.UtcNow;
        var opened = 0;

        foreach (var kiosk in (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
                     .Where(kiosk => kiosk.ActiveDeviceId is not null && kiosk.Status is not (KioskStatus.NotActivated or KioskStatus.Disabled)))
        {
            try
            {
                foreach (var forecast in await _outlook.ForecastAsync(kiosk, now, cancellationToken).ConfigureAwait(false))
                {
                    if (forecast.RunsOutAt is not { } runsOutAt || runsOutAt - now > lead)
                    {
                        continue;
                    }

                    var resource = Enum.Parse<SupplyResource>(forecast.Resource);
                    var when = runsOutAt.ToString(WhenFormat, CultureInfo.InvariantCulture);
                    var wanted = ServiceTask.Open(
                        kiosk.Id,
                        ServiceTask.TypeFor(resource),
                        TaskPriority.Normal,
                        ServiceTaskSource.Forecast,
                        now,
                        resource,
                        forecast.Variant,
                        UserMessage.Of(
                                "serviceTasks.note.forecast",
                                ("supply", forecast.Resource),
                                ("variant", SupplyEvaluationHandlers.VariantText(forecast.Variant)),
                                ("when", when))
                            .Encode(),
                        dueAt: runsOutAt);

                    // The same key a crossed rule uses: a tray that is both low and running out is one task.
                    var (_, isNew) = await _desk.EnsureAsync(kiosk, wanted, now, cancellationToken).ConfigureAwait(false);

                    if (isNew)
                    {
                        opened++;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The supply forecast of kiosk {KioskId} could not be made; the next run tries again.", kiosk.Id);
            }
        }

        return opened;
    }
}
