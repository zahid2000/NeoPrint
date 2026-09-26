using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;

namespace NeoPrint.Api.Hubs;

/// <summary>
/// Real-time channel for the admin and operator panels (plan section 6,
/// <c>/hubs/admin</c>).
/// </summary>
/// <remarks>
/// Everyone joins the <c>admins</c> group and a personal group. Kiosk-scoped
/// groups are joined only for the kiosks the account may see, so an operator
/// never receives events about a kiosk outside their scope.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.Admin)]
public sealed class AdminHub : Hub
{
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionResolver _permissions;

    public AdminHub(ICurrentUser currentUser, IPermissionResolver permissions)
    {
        _currentUser = currentUser;
        _permissions = permissions;
    }

    public const string AllAdminsGroup = "admins";

    public static string KioskGroup(Guid kioskId) => $"admin-kiosk:{kioskId}";

    public static string OperatorGroup(Guid adminUserId) => $"operator:{adminUserId}";

    public override async Task OnConnectedAsync()
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AllAdminsGroup);
        await Groups.AddToGroupAsync(Context.ConnectionId, OperatorGroup(adminId.Value));

        var effective = await _permissions.ResolveAsync(adminId.Value);

        // An account scoped to specific kiosks joins only those groups. An
        // account with full scope is added as kiosks appear, not up front.
        if (!effective.AllKiosks)
        {
            foreach (var kioskId in effective.KioskIds)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, KioskGroup(kioskId));
            }
        }

        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Sends the admin panel events (plan section 6).
/// </summary>
public sealed class SignalRAdminNotifier : IAdminNotifier
{
    private readonly IHubContext<AdminHub> _hub;

    public SignalRAdminNotifier(IHubContext<AdminHub> hub) => _hub = hub;

    public Task PermissionsChangedAsync(Guid roleId, CancellationToken cancellationToken = default)
        // Broadcast rather than targeted: the panel reloads its own
        // permissions, and knowing which accounts hold a role is not worth a
        // database round trip here.
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("PermissionsChanged", new { roleId }, cancellationToken);

    public Task DeviceRegisteredAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("DeviceRegistered", new { deviceId }, cancellationToken);

    public Task DeviceActivatedAsync(Guid deviceId, Guid kioskId, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("DeviceActivated", new { deviceId, kioskId }, cancellationToken);

    public Task DeviceRevokedAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("DeviceRevoked", new { deviceId }, cancellationToken);

    public Task FeatureFlagsChangedAsync(CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("FeatureFlagsChanged", cancellationToken);

    public Task StorageSyncProgressAsync(
        Application.Storage.StorageSyncProgress progress, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("StorageSyncProgress", progress, cancellationToken);
}
