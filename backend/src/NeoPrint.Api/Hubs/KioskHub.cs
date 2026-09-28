using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;

namespace NeoPrint.Api.Hubs;

/// <summary>
/// Real-time channel for an activated kiosk (plan section 6, <c>/hubs/kiosk</c>).
/// </summary>
/// <remarks>
/// The kiosk token names the kiosk, and the connection joins that kiosk's
/// group and no other: one kiosk never hears about another's queue. The
/// device check behind the policy runs on connect, so a detached device's
/// connection is refused (plan section 7.11).
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class KioskHub : Hub
{
    private readonly ICurrentUser _currentUser;

    public KioskHub(ICurrentUser currentUser) => _currentUser = currentUser;

    public static string GroupFor(Guid kioskId) => $"kiosk:{kioskId}";

    public override async Task OnConnectedAsync()
    {
        if (_currentUser.KioskId is not { } kioskId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(kioskId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_currentUser.KioskId is { } kioskId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(kioskId));
        }

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Sends the kiosk events (plan section 6).</summary>
public sealed class SignalRKioskNotifier : IKioskNotifier
{
    private readonly IHubContext<KioskHub> _hub;

    public SignalRKioskNotifier(IHubContext<KioskHub> hub) => _hub = hub;

    public Task QueueUpdatedAsync(Guid kioskId, IReadOnlyList<QueueEntry> queue, CancellationToken cancellationToken = default)
        => Send(kioskId, "QueueUpdated", new { kioskId, queue }, cancellationToken);

    public Task JobAddedAsync(Guid kioskId, Guid jobId, CancellationToken cancellationToken = default)
        => Send(kioskId, "JobAdded", new { kioskId, jobId }, cancellationToken);

    public Task JobCancelledAsync(Guid kioskId, Guid jobId, CancellationToken cancellationToken = default)
        => Send(kioskId, "JobCancelled", new { kioskId, jobId }, cancellationToken);

    public Task PrinterConfigChangedAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => Send(kioskId, "PrinterConfigChanged", new { kioskId }, cancellationToken);

    public Task KioskConfigChangedAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => Send(kioskId, "KioskConfigChanged", new { kioskId }, cancellationToken);

    public Task KioskCommandAsync(Guid kioskId, KioskCommandMessage command, CancellationToken cancellationToken = default)
        => Send(kioskId, "KioskCommand", command, cancellationToken);

    private Task Send(Guid kioskId, string method, object payload, CancellationToken cancellationToken)
        => _hub.Clients.Group(KioskHub.GroupFor(kioskId)).SendAsync(method, payload, cancellationToken);
}
