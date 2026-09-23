using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Devices;

/// <summary>
/// Finds a pending device from the code an admin typed in, or from the content
/// of the activation QR an operator scanned (plan sections 2.9, 7.11).
/// </summary>
public sealed class LookupDeviceHandler : IRequestHandler<LookupDeviceCommand, DeviceDetails>
{
    /// <summary>QR content: <c>neoprint://activate/{deviceId}?code={pairingCode}</c>.</summary>
    private const string ActivateScheme = "neoprint://activate/";

    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly ISecretHasher _secrets;
    private readonly IClock _clock;
    private readonly ILogger<LookupDeviceHandler> _logger;

    public LookupDeviceHandler(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        ISecretHasher secrets,
        IClock clock,
        ILogger<LookupDeviceHandler> logger)
    {
        _devices = devices;
        _kiosks = kiosks;
        _secrets = secrets;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DeviceDetails> Handle(LookupDeviceCommand request, CancellationToken cancellationToken)
    {
        var (deviceId, code) = Parse(request.CodeOrQr.Trim());

        KioskDevice? device;

        if (deviceId is not null)
        {
            device = await _devices.FindByIdAsync(deviceId.Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Only the hash is stored, so every pending device is checked. The
            // list is capped at a thousand, and the endpoint is rate limited.
            var pending = await _devices.ListAsync(DeviceStatus.Pending, cancellationToken).ConfigureAwait(false);
            var normalized = KioskDevice.NormalizePairingCode(code!);

            device = pending.FirstOrDefault(candidate =>
                _secrets.VerifySecret(normalized, candidate.PairingCodeHash));
        }

        if (device is null || !device.IsPairingCodeValid(_clock.UtcNow))
        {
            // Failed lookups are audited (plan section 7.11).
            _logger.LogWarning("A device lookup failed for the supplied activation code.");
            throw new NotFoundException("Device", request.CodeOrQr);
        }

        Kiosk? kiosk = null;
        if (device.KioskId is not null)
        {
            kiosk = await _kiosks.FindByIdAsync(device.KioskId.Value, cancellationToken).ConfigureAwait(false);
        }

        return new DeviceDetails(
            device.Id,
            device.Status,
            device.Platform,
            device.AppVersion,
            RegisterDeviceHandler.Map(device.Fingerprint),
            [.. device.ReportedPrinters.Select(RegisterDeviceHandler.Map)],
            device.LastIp,
            device.CreatedAt,
            device.LastSeenAt,
            kiosk?.Id,
            kiosk?.Code);
    }

    /// <summary>Accepts either a bare pairing code or the full activation QR.</summary>
    private static (Guid? DeviceId, string? Code) Parse(string input)
    {
        if (!input.StartsWith(ActivateScheme, StringComparison.OrdinalIgnoreCase))
        {
            return (null, input);
        }

        var rest = input[ActivateScheme.Length..];
        var separator = rest.IndexOf('?', StringComparison.Ordinal);
        var idPart = separator < 0 ? rest : rest[..separator];

        return Guid.TryParse(idPart, out var parsed) ? (parsed, null) : (null, input);
    }
}

/// <summary>
/// Activates a device, either onto an existing kiosk or as a new one
/// (plan sections 7.11, 9.0).
/// </summary>
public sealed class ActivateDeviceHandler : IRequestHandler<ActivateDeviceCommand, ActivateDeviceResult>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly ISecretHasher _secrets;
    private readonly IAuditWriter _audit;
    private readonly IDeviceNotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger<ActivateDeviceHandler> _logger;

    public ActivateDeviceHandler(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        ISecretHasher secrets,
        IAuditWriter audit,
        IDeviceNotifier notifier,
        IClock clock,
        ILogger<ActivateDeviceHandler> logger)
    {
        _devices = devices;
        _kiosks = kiosks;
        _secrets = secrets;
        _audit = audit;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ActivateDeviceResult> Handle(
        ActivateDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Device", request.DeviceId);

        var kiosk = request.KioskId is not null
            ? await _kiosks.FindByIdAsync(request.KioskId.Value, cancellationToken).ConfigureAwait(false)
              ?? throw new NotFoundException("Kiosk", request.KioskId.Value)
            : await CreateKioskAsync(request.NewKiosk!, now, cancellationToken).ConfigureAwait(false);

        // Binding to a kiosk that already has a device is how hardware is
        // replaced; the old device is revoked so its key stops working.
        if (kiosk.ActiveDeviceId is { } previousId && previousId != device.Id)
        {
            var previous = await _devices.FindByIdAsync(previousId, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                previous.Revoke(request.ActivatedBy, "Kiosk üçün yeni cihaz aktivləşdirildi.", now);
                await _devices.UpdateAsync(previous, cancellationToken).ConfigureAwait(false);
                await _notifier.DeviceRevokedAsync(previous.Id, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Device {PreviousDeviceId} was revoked because {DeviceId} took over kiosk {KioskCode}.",
                    previousId, device.Id, kiosk.Code);
            }
        }

        device.Activate(kiosk.Id, request.ActivatedBy, now);
        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.SelectedPrinterName))
        {
            // The admin can pick a printer straight from the list the device
            // reported before activation, so the kiosk comes up ready.
            kiosk.AvailablePrinters = [.. device.ReportedPrinters];
            kiosk.SelectedPrinter = new SelectedPrinter
            {
                Name = request.SelectedPrinterName,
                SelectedBy = request.ActivatedBy,
                SelectedAt = now,
            };
        }

        kiosk.BindDevice(device.Id, device.Platform, device.AppVersion);
        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.ActivatedBy,
                    "devices.activate",
                    "KioskDevice",
                    device.Id.ToString(),
                    Before: null,
                    After: $$"""{"kioskId":"{{kiosk.Id}}","kioskCode":"{{kiosk.Code}}"}""",
                    request.Ip,
                    Reason: null),
                cancellationToken)
            .ConfigureAwait(false);

        // Tells the kiosk, which is sitting on the activation screen, to ask
        // for a kiosk token (plan section 7.11).
        await _notifier.DeviceActivatedAsync(device.Id, kiosk.Id, cancellationToken).ConfigureAwait(false);

        return new ActivateDeviceResult(device.Id, kiosk.Id, kiosk.Code, kiosk.Status);
    }

    private async Task<Kiosk> CreateKioskAsync(
        NewKioskParameters parameters, DateTime now, CancellationToken cancellationToken)
    {
        var code = parameters.Code;

        if (string.IsNullOrWhiteSpace(code))
        {
            var highest = await _kiosks.GetHighestCodeSequenceAsync(cancellationToken).ConfigureAwait(false);
            code = Kiosk.NextCode(highest + 1);
        }
        else if (await _kiosks.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException($"Kiosk kodu '{code}' artıq istifadə olunur.");
        }

        var kiosk = new Kiosk
        {
            Code = code,
            Name = parameters.Name,
            Location = new KioskLocation
            {
                Lat = parameters.Lat,
                Lng = parameters.Lng,
                Address = parameters.Address,
            },
            Status = KioskStatus.NotActivated,
            PricingProfileId = parameters.PricingProfileId,
            MaxFileSizeMb = parameters.MaxFileSizeMb,
            MaxPages = parameters.MaxPages,
            QueueDisplayLimit = parameters.QueueDisplayLimit,
            DefaultLanguage = parameters.DefaultLanguage,
            FiscalTerminalId = parameters.FiscalTerminalId,
            // Only the hash ever reaches the kiosk (plan sections 7.1, 2.13).
            AdminPinHash = _secrets.HashSecret(parameters.AdminPin.Trim()),
            CreatedAt = now,
        };

        kiosk.PaperStock["A4"] = new PaperStock { Capacity = parameters.PaperCapacityA4, Current = 0 };
        kiosk.PaperStock["A3"] = new PaperStock { Capacity = parameters.PaperCapacityA3, Current = 0 };

        await _kiosks.AddAsync(kiosk, cancellationToken).ConfigureAwait(false);
        return kiosk;
    }
}

/// <summary>
/// Detaches a device (plan section 7.11).
/// </summary>
/// <remarks>
/// The kiosk goes back to NotActivated but keeps its code, configuration and
/// counters. The device is told over SignalR so it can wipe its local cache and
/// return to the activation screen without waiting for its token to expire.
/// </remarks>
public sealed class RevokeDeviceHandler : IRequestHandler<RevokeDeviceCommand, Unit>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IAuditWriter _audit;
    private readonly IDeviceNotifier _notifier;
    private readonly IKioskTokenBlocklist _blocklist;
    private readonly IClock _clock;

    public RevokeDeviceHandler(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IAuditWriter audit,
        IDeviceNotifier notifier,
        IKioskTokenBlocklist blocklist,
        IClock clock)
    {
        _devices = devices;
        _kiosks = kiosks;
        _audit = audit;
        _notifier = notifier;
        _blocklist = blocklist;
        _clock = clock;
    }

    public async Task<Unit> Handle(RevokeDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Device", request.DeviceId);

        var kioskId = device.KioskId;

        device.Revoke(request.RevokedBy, request.Reason, now);
        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        if (kioskId is not null)
        {
            var kiosk = await _kiosks.FindByIdAsync(kioskId.Value, cancellationToken).ConfigureAwait(false);
            if (kiosk is not null && kiosk.ActiveDeviceId == device.Id)
            {
                kiosk.UnbindDevice();
                await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
            }
        }

        // The kiosk token lives an hour; blocklisting it makes the revocation
        // immediate (plan section 7.11).
        await _blocklist.BlockAsync(device.Id, TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.RevokedBy,
                    "devices.revoke",
                    "KioskDevice",
                    device.Id.ToString(),
                    Before: null,
                    After: null,
                    request.Ip,
                    request.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        await _notifier.DeviceRevokedAsync(device.Id, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }
}

/// <summary>
/// Creates a single-use enrollment token for zero-touch activation
/// (plan section 7.11).
/// </summary>
public sealed class CreateEnrollmentTokenHandler
    : IRequestHandler<CreateEnrollmentTokenCommand, EnrollmentTokenResult>
{
    private readonly IEnrollmentTokenRepository _tokens;
    private readonly IKioskRepository _kiosks;
    private readonly ISecretHasher _secrets;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;

    public CreateEnrollmentTokenHandler(
        IEnrollmentTokenRepository tokens,
        IKioskRepository kiosks,
        ISecretHasher secrets,
        ISettingsReader settings,
        IClock clock)
    {
        _tokens = tokens;
        _kiosks = kiosks;
        _secrets = secrets;
        _settings = settings;
        _clock = clock;
    }

    public async Task<EnrollmentTokenResult> Handle(
        CreateEnrollmentTokenCommand request, CancellationToken cancellationToken)
    {
        if (request.KioskId is null && request.NewKiosk is null)
        {
            throw new DomainException(
                "Enrollment token ya mövcud kiosk üçün, ya da yeni kiosk şablonu ilə yaradılmalıdır.");
        }

        Kiosk? kiosk = null;
        NewKioskTemplate? template = null;

        if (request.KioskId is not null)
        {
            kiosk = await _kiosks.FindByIdAsync(request.KioskId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("Kiosk", request.KioskId.Value);
        }
        else
        {
            var parameters = request.NewKiosk!;
            template = new NewKioskTemplate
            {
                Name = parameters.Name,
                Code = parameters.Code,
                Location = new KioskLocation
                {
                    Lat = parameters.Lat,
                    Lng = parameters.Lng,
                    Address = parameters.Address,
                },
                PricingProfileId = parameters.PricingProfileId,
                MaxFileSizeMb = parameters.MaxFileSizeMb,
                MaxPages = parameters.MaxPages,
                QueueDisplayLimit = parameters.QueueDisplayLimit,
                DefaultLanguage = parameters.DefaultLanguage,
                FiscalTerminalId = parameters.FiscalTerminalId,
                AdminPinHash = _secrets.HashSecret(parameters.AdminPin.Trim()),
                PaperCapacity =
                {
                    ["A4"] = parameters.PaperCapacityA4,
                    ["A3"] = parameters.PaperCapacityA3,
                },
            };
        }

        var ttlHours = request.TtlHours
            ?? await _settings.GetIntAsync(SettingKeys.EnrollmentTokenTtlHours, 72, cancellationToken)
                .ConfigureAwait(false);

        var raw = EnrollmentToken.NewToken();
        var token = new EnrollmentToken
        {
            TokenHash = _secrets.HashToken(raw),
            KioskId = kiosk?.Id,
            NewKioskTemplate = template,
            ExpiresAt = _clock.UtcNow.AddHours(ttlHours),
            CreatedBy = request.CreatedBy,
            CreatedAt = _clock.UtcNow,
        };

        await _tokens.AddAsync(token, cancellationToken).ConfigureAwait(false);

        // The ready-to-paste provisioning commands from plan section 7.11.
        return new EnrollmentTokenResult(
            token.Id,
            raw,
            token.ExpiresAt,
            kiosk?.Id,
            kiosk?.Code,
            $"provision.ps1 -EnrollmentToken {raw}",
            $"sudo ./provision.sh --enrollment-token {raw}");
    }
}

/// <summary>Revokes an unused enrollment token.</summary>
public sealed class RevokeEnrollmentTokenHandler : IRequestHandler<RevokeEnrollmentTokenCommand, Unit>
{
    private readonly IEnrollmentTokenRepository _tokens;
    private readonly IClock _clock;

    public RevokeEnrollmentTokenHandler(IEnrollmentTokenRepository tokens, IClock clock)
    {
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<Unit> Handle(RevokeEnrollmentTokenCommand request, CancellationToken cancellationToken)
    {
        var tokens = await _tokens.ListAsync(cancellationToken).ConfigureAwait(false);
        var token = tokens.FirstOrDefault(candidate => candidate.Id == request.Id)
            ?? throw new NotFoundException("EnrollmentToken", request.Id);

        if (token.RevokedAt is null)
        {
            token.RevokedAt = _clock.UtcNow;
            await _tokens.UpdateAsync(token, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}
