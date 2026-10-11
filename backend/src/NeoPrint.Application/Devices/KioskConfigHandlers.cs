using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Features;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Features;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Devices;

/// <inheritdoc cref="GetKioskConfigQuery" />
public sealed class GetKioskConfigHandler : IRequestHandler<GetKioskConfigQuery, KioskConfigResponse?>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly ISettingsReader _settings;
    private readonly IFeatureFlagEvaluator _features;
    private readonly IClock _clock;
    private readonly Pricing.IPricingProfileRepository _profiles;

    public GetKioskConfigHandler(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        ISettingsReader settings,
        IFeatureFlagEvaluator features,
        IClock clock,
        Pricing.IPricingProfileRepository profiles)
    {
        _profiles = profiles;
        _devices = devices;
        _kiosks = kiosks;
        _settings = settings;
        _features = features;
        _clock = clock;
    }

    public async Task<KioskConfigResponse?> Handle(
        GetKioskConfigQuery request, CancellationToken cancellationToken)
    {
        var device = await _devices
            .FindByIdAsync(request.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device?.KioskId is null)
        {
            // Not bound to a kiosk, so there is no configuration to give it.
            // The caller answers with a refusal rather than an empty one.
            return null;
        }

        var kiosk = await _kiosks
            .FindByIdAsync(device.KioskId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (kiosk is null)
        {
            return null;
        }

        var capabilities = kiosk.EffectiveCapabilities();

        // The effective limit is the kiosk's own, or the platform default
        // (plan section 2.2).
        var maxFileSize = kiosk.MaxFileSizeMb
                          ?? await _settings
                              .GetIntAsync(SettingKeys.FilesDefaultMaxSizeMb, 50, cancellationToken)
                              .ConfigureAwait(false);

        var maxPages = kiosk.MaxPages
                       ?? await _settings
                           .GetIntAsync(SettingKeys.FilesDefaultMaxPages, 200, cancellationToken)
                           .ConfigureAwait(false);

        var queueLimit = kiosk.QueueDisplayLimit
                         ?? await _settings
                             .GetIntAsync(SettingKeys.QueueDisplayLimit, 5, cancellationToken)
                             .ConfigureAwait(false);

        var features = await _features
            .EvaluateAllAsync(FeaturePlatform.Kiosk, kiosk.Id, userId: null, cancellationToken)
            .ConfigureAwait(false);

        var profile = (kiosk.PricingProfileId is { } profileId
                          ? await _profiles.FindByIdAsync(profileId, cancellationToken).ConfigureAwait(false)
                          : null)
                      ?? await _profiles.FindDefaultAsync(cancellationToken).ConfigureAwait(false);

        var table = profile?.ToPriceTable();

        var allowed = await _settings.GetAsync(SettingKeys.FilesAllowedFormats, cancellationToken).ConfigureAwait(false);

        return new KioskConfigResponse(
            kiosk.Id,
            kiosk.Code,
            kiosk.Name,
            kiosk.Status,
            kiosk.DefaultLanguage,
            kiosk.SelectedPrinter?.Name,
            new PrinterCapabilitiesDto(capabilities.Color, capabilities.Duplex, capabilities.A3),
            [.. kiosk.AvailablePrinters.Select(printer => new AvailablePrinterDto(
                printer.Name,
                printer.DriverName,
                printer.IsOnline,
                new PrinterCapabilitiesDto(
                    printer.DetectedCapabilities.Color,
                    printer.DetectedCapabilities.Duplex,
                    printer.DetectedCapabilities.A3)))],
            // A PBKDF2 hash, not the PIN: the kiosk compares against it, and a
            // stolen kiosk yields nothing usable (plan sections 7.1, 2.13).
            kiosk.AdminPinHash,
            queueLimit,
            maxFileSize,
            maxPages,
            kiosk.PricingProfileId,
            kiosk.PaperStock.ToDictionary(
                stock => stock.Key,
                stock => new PaperStockDto(stock.Value.Capacity, stock.Value.Current),
                StringComparer.Ordinal),
            features,
            await _settings
                .GetIntAsync(SettingKeys.KioskHeartbeatSeconds, 15, cancellationToken)
                .ConfigureAwait(false),
            await _settings
                .GetIntAsync(SettingKeys.KioskConfigCacheTtlHours, 24, cancellationToken)
                .ConfigureAwait(false),
            _clock.UtcNow,
            table is null
                ? null
                : new KioskPricesDto(
                    profile!.Name,
                    table.BwPerSide,
                    table.ColorPerSide,
                    table.PaperMultipliers,
                    table.MinOrder,
                    [.. table.Tiers.Select(tier => new KioskPriceTierDto(tier.MinSides, tier.DiscountPercent))],
                    table.Currency),
            // Empty means the whole catalogue, as on upload (plan section 2.2).
            string.IsNullOrWhiteSpace(allowed)
                ? null
                : [.. allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            await _settings.GetBoolAsync(SettingKeys.PaperBlockWhenInsufficient, true, cancellationToken).ConfigureAwait(false),
            await _settings.GetIntAsync(SettingKeys.KioskIdleResetSeconds, 90, cancellationToken).ConfigureAwait(false),
            await _settings.GetIntAsync(SettingKeys.KioskTelemetrySeconds, 30, cancellationToken).ConfigureAwait(false));
    }
}

/// <inheritdoc cref="KioskHeartbeatCommand" />
public sealed class KioskHeartbeatHandler : IRequestHandler<KioskHeartbeatCommand, KioskHeartbeatResult>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public KioskHeartbeatHandler(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        ISettingsReader settings,
        ICorrelationContext correlation,
        IClock clock)
    {
        _devices = devices;
        _kiosks = kiosks;
        _settings = settings;
        _correlation = correlation;
        _clock = clock;
    }

    public async Task<KioskHeartbeatResult> Handle(
        KioskHeartbeatCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var device = await _devices
            .FindByIdAsync(request.DeviceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("device");

        if (device.KioskId is null)
        {
            throw new DomainException("devices.notBound");
        }

        device.LastSeenAt = now;
        device.LastIp = request.Ip;

        if (!string.IsNullOrWhiteSpace(request.AppVersion))
        {
            device.AppVersion = request.AppVersion;
        }

        if (!string.IsNullOrWhiteSpace(request.AgentVersion))
        {
            device.AgentVersion = request.AgentVersion;
        }

        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        var kiosk = await _kiosks
            .FindByIdAsync(device.KioskId.Value, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("kiosk");

        kiosk.LastHeartbeatAt = now;
        kiosk.AppVersion = device.AppVersion;

        // A kiosk that went Offline because its heartbeat stopped comes back on
        // the next one, with no operator involved (plan section 7.9). Paused,
        // Maintenance and Disabled are decisions somebody made, so a heartbeat
        // does not undo them.
        if (kiosk.Status == KioskStatus.Offline)
        {
            // Back, but an administrator's pause is still theirs to lift.
            var recovered = kiosk.SelectedPrinter is null && kiosk.PausedByAdminSince is null
                ? KioskStatus.NotConfigured
                : kiosk.WorkingStatus;

            kiosk.MoveTo(
                recovered,
                ActorContext.Kiosk(device.Id, "Heartbeat resumed", _correlation.CorrelationId),
                now);
        }

        // Safe mode is the kiosk's own report, so the heartbeat both sets and
        // clears it (plan section 7.12).
        var actor = ActorContext.Kiosk(device.Id, correlationId: _correlation.CorrelationId);

        if (request.SafeMode)
        {
            kiosk.EnterSafeMode(actor, now);
        }
        else
        {
            kiosk.LeaveSafeMode(actor, now);
        }

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        return new KioskHeartbeatResult(
            kiosk.Status,
            await _settings
                .GetIntAsync(SettingKeys.KioskHeartbeatSeconds, 15, cancellationToken)
                .ConfigureAwait(false),
            // The kiosk refetches its configuration when this moves past what
            // it last saw, rather than polling the whole document.
            kiosk.ConfigurationChangedAt,
            now);
    }
}
