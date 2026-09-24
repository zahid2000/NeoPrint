using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;

namespace NeoPrint.Application.Devices;

/// <inheritdoc cref="ListDevicesQuery" />
public sealed class ListDevicesHandler : IRequestHandler<ListDevicesQuery, IReadOnlyList<DeviceListItem>>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IClock _clock;

    public ListDevicesHandler(IKioskDeviceRepository devices, IKioskRepository kiosks, IClock clock)
    {
        _devices = devices;
        _kiosks = kiosks;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DeviceListItem>> Handle(
        ListDevicesQuery request, CancellationToken cancellationToken)
    {
        var devices = await _devices.ListAsync(request.Status, cancellationToken).ConfigureAwait(false);

        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(kiosk => kiosk.Id);

        var now = _clock.UtcNow;

        return devices.Select(device => new DeviceListItem(
            device.Id,
            device.Status,
            device.Platform,
            device.AppVersion,
            device.AgentVersion,
            device.Fingerprint.Hostname,
            device.Fingerprint.Os,
            device.LastIp,
            device.CreatedAt,
            device.LastSeenAt,
            device.KioskId,
            device.KioskId is not null && kiosks.TryGetValue(device.KioskId.Value, out var kiosk)
                ? kiosk.Code
                : null,
            // The code itself is only ever hashed; the panel shows whether one
            // is still valid, and the operator reads the code off the kiosk.
            device.IsPairingCodeValid(now),
            device.PairingCodeExpiresAt,
            [.. device.ReportedPrinters.Select(printer => printer.Name)])).ToList();
    }
}

/// <inheritdoc cref="ListEnrollmentTokensQuery" />
public sealed class ListEnrollmentTokensHandler
    : IRequestHandler<ListEnrollmentTokensQuery, IReadOnlyList<EnrollmentTokenListItem>>
{
    private readonly IEnrollmentTokenRepository _tokens;
    private readonly IKioskRepository _kiosks;
    private readonly IClock _clock;

    public ListEnrollmentTokensHandler(
        IEnrollmentTokenRepository tokens, IKioskRepository kiosks, IClock clock)
    {
        _tokens = tokens;
        _kiosks = kiosks;
        _clock = clock;
    }

    public async Task<IReadOnlyList<EnrollmentTokenListItem>> Handle(
        ListEnrollmentTokensQuery request, CancellationToken cancellationToken)
    {
        var all = await _tokens.ListAsync(cancellationToken).ConfigureAwait(false);

        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(kiosk => kiosk.Id);

        var now = _clock.UtcNow;

        return all.Select(token => new EnrollmentTokenListItem(
            token.Id,
            token.KioskId,
            token.KioskId is not null && kiosks.TryGetValue(token.KioskId.Value, out var kiosk)
                ? kiosk.Code
                : null,
            token.NewKioskTemplate?.Name,
            token.CreatedAt,
            token.ExpiresAt,
            token.UsedByDeviceId,
            token.RevokedAt,
            token.IsUsable(now))).ToList();
    }
}

/// <inheritdoc cref="ListKiosksQuery" />
public sealed class ListKiosksHandler : IRequestHandler<ListKiosksQuery, IReadOnlyList<KioskListItem>>
{
    private readonly IKioskRepository _kiosks;
    private readonly IPermissionResolver _permissions;

    public ListKiosksHandler(IKioskRepository kiosks, IPermissionResolver permissions)
    {
        _kiosks = kiosks;
        _permissions = permissions;
    }

    public async Task<IReadOnlyList<KioskListItem>> Handle(
        ListKiosksQuery request, CancellationToken cancellationToken)
    {
        var effective = await _permissions
            .ResolveAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);

        // A scoped operator sees only their own kiosks, so they cannot bind a
        // device to somebody else's kiosk by picking it out of a list.
        return kiosks
            .Where(kiosk => effective.CanSee(kiosk.Id))
            .OrderBy(kiosk => kiosk.Code, StringComparer.Ordinal)
            .Select(kiosk => new KioskListItem(
                kiosk.Id,
                kiosk.Code,
                kiosk.Name,
                kiosk.Status,
                kiosk.Platform,
                kiosk.Location.Address,
                kiosk.DefaultLanguage,
                kiosk.ActiveDeviceId,
                kiosk.AppVersion,
                kiosk.SelectedPrinter?.Name,
                kiosk.LastHeartbeatAt))
            .ToList();
    }
}

/// <inheritdoc cref="IsKioskInScopeQuery" />
public sealed class IsKioskInScopeHandler : IRequestHandler<IsKioskInScopeQuery, bool>
{
    private readonly IPermissionResolver _permissions;

    public IsKioskInScopeHandler(IPermissionResolver permissions) => _permissions = permissions;

    public async Task<bool> Handle(IsKioskInScopeQuery request, CancellationToken cancellationToken)
    {
        var effective = await _permissions
            .ResolveAsync(request.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        return effective.CanSee(request.KioskId);
    }
}

/// <inheritdoc cref="FindDeviceQuery" />
public sealed class FindDeviceHandler : IRequestHandler<FindDeviceQuery, DeviceSummary?>
{
    private readonly IKioskDeviceRepository _devices;

    public FindDeviceHandler(IKioskDeviceRepository devices) => _devices = devices;

    public async Task<DeviceSummary?> Handle(FindDeviceQuery request, CancellationToken cancellationToken)
    {
        var device = await _devices
            .FindByIdAsync(request.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        return device is null ? null : new DeviceSummary(device.Id, device.Status, device.KioskId);
    }
}
