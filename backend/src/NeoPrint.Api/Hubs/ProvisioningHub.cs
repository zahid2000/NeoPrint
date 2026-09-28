using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;

namespace NeoPrint.Api.Hubs;

/// <summary>
/// Real-time channel for a device that is registered but not yet activated
/// (plan section 6, <c>/hubs/provisioning</c>).
/// </summary>
/// <remarks>
/// Lets the kiosk react the moment an admin activates or detaches it, instead
/// of polling from the activation screen. Each device joins only its own group,
/// so nothing about one kiosk reaches another.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.Device)]
public sealed class ProvisioningHub : Hub
{
    /// <summary>The group a device listens on.</summary>
    public static string GroupFor(Guid deviceId) => $"device:{deviceId}";

    public override async Task OnConnectedAsync()
    {
        var deviceId = CallerClaims.DeviceId(Context.User);

        if (deviceId is null)
        {
            // Should be impossible behind the policy, but a connection without
            // a device claim has nothing to listen to.
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(deviceId.Value));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (CallerClaims.DeviceId(Context.User) is { } deviceId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(deviceId));
        }

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Sends the provisioning events to a device (plan section 6).
/// </summary>
public sealed class SignalRDeviceNotifier : IDeviceNotifier
{
    private readonly IHubContext<ProvisioningHub> _hub;

    public SignalRDeviceNotifier(IHubContext<ProvisioningHub> hub) => _hub = hub;

    public Task DeviceActivatedAsync(Guid deviceId, Guid kioskId, CancellationToken cancellationToken = default)
        => _hub.Clients
            .Group(ProvisioningHub.GroupFor(deviceId))
            .SendAsync("DeviceActivated", new { deviceId, kioskId }, cancellationToken);

    public Task DeviceRevokedAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => _hub.Clients
            .Group(ProvisioningHub.GroupFor(deviceId))
            .SendAsync("DeviceRevoked", new { deviceId }, cancellationToken);

    public Task PairingCodeRenewedAsync(
        Guid deviceId, string pairingCode, DateTime expiresAt, CancellationToken cancellationToken = default)
        => _hub.Clients
            .Group(ProvisioningHub.GroupFor(deviceId))
            .SendAsync("PairingCodeRenewed", new { deviceId, pairingCode, expiresAt }, cancellationToken);
}
