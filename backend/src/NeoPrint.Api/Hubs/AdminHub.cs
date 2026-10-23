using System.Collections.Concurrent;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Hubs;

/// <summary>
/// Real-time channel for the admin and operator panels (plan section 6,
/// <c>/hubs/admin</c>).
/// </summary>
/// <remarks>
/// Everyone joins the <c>admins</c> group and a personal group; nothing that
/// carries data is sent to the first. Every other event has an
/// <see cref="AdminAudience">audience</see> - the accounts whose pages may show
/// what it carries: they hold the permission the page's own request needs, and
/// the kiosk is in their scope. A connection joins the groups of the audiences
/// it belongs to, for the kiosks it may see, so an operator of one kiosk who
/// may not look at payments hears neither another kiosk's status nor anybody's
/// payments (T-245, T-261).
/// <para>
/// The groups are worked out when the connection opens, so a connection has to
/// end when what it was worked out from changes: the account's scope or roles,
/// or what one of its roles permits (<see cref="AdminConnections"/> closes it
/// at once) and the token it was
/// opened with (the endpoint closes it when the token runs out). The panel
/// connects again by itself, with a token that is good now (T-249).
/// </para>
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.Admin)]
public sealed class AdminHub : Hub
{
    private readonly IPermissionResolver _permissions;
    private readonly AdminConnections _connections;

    public AdminHub(IPermissionResolver permissions, AdminConnections connections)
    {
        _permissions = permissions;
        _connections = connections;
    }

    /// <summary>Every signed-in panel. Only for what says nothing by itself: "your permissions may have changed".</summary>
    public const string AllAdminsGroup = "admins";

    public static string OperatorGroup(Guid adminUserId) => $"operator:{adminUserId}";

    /// <summary>The members of an audience, whatever kiosks they see: for what is about no kiosk at all.</summary>
    public static string HoldersGroup(AdminAudience audience) => $"admins:{audience}";

    /// <summary>
    /// The members of an audience that see every kiosk. An event about a kiosk
    /// goes here and to <see cref="KioskGroup"/>; a connection is in one or the
    /// other, never both, so nobody is told twice.
    /// </summary>
    public static string AllKiosksGroup(AdminAudience audience) => $"admins:{audience}:all-kiosks";

    /// <summary>The members of an audience whose scope names this kiosk.</summary>
    public static string KioskGroup(AdminAudience audience, Guid kioskId) => $"admins:{audience}:kiosk:{kioskId:N}";

    /// <summary>
    /// An account that is given tasks and may not read the task list
    /// (<c>service.tasks.complete</c> without <c>service.tasks.view</c>): it
    /// is told of its own tasks here. One that may read the list hears through
    /// the audience instead and is not in this group, so nobody is told twice.
    /// </summary>
    public static string AssigneeGroup(Guid adminUserId) => $"operator:{adminUserId}:tasks";

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

        // For each audience the account belongs to: its group, and then either
        // the group of those who see every kiosk or one group per kiosk in scope.
        var audiences = AdminAudiences.Of(effective).ToList();

        if (!audiences.Contains(AdminAudience.ServiceTasks))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AssigneeGroup(adminId.Value));
        }

        foreach (var audience in audiences)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HoldersGroup(audience));

            if (effective.AllKiosks)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, AllKiosksGroup(audience));
            }
            else
            {
                foreach (var kioskId in effective.KioskIds)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, KioskGroup(audience, kioskId));
                }
            }
        }

        _connections.Opened(adminId.Value, Context);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Closed(Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Who a panel event is for: the accounts whose pages may show what it carries.
/// </summary>
public enum AdminAudience
{
    /// <summary>Kiosk statuses, telemetry, command results.</summary>
    Kiosks,

    /// <summary>A kiosk's printers and what they report.</summary>
    Printers,

    /// <summary>Queues and orders.</summary>
    Jobs,

    Devices,

    Supplies,

    ServiceTasks,

    Incidents,

    Payments,

    /// <summary>Refunds: the refund list, and the payment a refund belongs to.</summary>
    Refunds,

    /// <summary>Fiscal receipts: their own list, the receipts list, the payment they belong to.</summary>
    Fiscal,

    /// <summary>Storage providers and syncs: the storage page, and the dashboard's storage tiles.</summary>
    Storage,

    Features,
}

/// <summary>
/// Which permissions put an account in an audience (T-261). The rule for each
/// is the one the REST endpoint behind the page applies: an event must not say
/// what the same account would be refused over HTTP.
/// </summary>
public static class AdminAudiences
{
    private static readonly (AdminAudience Audience, string[] AnyOf)[] Rules =
    [
        (AdminAudience.Kiosks, [Permissions.KiosksView]),
        (AdminAudience.Printers, [Permissions.PrintersView]),
        (AdminAudience.Jobs, [Permissions.JobsView]),
        (AdminAudience.Devices, [Permissions.DevicesView]),
        (AdminAudience.Supplies, [Permissions.SuppliesView]),
        (AdminAudience.ServiceTasks, [Permissions.ServiceTasksView]),
        (AdminAudience.Incidents, [Permissions.IncidentsView]),
        (AdminAudience.Payments, [Permissions.PaymentsView]),
        (AdminAudience.Refunds, [Permissions.RefundsView, Permissions.PaymentsView]),
        (AdminAudience.Fiscal, [Permissions.FiscalView, Permissions.ReceiptsView, Permissions.PaymentsView]),
        (AdminAudience.Storage, [Permissions.StorageManage, Permissions.DashboardView]),
        (AdminAudience.Features, [Permissions.FeaturesManage]),
    ];

    /// <summary>The audiences an account belongs to.</summary>
    public static IEnumerable<AdminAudience> Of(EffectivePermissions effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        return Rules.Where(rule => rule.AnyOf.Any(effective.Has)).Select(rule => rule.Audience);
    }
}

/// <summary>
/// The panel connections this instance holds, by account, so that an account
/// whose scope changed can be cut off without waiting for its token to run out.
/// </summary>
/// <remarks>
/// Of this instance only. With several instances a panel connected to another
/// one hears <c>AccountChanged</c> and reconnects by itself; one that chose
/// not to would keep its groups until its token runs out, which is when the
/// endpoint closes the connection - fifteen minutes at most
/// (<c>docs/SECURITY_THREAT_MODEL.md</c>).
/// </remarks>
public sealed class AdminConnections
{
    private readonly ConcurrentDictionary<string, (Guid AdminId, HubCallerContext Context)> _open = new();

    public void Opened(Guid adminId, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _open[context.ConnectionId] = (adminId, context);
    }

    public void Closed(string connectionId) => _open.TryRemove(connectionId, out _);

    /// <summary>Whether the account has a connection here that has joined all its groups.</summary>
    public bool IsOpen(Guid adminId) => _open.Values.Any(open => open.AdminId == adminId);

    /// <summary>Ends every connection of the account; how many there were.</summary>
    public int Close(Guid adminId)
    {
        var closed = 0;

        foreach (var (connectionId, open) in _open)
        {
            if (open.AdminId == adminId && _open.TryRemove(connectionId, out _))
            {
                open.Context.Abort();
                closed++;
            }
        }

        return closed;
    }
}

/// <summary>
/// Sends the admin panel events (plan section 6).
/// </summary>
public sealed class SignalRAdminNotifier : IAdminNotifier
{
    private readonly IHubContext<AdminHub> _hub;
    private readonly AdminConnections _connections;

    public SignalRAdminNotifier(IHubContext<AdminHub> hub, AdminConnections connections)
    {
        _hub = hub;
        _connections = connections;
    }

    public async Task AccountChangedAsync(Guid adminUserId, CancellationToken cancellationToken = default)
    {
        // Said first, for a panel that another instance holds; then cut here.
        await _hub.Clients.Group(AdminHub.OperatorGroup(adminUserId))
            .SendAsync("AccountChanged", cancellationToken)
            .ConfigureAwait(false);

        _connections.Close(adminUserId);
    }

    /// <summary>The members of an audience that may see the kiosk (<c>AdminHub</c>, T-261).</summary>
    private IClientProxy About(AdminAudience audience, Guid kioskId)
        => _hub.Clients.Groups([AdminHub.AllKiosksGroup(audience), AdminHub.KioskGroup(audience, kioskId)]);

    /// <summary>The members of an audience, for what is about no kiosk.</summary>
    private IClientProxy Holders(AdminAudience audience) => _hub.Clients.Group(AdminHub.HoldersGroup(audience));

    public Task SupplyAlertRaisedAsync(Application.Supplies.SupplyAlertView alert, CancellationToken cancellationToken = default)
        => About(AdminAudience.Supplies, alert.KioskId).SendAsync("SupplyAlertRaised", alert, cancellationToken);

    public Task SupplyAlertClearedAsync(Application.Supplies.SupplyAlertView alert, CancellationToken cancellationToken = default)
        => About(AdminAudience.Supplies, alert.KioskId).SendAsync("SupplyAlertCleared", alert, cancellationToken);

    public Task ServiceTaskAssignedAsync(Application.Supplies.ServiceTaskView task, CancellationToken cancellationToken = default)
    {
        var groups = new List<string>
        {
            AdminHub.AllKiosksGroup(AdminAudience.ServiceTasks),
            AdminHub.KioskGroup(AdminAudience.ServiceTasks, task.KioskId),
        };

        // The task is theirs to do, so they are told even if they may not read the list.
        if (task.AssignedTo is { } operatorId)
        {
            groups.Add(AdminHub.AssigneeGroup(operatorId));
        }

        return _hub.Clients.Groups(groups).SendAsync("ServiceTaskAssigned", task, cancellationToken);
    }

    public Task ServiceTaskUpdatedAsync(Application.Supplies.ServiceTaskView task, CancellationToken cancellationToken = default)
        => About(AdminAudience.ServiceTasks, task.KioskId).SendAsync("ServiceTaskUpdated", task, cancellationToken);

    public Task KioskStatusChangedAsync(Guid kioskId, string status, CancellationToken cancellationToken = default)
        => About(AdminAudience.Kiosks, kioskId).SendAsync("KioskStatusChanged", new { kioskId, status }, cancellationToken);

    public Task KioskPrintersUpdatedAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => About(AdminAudience.Printers, kioskId).SendAsync("KioskPrintersUpdated", new { kioskId }, cancellationToken);

    public Task KioskTelemetryUpdatedAsync(
        Application.Kiosks.KioskTelemetryView telemetry, CancellationToken cancellationToken = default)
        => About(AdminAudience.Kiosks, telemetry.KioskId).SendAsync("KioskTelemetryUpdated", telemetry, cancellationToken);

    public Task PrinterTelemetryUpdatedAsync(
        Guid kioskId, IReadOnlyList<Application.Kiosks.PrinterTelemetryView> printers, CancellationToken cancellationToken = default)
        => About(AdminAudience.Printers, kioskId).SendAsync("PrinterTelemetryUpdated", new { kioskId, printers }, cancellationToken);

    // The commands of a kiosk are read with kiosks.view (GET admin/kiosks/{id}/commands).
    public Task CommandResultAsync(
        Guid kioskId, Application.Kiosks.KioskCommandView command, CancellationToken cancellationToken = default)
        => About(AdminAudience.Kiosks, kioskId).SendAsync("CommandResult", command, cancellationToken);

    public Task QueueUpdatedAsync(
        Guid kioskId, IReadOnlyList<Application.PrintJobs.QueueEntry> queue, CancellationToken cancellationToken = default)
        => About(AdminAudience.Jobs, kioskId).SendAsync("QueueUpdated", new { kioskId, queue }, cancellationToken);

    public Task PermissionsChangedAsync(Guid roleId, CancellationToken cancellationToken = default)
        // Broadcast rather than targeted: the panel reloads its own
        // permissions, and knowing which accounts hold a role is not worth a
        // database round trip here.
        => _hub.Clients.Group(AdminHub.AllAdminsGroup)
            .SendAsync("PermissionsChanged", new { roleId }, cancellationToken);

    // A device waiting to be activated belongs to no kiosk yet, and the list of
    // devices is one list for whoever may read it (GET admin/devices).
    public Task DeviceRegisteredAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Devices).SendAsync("DeviceRegistered", new { deviceId }, cancellationToken);

    public Task DeviceActivatedAsync(Guid deviceId, Guid kioskId, CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Devices).SendAsync("DeviceActivated", new { deviceId, kioskId }, cancellationToken);

    public Task DeviceRevokedAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Devices).SendAsync("DeviceRevoked", new { deviceId }, cancellationToken);

    public Task FeatureFlagsChangedAsync(CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Features).SendAsync("FeatureFlagsChanged", cancellationToken);

    public Task StorageSyncProgressAsync(
        Application.Storage.StorageSyncProgress progress, CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Storage).SendAsync("StorageSyncProgress", progress, cancellationToken);

    public Task StorageSyncCompletedAsync(
        Application.Storage.StorageSyncProgress progress, CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Storage).SendAsync("StorageSyncCompleted", progress, cancellationToken);

    public Task StorageHealthChangedAsync(
        Application.Storage.StorageHealthChanged change, CancellationToken cancellationToken = default)
        => Holders(AdminAudience.Storage).SendAsync("StorageHealthChanged", change, cancellationToken);
}

/// <summary>Payments, refunds, fiscal receipts and incidents to the panels (plan section 6, <c>/hubs/admin</c>).</summary>
public sealed class SignalRPaymentsAdminNotifier : NeoPrint.Application.Refunds.IPaymentsAdminNotifier
{
    private readonly IHubContext<AdminHub> _hub;

    public SignalRPaymentsAdminNotifier(IHubContext<AdminHub> hub) => _hub = hub;

    public Task IncidentRaisedAsync(
        Guid incidentId, string type, string severity, CancellationToken cancellationToken = default, Guid? kioskId = null)
        => About(AdminAudience.Incidents, kioskId).SendAsync("IncidentRaised", new { incidentId, type, severity, kioskId }, cancellationToken);

    public Task IncidentResolvedAsync(
        Guid incidentId, string type, string status, CancellationToken cancellationToken = default, Guid? kioskId = null)
        => About(AdminAudience.Incidents, kioskId).SendAsync("IncidentResolved", new { incidentId, type, status, kioskId }, cancellationToken);

    /// <summary>
    /// The members of an audience that may see what the event is about: the
    /// kiosk, when there is one - and when there is none (a wallet top-up, a
    /// refund without a kiosk), those who see every kiosk, which is the rule
    /// the lists themselves apply (T-261).
    /// </summary>
    private IClientProxy About(AdminAudience audience, Guid? kioskId)
        => kioskId is { } kiosk
            ? _hub.Clients.Groups([AdminHub.AllKiosksGroup(audience), AdminHub.KioskGroup(audience, kiosk)])
            : _hub.Clients.Group(AdminHub.AllKiosksGroup(audience));

    public Task RefundStatusChangedAsync(
        Guid refundId, Guid paymentId, string status, CancellationToken cancellationToken = default, Guid? kioskId = null)
        => About(AdminAudience.Refunds, kioskId).SendAsync("RefundStatusChanged", new { refundId, paymentId, status }, cancellationToken);

    public Task PaymentSucceededAsync(Guid paymentId, Guid? kioskId, decimal amount, CancellationToken cancellationToken = default)
        => About(AdminAudience.Payments, kioskId).SendAsync("PaymentSucceeded", new { paymentId, kioskId, amount }, cancellationToken);

    public Task FiscalStatusChangedAsync(
        Guid receiptId, string status, CancellationToken cancellationToken = default, Guid? kioskId = null)
        => About(AdminAudience.Fiscal, kioskId).SendAsync("FiscalStatusChanged", new { receiptId, status }, cancellationToken);
}
