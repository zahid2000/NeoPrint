using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Devices;

// ---------------------------------------------------------------------------
// Device side (plan section 7.11)
// ---------------------------------------------------------------------------

/// <summary>
/// Registers a kiosk device on first start.
/// </summary>
/// <remarks>
/// Anonymous, because the universal build carries no credentials at all. It is
/// rate limited per IP, and the number of pending devices is capped.
/// </remarks>
public sealed record RegisterDeviceCommand(
    string PublicKey,
    DeviceFingerprintDto Fingerprint,
    KioskPlatform Platform,
    string? AppVersion,
    string? AgentVersion,
    string? ProvisioningVersion,
    string? EnrollmentToken,
    IReadOnlyCollection<ReportedPrinterDto>? Printers,
    string? Ip) : IRequest<RegisterDeviceResult>, ITransactionalRequest;

/// <summary>
/// What the device gets back.
/// </summary>
/// <param name="Activated">
/// True when an enrollment token activated it immediately, in which case there
/// is no pairing code to show.
/// </param>
public sealed record RegisterDeviceResult(
    Guid DeviceId,
    bool Activated,
    string? PairingCode,
    DateTime? PairingCodeExpiresAt,
    Guid? KioskId,
    string? KioskCode);

public sealed record DeviceFingerprintDto(
    string? Hostname,
    string? Os,
    string? OsVersion,
    string? MachineId,
    IReadOnlyCollection<string>? MacAddresses,
    string? Cpu,
    int RamGb,
    string? ScreenResolution);

public sealed record ReportedPrinterDto(
    string Name, string? DriverName, bool IsOnline, bool Color, bool Duplex, bool A3);

/// <summary>Issues a challenge for a device to sign.</summary>
public sealed record CreateDeviceChallengeCommand(Guid DeviceId) : IRequest<DeviceChallengeResult>;

public sealed record DeviceChallengeResult(string Nonce, DateTime ExpiresAt);

/// <summary>
/// Exchanges a signed challenge for a token.
/// </summary>
/// <remarks>
/// An activated device gets a kiosk token; one still waiting gets a limited
/// provisioning token that can only renew its pairing code and report printers.
/// </remarks>
public sealed record CreateDeviceTokenCommand(Guid DeviceId, string Nonce, string Signature, string? Ip)
    : IRequest<DeviceTokenResult>;

public sealed record DeviceTokenResult(
    string AccessToken,
    DateTime ExpiresAt,
    string TokenType,
    Guid? KioskId,
    string? KioskCode);

/// <summary>Renews the pairing code of a device that is still waiting.</summary>
public sealed record RenewPairingCodeCommand(Guid DeviceId) : IRequest<PairingCodeResult>;

public sealed record PairingCodeResult(string PairingCode, DateTime ExpiresAt);

/// <summary>
/// What a device asks about itself while it waits to be activated
/// (plan sections 7.11, 9.0).
/// </summary>
/// <remarks>
/// The activation screen has to notice that an administrator activated the
/// kiosk. Asking by taking a fresh token means a challenge and a token request
/// every few seconds, which runs into the ten-per-minute limit those two share
/// with sign-in - so the device asks this instead, which is a plain read.
/// </remarks>
public sealed record GetDeviceStateQuery(Guid DeviceId) : IRequest<DeviceStateResult?>;

/// <param name="Status">Where the device stands (<c>Pending</c>, <c>Active</c>, <c>Revoked</c>).</param>
/// <param name="HasValidPairingCode">
/// Whether the code the screen is showing is still the one the backend accepts.
/// The code itself is not returned: only its hash is stored, and the device
/// already has the one it was given.
/// </param>
public sealed record DeviceStateResult(
    Guid DeviceId,
    DeviceStatus Status,
    Guid? KioskId,
    string? KioskCode,
    bool HasValidPairingCode,
    DateTime? PairingCodeExpiresAt);

/// <summary>Reports the printers a device can see, before it is activated.</summary>
public sealed record ReportDevicePrintersCommand(Guid DeviceId, IReadOnlyCollection<ReportedPrinterDto> Printers)
    : IRequest<Unit>;

// ---------------------------------------------------------------------------
// Admin side
// ---------------------------------------------------------------------------

/// <summary>Looks a device up by its pairing code, or by the content of its activation QR.</summary>
public sealed record LookupDeviceCommand(string CodeOrQr) : IRequest<DeviceDetails>;

/// <summary>What the admin panel shows before activating a device.</summary>
public sealed record DeviceDetails(
    Guid DeviceId,
    DeviceStatus Status,
    KioskPlatform Platform,
    string? AppVersion,
    DeviceFingerprintDto Fingerprint,
    IReadOnlyCollection<ReportedPrinterDto> Printers,
    string? LastIp,
    DateTime RegisteredAt,
    DateTime LastSeenAt,
    Guid? KioskId,
    string? KioskCode);

/// <summary>
/// Activates a device, either onto an existing kiosk or as a new one.
/// </summary>
/// <remarks>
/// Binding to an existing kiosk is how broken hardware is replaced: the code,
/// the history and the counters stay, and the kiosk's previous device is
/// revoked automatically (plan section 7.11).
/// </remarks>
public sealed record ActivateDeviceCommand(
    Guid DeviceId,
    Guid? KioskId,
    NewKioskParameters? NewKiosk,
    string? SelectedPrinterName,
    Guid ActivatedBy,
    string? Ip) : IRequest<ActivateDeviceResult>, ITransactionalRequest;

public sealed record NewKioskParameters(
    string Name,
    string? Code,
    double Lat,
    double Lng,
    string Address,
    Guid? PricingProfileId,
    int? MaxFileSizeMb,
    int? MaxPages,
    int? QueueDisplayLimit,
    string DefaultLanguage,
    string? FiscalTerminalId,
    int PaperCapacityA4,
    int PaperCapacityA3,
    string AdminPin);

public sealed record ActivateDeviceResult(Guid DeviceId, Guid KioskId, string KioskCode, KioskStatus KioskStatus);

/// <summary>Detaches a device. Its next request is refused.</summary>
public sealed record RevokeDeviceCommand(Guid DeviceId, string? Reason, Guid RevokedBy, string? Ip)
    : IRequest<Unit>, ITransactionalRequest;

/// <summary>Creates a single-use enrollment token for zero-touch activation.</summary>
public sealed record CreateEnrollmentTokenCommand(
    Guid? KioskId,
    NewKioskParameters? NewKiosk,
    int? TtlHours,
    Guid CreatedBy) : IRequest<EnrollmentTokenResult>;

public sealed record EnrollmentTokenResult(
    Guid Id,
    string Token,
    DateTime ExpiresAt,
    Guid? KioskId,
    string? KioskCode,
    string WindowsCommand,
    string LinuxCommand);

/// <summary>Revokes an enrollment token that has not been used.</summary>
public sealed record RevokeEnrollmentTokenCommand(Guid Id, Guid RevokedBy) : IRequest<Unit>;

// ---------------------------------------------------------------------------
// Validators
// ---------------------------------------------------------------------------

public sealed class RegisterDeviceCommandValidator : IValidator<RegisterDeviceCommand>
{
    private readonly IDeviceSignatureVerifier _verifier;

    public RegisterDeviceCommandValidator(IDeviceSignatureVerifier verifier) => _verifier = verifier;

    public IEnumerable<ValidationError> Validate(RegisterDeviceCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.PublicKey))
        {
            yield return new ValidationError("publicKey", "devices.keyRequired");
        }
        else if (!_verifier.IsValidPublicKey(request.PublicKey))
        {
            yield return new ValidationError("publicKey", "devices.keyFormat");
        }

        if (request.Platform == KioskPlatform.Unknown)
        {
            yield return new ValidationError("platform", "devices.platformInvalid");
        }
    }
}

public sealed class ActivateDeviceCommandValidator : IValidator<ActivateDeviceCommand>
{
    /// <summary>The kiosk admin PIN unlocks the hidden exit, so it is not a two-digit affair.</summary>
    public const int MinimumPinLength = 4;

    public IEnumerable<ValidationError> Validate(ActivateDeviceCommand request)
    {
        if (request.KioskId is null && request.NewKiosk is null)
        {
            yield return new ValidationError(
                "", "devices.activationTargetRequired");
            yield break;
        }

        if (request.KioskId is not null && request.NewKiosk is not null)
        {
            yield return new ValidationError(
                "", "devices.activationTargetBoth");
            yield break;
        }

        if (request.NewKiosk is null)
        {
            yield break;
        }

        var kiosk = request.NewKiosk;

        if (string.IsNullOrWhiteSpace(kiosk.Name))
        {
            yield return new ValidationError("newKiosk.name", "kiosks.nameRequired");
        }

        if (string.IsNullOrWhiteSpace(kiosk.Address))
        {
            yield return new ValidationError("newKiosk.address", "kiosks.addressRequired");
        }

        if (kiosk.Lat is < -90 or > 90)
        {
            yield return new ValidationError("newKiosk.lat", "kiosks.latitudeRange");
        }

        if (kiosk.Lng is < -180 or > 180)
        {
            yield return new ValidationError("newKiosk.lng", "kiosks.longitudeRange");
        }

        if (string.IsNullOrWhiteSpace(kiosk.AdminPin) || kiosk.AdminPin.Trim().Length < MinimumPinLength)
        {
            yield return new ValidationError(
                "newKiosk.adminPin", "kiosks.pinTooShort", ("min", MinimumPinLength));
        }
        else if (!kiosk.AdminPin.Trim().All(char.IsAsciiDigit))
        {
            // The kiosk on-screen keypad is numeric.
            yield return new ValidationError("newKiosk.adminPin", "kiosks.pinDigits");
        }

        if (kiosk.Code is not null && !System.Text.RegularExpressions.Regex.IsMatch(kiosk.Code, "^KSK-[0-9]{4}$"))
        {
            yield return new ValidationError("newKiosk.code", "kiosks.codeFormat");
        }

        foreach (var error in ValidateCapacity(kiosk.PaperCapacityA4, "newKiosk.paperCapacityA4"))
        {
            yield return error;
        }

        foreach (var error in ValidateCapacity(kiosk.PaperCapacityA3, "newKiosk.paperCapacityA3"))
        {
            yield return error;
        }
    }

    private static IEnumerable<ValidationError> ValidateCapacity(int capacity, string field)
    {
        if (capacity is < 0 or > 100_000)
        {
            yield return new ValidationError(field, "kiosks.paperCapacityRange", ("max", 100000));
        }
    }
}

public sealed class LookupDeviceCommandValidator : IValidator<LookupDeviceCommand>
{
    public IEnumerable<ValidationError> Validate(LookupDeviceCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.CodeOrQr))
        {
            yield return new ValidationError("codeOrQr", "devices.codeRequired");
        }
    }
}
