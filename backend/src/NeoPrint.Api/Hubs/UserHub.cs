using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Notifications;

namespace NeoPrint.Api.Hubs;

/// <summary>
/// Real-time channel for the customer app (plan section 6, <c>/hubs/user</c>).
/// </summary>
/// <remarks>
/// The user token names the user, and the connection joins that user's group
/// and no other: nobody hears about another customer's jobs or money. The
/// client sends nothing; the hub only speaks.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class UserHub : Hub
{
    public static string GroupFor(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        if (CallerClaims.UserId(Context.User) is not { } userId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (CallerClaims.UserId(Context.User) is { } userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(userId));
        }

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Sends the user events (plan section 6).</summary>
public sealed class SignalRUserNotifier : IUserNotifier
{
    private readonly IHubContext<UserHub> _hub;

    public SignalRUserNotifier(IHubContext<UserHub> hub) => _hub = hub;

    public Task JobStatusChangedAsync(Guid userId, UserJobUpdate update, CancellationToken cancellationToken = default)
        => Send(userId, "JobStatusChanged", update, cancellationToken);

    public Task PaymentStatusChangedAsync(Guid userId, UserPaymentUpdate update, CancellationToken cancellationToken = default)
        => Send(userId, "PaymentStatusChanged", update, cancellationToken);

    public Task RefundStatusChangedAsync(Guid userId, UserRefundUpdate update, CancellationToken cancellationToken = default)
        => Send(userId, "RefundStatusChanged", update, cancellationToken);

    public Task SessionClosedAsync(Guid userId, UserSessionUpdate update, CancellationToken cancellationToken = default)
        => Send(userId, "SessionClosed", update, cancellationToken);

    private Task Send(Guid userId, string method, object payload, CancellationToken cancellationToken)
        => _hub.Clients.Group(UserHub.GroupFor(userId)).SendAsync(method, payload, cancellationToken);
}
