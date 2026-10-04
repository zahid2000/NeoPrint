using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Api.Infrastructure;
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
    public static string GroupFor(Guid kioskId) => $"kiosk:{kioskId}";

    /// <summary>Every connected kiosk: for what all their configurations carry (feature flags).</summary>
    public const string AllKiosksGroup = "kiosks";

    public override async Task OnConnectedAsync()
    {
        if (CallerClaims.KioskId(Context.User) is not { } kioskId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(kioskId));
        await Groups.AddToGroupAsync(Context.ConnectionId, AllKiosksGroup);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (CallerClaims.KioskId(Context.User) is { } kioskId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(kioskId));
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AllKiosksGroup);

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Sends the kiosk events (plan section 6).</summary>
public sealed class SignalRKioskNotifier : IKioskNotifier, NeoPrint.Application.Payments.IPaymentNotifier
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

    public Task AllKiosksConfigChangedAsync(CancellationToken cancellationToken = default)
        => _hub.Clients.Group(KioskHub.AllKiosksGroup).SendAsync("KioskConfigChanged", new { kioskId = Guid.Empty }, cancellationToken);

    /// <summary>The customer at this kiosk paid (plan section 7.3, screen 7: SignalR plus a polling fallback).</summary>
    public Task PaymentConfirmedAsync(Guid kioskId, Guid paymentId, Guid jobId, CancellationToken cancellationToken = default)
        => Send(kioskId, "PaymentConfirmed", new { kioskId, paymentId, jobId }, cancellationToken);

    public Task KioskCommandAsync(Guid kioskId, KioskCommandMessage command, CancellationToken cancellationToken = default)
        => Send(kioskId, "KioskCommand", command, cancellationToken);

    public Task SessionOpenedAsync(Guid kioskId, Guid sessionId, DateTime expiresAt, CancellationToken cancellationToken = default)
        => Send(kioskId, "SessionOpened", new { kioskId, sessionId, expiresAt }, cancellationToken);

    public Task SessionClosedAsync(Guid kioskId, Guid sessionId, string reason, CancellationToken cancellationToken = default)
        => Send(kioskId, "SessionClosed", new { kioskId, sessionId, reason }, cancellationToken);

    private Task Send(Guid kioskId, string method, object payload, CancellationToken cancellationToken)
        => _hub.Clients.Group(KioskHub.GroupFor(kioskId)).SendAsync(method, payload, cancellationToken);
}
