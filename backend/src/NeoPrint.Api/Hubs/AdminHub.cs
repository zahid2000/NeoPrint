using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Api.Infrastructure;
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
    private readonly IPermissionResolver _permissions;

    public AdminHub(IPermissionResolver permissions) => _permissions = permissions;

    public const string AllAdminsGroup = "admins";

    /// <summary>
    /// Accounts that see every kiosk. Kiosk events go here and to the kiosk's
    /// own group, so a scoped operator hears only about their kiosks.
    /// </summary>
    public const string AllKiosksGroup = "admins:all-kiosks";

    public static string KioskGroup(Guid kioskId) => $"admin-kiosk:{kioskId}";

    public static string OperatorGroup(Guid adminUserId) => $"operator:{adminUserId}";

    public override async Task OnConnectedAsync()
    {
        var adminId = CallerClaims.AdminUserId(Context.User);
        if (adminId is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AllAdminsGroup);
        await Groups.AddToGroupAsync(Context.ConnectionId, OperatorGroup(adminId.Value));

        var effective = await _permissions.ResolveAsync(adminId.Value);

        // An account scoped to specific kiosks joins only those groups; one
        // with full scope joins the group every kiosk's events also go to.
        if (effective.AllKiosks)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AllKiosksGroup);
        }
        else
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

    public Task KioskStatusChangedAsync(Guid kioskId, string status, CancellationToken cancellationToken = default)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(kioskId)])
            .SendAsync("KioskStatusChanged", new { kioskId, status }, cancellationToken);

    public Task KioskPrintersUpdatedAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(kioskId)])
            .SendAsync("KioskPrintersUpdated", new { kioskId }, cancellationToken);

    public Task KioskTelemetryUpdatedAsync(
        Application.Kiosks.KioskTelemetryView telemetry, CancellationToken cancellationToken = default)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(telemetry.KioskId)])
            .SendAsync("KioskTelemetryUpdated", telemetry, cancellationToken);

    public Task PrinterTelemetryUpdatedAsync(
        Guid kioskId, IReadOnlyList<Application.Kiosks.PrinterTelemetryView> printers, CancellationToken cancellationToken = default)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(kioskId)])
            .SendAsync("PrinterTelemetryUpdated", new { kioskId, printers }, cancellationToken);

    public Task CommandResultAsync(
        Guid kioskId, Application.Kiosks.KioskCommandView command, CancellationToken cancellationToken = default)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(kioskId)])
            .SendAsync("CommandResult", command, cancellationToken);

    public Task QueueUpdatedAsync(
        Guid kioskId, IReadOnlyList<Application.PrintJobs.QueueEntry> queue, CancellationToken cancellationToken = default)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(kioskId)])
            .SendAsync("QueueUpdated", new { kioskId, queue }, cancellationToken);

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

    public Task StorageSyncCompletedAsync(
        Application.Storage.StorageSyncProgress progress, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("StorageSyncCompleted", progress, cancellationToken);

    public Task StorageHealthChangedAsync(
        Application.Storage.StorageHealthChanged change, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("StorageHealthChanged", change, cancellationToken);
}

/// <summary>Payments, refunds, fiscal receipts and incidents to the panels (plan section 6, <c>/hubs/admin</c>).</summary>
public sealed class SignalRPaymentsAdminNotifier : NeoPrint.Application.Refunds.IPaymentsAdminNotifier
{
    private readonly IHubContext<AdminHub> _hub;

    public SignalRPaymentsAdminNotifier(IHubContext<AdminHub> hub) => _hub = hub;

    public Task IncidentRaisedAsync(
        Guid incidentId, string type, string severity, CancellationToken cancellationToken = default, Guid? kioskId = null)
        => About(kioskId).SendAsync("IncidentRaised", new { incidentId, type, severity, kioskId }, cancellationToken);

    public Task IncidentResolvedAsync(
        Guid incidentId, string type, string status, CancellationToken cancellationToken = default, Guid? kioskId = null)
        => About(kioskId).SendAsync("IncidentResolved", new { incidentId, type, status, kioskId }, cancellationToken);

    /// <summary>
    /// A kiosk's incident goes to the accounts that may see that kiosk; one
    /// that is about no kiosk (a refund, a fiscal receipt) to every panel.
    /// </summary>
    private IClientProxy About(Guid? kioskId)
        => kioskId is { } kiosk
            ? _hub.Clients.Groups([AdminHub.AllKiosksGroup, AdminHub.KioskGroup(kiosk)])
            : _hub.Clients.Group(AdminHub.AllAdminsGroup);

    public Task RefundStatusChangedAsync(Guid refundId, Guid paymentId, string status, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup).SendAsync("RefundStatusChanged", new { refundId, paymentId, status }, cancellationToken);

    public Task PaymentSucceededAsync(Guid paymentId, Guid? kioskId, decimal amount, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup).SendAsync("PaymentSucceeded", new { paymentId, kioskId, amount }, cancellationToken);

    public Task FiscalStatusChangedAsync(Guid receiptId, string status, CancellationToken cancellationToken = default)
        => _hub.Clients.Group(AdminHub.AllAdminsGroup).SendAsync("FiscalStatusChanged", new { receiptId, status }, cancellationToken);
}
