using MediatR;

using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Devices;

/// <summary>
/// Everything a kiosk needs to run, fetched after activation
/// (plan sections 2.2, 7.11, 6).
/// </summary>
/// <remarks>
/// The kiosk holds no configuration of its own: it is a universal build, so
/// everything that makes it this kiosk comes from here (plan section 7.11).
/// No backend secret is included - the admin PIN travels as a PBKDF2 hash the
/// kiosk compares against, and nothing else here is sensitive.
/// </remarks>
/// <param name="CacheTtlHours">
/// How long this configuration may be acted on if the backend becomes
/// unreachable. Past it the kiosk stops taking orders
/// (<c>Kiosk.ConfigCacheTtlHours</c>, plan section 7.3).
/// </param>
public sealed record KioskConfigResponse(
    Guid KioskId,
    string Code,
    string Name,
    KioskStatus Status,
    string DefaultLanguage,
    string? SelectedPrinter,
    PrinterCapabilitiesDto EffectiveCapabilities,
    IReadOnlyCollection<AvailablePrinterDto> AvailablePrinters,
    string? AdminPinHash,
    int QueueDisplayLimit,
    int MaxFileSizeMb,
    int MaxPages,
    Guid? PricingProfileId,
    IReadOnlyDictionary<string, PaperStockDto> PaperStock,
    IReadOnlyDictionary<string, bool> Features,
    int HeartbeatSeconds,
    int CacheTtlHours,
    DateTime ServerTime);

public sealed record PrinterCapabilitiesDto(bool Color, bool Duplex, bool A3);

public sealed record AvailablePrinterDto(
    string Name, string? DriverName, bool IsOnline, PrinterCapabilitiesDto DetectedCapabilities);

public sealed record PaperStockDto(int Capacity, int Current);

/// <summary>The configuration of the kiosk the calling device is bound to.</summary>
public sealed record GetKioskConfigQuery(Guid DeviceId) : IRequest<KioskConfigResponse?>;

/// <summary>
/// A kiosk reporting that it is alive (plan sections 7.9, 6).
/// </summary>
/// <remarks>
/// Also the moment a kiosk comes back: a kiosk marked Offline because its
/// heartbeat stopped returns to service on the next one, without an operator
/// having to do anything.
/// </remarks>
public sealed record KioskHeartbeatCommand(
    Guid DeviceId,
    string? AppVersion,
    string? AgentVersion,
    bool PrinterOnline,
    string? Ip) : IRequest<KioskHeartbeatResult>;

/// <param name="NextInSeconds">
/// When to send the next one, so the interval can be changed from the panel
/// without redeploying the kiosk.
/// </param>
/// <param name="ConfigurationChangedAt">
/// When the kiosk's configuration last changed, so a kiosk can tell it should
/// fetch the configuration again without polling the whole thing.
/// </param>
public sealed record KioskHeartbeatResult(
    KioskStatus Status,
    int NextInSeconds,
    DateTime ConfigurationChangedAt,
    DateTime ServerTime);
