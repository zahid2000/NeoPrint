using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Devices;

/// <summary>
/// Registers a device on first start (plan section 7.11).
/// </summary>
/// <remarks>
/// A device that has registered before - after a restart, an update or a
/// reinstall - is recognised by its public key and keeps its identity. That is
/// what makes an update or a reboot cost nothing.
/// </remarks>
public sealed class RegisterDeviceHandler : IRequestHandler<RegisterDeviceCommand, RegisterDeviceResult>
{
    /// <summary>Upper bound on devices waiting to be activated (plan section 7.11).</summary>
    public const int MaxPendingDevices = 1000;

    private readonly IKioskDeviceRepository _devices;
    private readonly IEnrollmentTokenRepository _enrollmentTokens;
    private readonly IKioskRepository _kiosks;
    private readonly ISecretHasher _secrets;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<RegisterDeviceHandler> _logger;

    public RegisterDeviceHandler(
        IKioskDeviceRepository devices,
        IEnrollmentTokenRepository enrollmentTokens,
        IKioskRepository kiosks,
        ISecretHasher secrets,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<RegisterDeviceHandler> logger)
    {
        _devices = devices;
        _enrollmentTokens = enrollmentTokens;
        _kiosks = kiosks;
        _secrets = secrets;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RegisterDeviceResult> Handle(
        RegisterDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var fingerprint = Map(request.Fingerprint);

        var device = await _devices.FindByPublicKeyAsync(request.PublicKey, cancellationToken).ConfigureAwait(false);

        if (device is null)
        {
            var pending = await _devices.CountPendingAsync(cancellationToken).ConfigureAwait(false);
            if (pending >= MaxPendingDevices)
            {
                throw new DomainException("devices.tooManyPending");
            }

            // A new device starts Pending, which is the status it is born with;
            // there is no transition to record because nothing changed.
            device = new KioskDevice
            {
                PublicKey = request.PublicKey,
                CreatedAt = now,
            };
        }
        else if (device.Status == DeviceStatus.Revoked)
        {
            // A revoked device must not talk its way back in. Reinstalling
            // generates a new key, which registers as a new device.
            throw new DomainException("devices.revokedReinstall");
        }
        else if (device.Fingerprint.MachineId is not null && !device.Fingerprint.LooksLikeSameMachine(fingerprint))
        {
            // Same key, different machine: worth flagging, but not worth
            // refusing - the key is what proves identity.
            _logger.LogWarning(
                "Device {DeviceId} reported a substantially different fingerprint than before.", device.Id);
        }

        device.Fingerprint = fingerprint;
        device.Platform = request.Platform;
        device.AppVersion = request.AppVersion;
        device.AgentVersion = request.AgentVersion;
        device.ProvisioningVersion = request.ProvisioningVersion;
        device.LastSeenAt = now;
        device.LastIp = request.Ip;

        if (request.Printers is { Count: > 0 })
        {
            device.ReportedPrinters = [.. request.Printers.Select(Map)];
        }

        // Zero-touch: a valid enrollment token activates the device here and
        // now, so nobody has to read a code off the kiosk screen.
        if (device.Status != DeviceStatus.Active && !string.IsNullOrWhiteSpace(request.EnrollmentToken))
        {
            var activated = await TryActivateWithEnrollmentTokenAsync(device, request.EnrollmentToken, now, cancellationToken)
                .ConfigureAwait(false);

            if (activated is not null)
            {
                await SaveAsync(device, cancellationToken).ConfigureAwait(false);

                return new RegisterDeviceResult(
                    device.Id, Activated: true, PairingCode: null, PairingCodeExpiresAt: null,
                    activated.Id, activated.Code);
            }

            _logger.LogWarning(
                "An enrollment token was presented for device {DeviceId} but it was not usable; " +
                "falling back to the pairing code flow.", device.Id);
        }

        if (device.Status == DeviceStatus.Active && device.KioskId is not null)
        {
            var kiosk = await _kiosks.FindByIdAsync(device.KioskId.Value, cancellationToken).ConfigureAwait(false);

            await SaveAsync(device, cancellationToken).ConfigureAwait(false);

            return new RegisterDeviceResult(
                device.Id, Activated: true, null, null, kiosk?.Id, kiosk?.Code);
        }

        var pairingCode = KioskDevice.NewPairingCode();

        // The device itself is the actor: it registered and asked for a code.
        device.AwaitActivation(
            _secrets.HashSecret(KioskDevice.NormalizePairingCode(pairingCode)),
            now + KioskDevice.PairingCodeLifetime,
            ActorContext.Kiosk(device.Id, correlationId: _correlation.CorrelationId),
            now);

        await SaveAsync(device, cancellationToken).ConfigureAwait(false);

        return new RegisterDeviceResult(
            device.Id, Activated: false, pairingCode, device.PairingCodeExpiresAt, null, null);
    }

    private async Task<Kiosk?> TryActivateWithEnrollmentTokenAsync(
        KioskDevice device, string rawToken, DateTime now, CancellationToken cancellationToken)
    {
        var token = await _enrollmentTokens
            .FindByHashAsync(_secrets.HashToken(rawToken.Trim()), cancellationToken)
            .ConfigureAwait(false);

        if (token is null || !token.IsUsable(now))
        {
            return null;
        }

        Kiosk kiosk;

        if (token.KioskId is not null)
        {
            var existing = await _kiosks.FindByIdAsync(token.KioskId.Value, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return null;
            }

            kiosk = existing;

            // Replacing the hardware: the kiosk's previous device is revoked.
            if (kiosk.ActiveDeviceId is { } previousId && previousId != device.Id)
            {
                await RevokePreviousDeviceAsync(previousId, now, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (token.NewKioskTemplate is not null)
        {
            kiosk = await CreateKioskFromTemplateAsync(token.NewKioskTemplate, now, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        // The administrator who issued the token is the actor: they decided
        // this device belongs to this kiosk, even though they are not here now.
        var actor = token.CreatedBy is { } issuedBy
            ? ActorContext.Admin(issuedBy, "Enrollment token", _correlation.CorrelationId)
            : ActorContext.System("Enrollment token", _correlation.CorrelationId);

        device.Activate(kiosk.Id, actor, now);
        device.EnrollmentTokenId = token.Id;

        kiosk.BindDevice(device.Id, device.Platform, device.AppVersion, actor, now);
        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        token.UsedByDeviceId = device.Id;
        await _enrollmentTokens.UpdateAsync(token, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Device {DeviceId} activated against kiosk {KioskCode} with an enrollment token.", device.Id, kiosk.Code);

        return kiosk;
    }

    private async Task RevokePreviousDeviceAsync(Guid deviceId, DateTime now, CancellationToken cancellationToken)
    {
        var previous = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return;
        }

        previous.Revoke(
            ActorContext.System(
                "devices.reason.replaced", _correlation.CorrelationId),
            now);
        await _devices.UpdateAsync(previous, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Kiosk> CreateKioskFromTemplateAsync(
        NewKioskTemplate template, DateTime now, CancellationToken cancellationToken)
    {
        var code = template.Code;

        if (string.IsNullOrWhiteSpace(code))
        {
            var highest = await _kiosks.GetHighestCodeSequenceAsync(cancellationToken).ConfigureAwait(false);
            code = Kiosk.NextCode(highest + 1);
        }

        var kiosk = new Kiosk
        {
            Code = code,
            Name = template.Name,
            Location = template.Location,
            PricingProfileId = template.PricingProfileId,
            MaxFileSizeMb = template.MaxFileSizeMb,
            MaxPages = template.MaxPages,
            QueueDisplayLimit = template.QueueDisplayLimit,
            DefaultLanguage = template.DefaultLanguage,
            FiscalTerminalId = template.FiscalTerminalId,
            AdminPinHash = template.AdminPinHash,
            CreatedAt = now,
        };

        foreach (var (size, capacity) in template.PaperCapacity)
        {
            kiosk.PaperStock[size] = new PaperStock { Capacity = capacity, Current = 0 };
        }

        await _kiosks.AddAsync(kiosk, cancellationToken).ConfigureAwait(false);
        return kiosk;
    }

    private async Task SaveAsync(KioskDevice device, CancellationToken cancellationToken)
    {
        var existing = await _devices.FindByIdAsync(device.Id, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            await _devices.AddAsync(device, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static DeviceFingerprint Map(DeviceFingerprintDto dto) => new()
    {
        Hostname = dto.Hostname,
        Os = dto.Os,
        OsVersion = dto.OsVersion,
        MachineId = dto.MachineId,
        MacAddresses = [.. dto.MacAddresses ?? []],
        Cpu = dto.Cpu,
        RamGb = dto.RamGb,
        ScreenResolution = dto.ScreenResolution,
    };

    internal static DeviceFingerprintDto Map(DeviceFingerprint fingerprint) => new(
        fingerprint.Hostname,
        fingerprint.Os,
        fingerprint.OsVersion,
        fingerprint.MachineId,
        fingerprint.MacAddresses,
        fingerprint.Cpu,
        fingerprint.RamGb,
        fingerprint.ScreenResolution);

    internal static AvailablePrinter Map(ReportedPrinterDto dto) => new()
    {
        Name = dto.Name,
        DriverName = dto.DriverName,
        IsOnline = dto.IsOnline,
        DetectedCapabilities = new PrinterCapabilities { Color = dto.Color, Duplex = dto.Duplex, A3 = dto.A3 },
    };

    internal static ReportedPrinterDto Map(AvailablePrinter printer) => new(
        printer.Name,
        printer.DriverName,
        printer.IsOnline,
        printer.DetectedCapabilities.Color,
        printer.DetectedCapabilities.Duplex,
        printer.DetectedCapabilities.A3);
}

/// <summary>Issues a one-time challenge for a device to sign.</summary>
public sealed class CreateDeviceChallengeHandler
    : IRequestHandler<CreateDeviceChallengeCommand, DeviceChallengeResult>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IDeviceChallengeStore _challenges;
    private readonly IClock _clock;

    public CreateDeviceChallengeHandler(
        IKioskDeviceRepository devices, IDeviceChallengeStore challenges, IClock clock)
    {
        _devices = devices;
        _challenges = challenges;
        _clock = clock;
    }

    public async Task<DeviceChallengeResult> Handle(
        CreateDeviceChallengeCommand request, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("device");

        if (device.Status == DeviceStatus.Revoked)
        {
            throw new DomainException("devices.revoked");
        }

        var nonce = await _challenges.IssueAsync(device.Id, cancellationToken).ConfigureAwait(false);

        return new DeviceChallengeResult(nonce, _clock.UtcNow.AddMinutes(5));
    }
}

/// <summary>
/// Exchanges a signed challenge for a token (plan section 7.11).
/// </summary>
public sealed class CreateDeviceTokenHandler : IRequestHandler<CreateDeviceTokenCommand, DeviceTokenResult>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IDeviceChallengeStore _challenges;
    private readonly IDeviceSignatureVerifier _verifier;
    private readonly ITokenService _tokens;
    private readonly IClock _clock;

    public CreateDeviceTokenHandler(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IDeviceChallengeStore challenges,
        IDeviceSignatureVerifier verifier,
        ITokenService tokens,
        IClock clock)
    {
        _devices = devices;
        _kiosks = kiosks;
        _challenges = challenges;
        _verifier = verifier;
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<DeviceTokenResult> Handle(
        CreateDeviceTokenCommand request, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);

        // One message for every failure: a caller must not be able to tell an
        // unknown device from a bad signature.
        var refused = new DomainException("devices.refused");

        if (device is null || device.Status == DeviceStatus.Revoked || device.Status == DeviceStatus.Expired)
        {
            throw refused;
        }

        // The challenge is consumed whatever happens next, so a captured
        // signature cannot be replayed.
        if (!await _challenges.RedeemAsync(device.Id, request.Nonce, cancellationToken).ConfigureAwait(false))
        {
            throw refused;
        }

        if (!_verifier.Verify(device.PublicKey, request.Nonce, request.Signature))
        {
            throw refused;
        }

        device.LastSeenAt = _clock.UtcNow;
        device.LastIp = request.Ip;
        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        if (!device.CanIssueKioskToken)
        {
            // Registered but not activated: a limited token that can only renew
            // the pairing code and report printers.
            var provisioning = _tokens.CreateProvisioningToken(device.Id);
            return new DeviceTokenResult(
                provisioning.Token, provisioning.ExpiresAt, TokenTypes.Provisioning, null, null);
        }

        var kiosk = await _kiosks.FindByIdAsync(device.KioskId!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw refused;

        var token = _tokens.CreateKioskToken(device.Id, kiosk.Id);

        return new DeviceTokenResult(token.Token, token.ExpiresAt, TokenTypes.Kiosk, kiosk.Id, kiosk.Code);
    }
}

/// <summary>Renews the pairing code shown on the activation screen.</summary>
public sealed class RenewPairingCodeHandler : IRequestHandler<RenewPairingCodeCommand, PairingCodeResult>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly ISecretHasher _secrets;
    private readonly IClock _clock;

    public RenewPairingCodeHandler(IKioskDeviceRepository devices, ISecretHasher secrets, IClock clock)
    {
        _devices = devices;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<PairingCodeResult> Handle(RenewPairingCodeCommand request, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("device");

        if (device.Status != DeviceStatus.Pending)
        {
            throw new DomainException("devices.codeRefreshPendingOnly");
        }

        var code = KioskDevice.NewPairingCode();
        device.PairingCodeHash = _secrets.HashSecret(KioskDevice.NormalizePairingCode(code));
        device.PairingCodeExpiresAt = _clock.UtcNow + KioskDevice.PairingCodeLifetime;
        device.LastSeenAt = _clock.UtcNow;

        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        return new PairingCodeResult(code, device.PairingCodeExpiresAt.Value);
    }
}

/// <inheritdoc cref="GetDeviceStateQuery" />
/// <remarks>
/// A read, and deliberately a cheap one: it is what the activation screen polls
/// while it waits, so it must not cost a token exchange.
/// </remarks>
public sealed class GetDeviceStateHandler : IRequestHandler<GetDeviceStateQuery, DeviceStateResult?>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IClock _clock;

    public GetDeviceStateHandler(
        IKioskDeviceRepository devices, IKioskRepository kiosks, IClock clock)
    {
        _devices = devices;
        _kiosks = kiosks;
        _clock = clock;
    }

    public async Task<DeviceStateResult?> Handle(
        GetDeviceStateQuery request, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);

        if (device is null)
        {
            return null;
        }

        // The kiosk code is what the footer shows once the device is activated,
        // so it is resolved here rather than making the device ask again.
        var kiosk = device.KioskId is { } kioskId
            ? await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
            : null;

        return new DeviceStateResult(
            device.Id,
            device.Status,
            device.KioskId,
            kiosk?.Code,
            device.PairingCodeHash is not null
                && device.PairingCodeExpiresAt is { } expiresAt
                && expiresAt > _clock.UtcNow,
            device.PairingCodeExpiresAt);
    }
}

/// <summary>Records the printers a device reports before activation.</summary>
public sealed class ReportDevicePrintersHandler : IRequestHandler<ReportDevicePrintersCommand, Unit>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IClock _clock;

    public ReportDevicePrintersHandler(IKioskDeviceRepository devices, IClock clock)
    {
        _devices = devices;
        _clock = clock;
    }

    public async Task<Unit> Handle(ReportDevicePrintersCommand request, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("device");

        device.ReportedPrinters = [.. request.Printers.Select(RegisterDeviceHandler.Map)];
        device.LastSeenAt = _clock.UtcNow;

        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }
}
