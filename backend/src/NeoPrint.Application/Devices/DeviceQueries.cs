using MediatR;

using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Devices;

/// <summary>A device row, as the Devices page lists it (plan section 8A.3).</summary>
/// <param name="HasValidPairingCode">
/// Whether the code on the kiosk screen is still valid. The code itself is only
/// ever stored hashed, so the panel cannot show it - the operator reads it off
/// the kiosk (plan section 7.11).
/// </param>
public sealed record DeviceListItem(
    Guid Id,
    DeviceStatus Status,
    KioskPlatform Platform,
    string? AppVersion,
    string? AgentVersion,
    string? Hostname,
    string? Os,
    string? LastIp,
    DateTime RegisteredAt,
    DateTime LastSeenAt,
    Guid? KioskId,
    string? KioskCode,
    bool HasValidPairingCode,
    DateTime? PairingCodeExpiresAt,
    IReadOnlyCollection<string> ReportedPrinters);

/// <summary>Devices, optionally filtered by status.</summary>
/// <summary>
/// The devices the account may see (T-301): those waiting to be activated -
/// an operator binds them to a kiosk of their own - and those of the kiosks in
/// its scope. Another kiosk's machine, its name and its address, are not shown.
/// </summary>
public sealed record ListDevicesQuery(Guid AdminUserId, DeviceStatus? Status) : IRequest<IReadOnlyList<DeviceListItem>>;

/// <summary>An enrollment token, without the token itself.</summary>
public sealed record EnrollmentTokenListItem(
    Guid Id,
    Guid? KioskId,
    string? KioskCode,
    string? NewKioskName,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    Guid? UsedByDeviceId,
    DateTime? RevokedAt,
    bool IsUsable);

public sealed record ListEnrollmentTokensQuery : IRequest<IReadOnlyList<EnrollmentTokenListItem>>;

/// <summary>
/// A kiosk row: the kiosk list and its map, and the activation wizard's
/// choice of kiosks (plan section 8A.3).
/// </summary>
public sealed record KioskListItem(
    Guid Id,
    string Code,
    string Name,
    KioskStatus Status,
    KioskPlatform Platform,
    string Address,
    string DefaultLanguage,
    Guid? ActiveDeviceId,
    string? AppVersion,
    string? SelectedPrinter,
    DateTime? LastHeartbeatAt,
    double Lat,
    double Lng,
    Guid? PricingProfileId,
    DateTime? SafeModeSince = null,
    int? PaperA4Current = null,
    int? PaperA4Capacity = null,
    bool? PaperA4Counted = null);

/// <summary>
/// The kiosks an administrator's scope covers (plan section 8A.2).
/// </summary>
/// <remarks>
/// Scoped in the handler, not in the controller: a scoped operator must not be
/// able to bind a device to somebody else's kiosk by picking it out of a list,
/// and that is a rule about who may see what rather than about transport.
/// </remarks>
public sealed record ListKiosksQuery(Guid AdminUserId) : IRequest<IReadOnlyList<KioskListItem>>;

/// <summary>
/// Whether an administrator's kiosk scope covers a kiosk
/// (plan section 8A.2).
/// </summary>
/// <remarks>
/// A query rather than a check in the controller: what "in scope" means is a
/// rule about who may act on what, and it is resolved through the same cached
/// resolver that a role change invalidates. The caller turns the answer into a
/// status code, which is transport.
/// </remarks>
public sealed record IsKioskInScopeQuery(Guid AdminUserId, Guid KioskId) : IRequest<bool>;

/// <summary>
/// Whether an account may make a new kiosk while activating a device.
/// </summary>
/// <remarks>
/// <c>kiosks.activate</c> binds a device to a kiosk that exists; a kiosk that
/// does not exist yet takes <c>kiosks.create</c> as well (plan section 8B,
/// "Kiosk aktivləşdir": "(icazə varsa) Yeni kiosk"). Without this an operator,
/// who has the first and not the second, could create kiosks through the
/// activation endpoint (T-265).
/// </remarks>
public sealed record CanCreateKioskQuery(Guid AdminUserId) : IRequest<bool>;

/// <summary>
/// One device, or <c>null</c> when there is no such device.
/// </summary>
/// <remarks>
/// Only what a caller needs before acting on it - which kiosk it is bound to,
/// so its scope can be checked. Not the fingerprint or the printers, which the
/// activation wizard asks for separately.
/// </remarks>
public sealed record FindDeviceQuery(Guid DeviceId) : IRequest<DeviceSummary?>;

/// <param name="KioskId">The kiosk it is bound to, when it is bound to one.</param>
public sealed record DeviceSummary(Guid Id, DeviceStatus Status, Guid? KioskId);
