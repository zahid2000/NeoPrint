using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Notifications;

/// <summary>Persistence for <see cref="Notification"/> (plan section 5, <c>notifications</c>).</summary>
public interface INotificationRepository
{
    Task<Notification?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string dedupKey, CancellationToken cancellationToken = default);

    Task InsertAsync(Notification notification, CancellationToken cancellationToken = default);

    Task ReplaceAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>The user's newest first, older than <paramref name="before"/> when given.</summary>
    Task<IReadOnlyList<Notification>> ListAsync(Guid userId, DateTime? before, int take, CancellationToken cancellationToken = default);

    Task<long> CountUnreadAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>One push to one device.</summary>
/// <param name="Data">Small strings the app reads when the push is opened: the notification's id, type and link.</param>
public sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> Data);

public enum PushResult
{
    Delivered = 0,

    /// <summary>FCM no longer knows the token (app removed, token rotated): drop it.</summary>
    TokenGone = 1,

    /// <summary>Not sent this time (network, FCM busy); worth trying again.</summary>
    Failed = 2,
}

/// <summary>
/// Sends a push to one device (plan section 2.1: FCM HTTP v1, our own client;
/// without the FCM key a sender that only logs).
/// </summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells a user something: stored once per <paramref name="DedupKey"/>, then
/// delivered to the app's hub and as a push (<see cref="DeliverNotificationCommand"/>).
/// </summary>
/// <remarks>
/// Transactional, so the notification and the event that delivers it are
/// written together through the outbox: neither is kept without the other.
/// A repeat with the same key does nothing.
/// </remarks>
public sealed record NotifyUserCommand(
    Guid UserId,
    NotificationType Type,
    UserMessage Title,
    UserMessage Body,
    string? Link,
    string DedupKey) : IRequest, ITransactionalRequest;

/// <summary>Delivers a stored notification: the hub nudge, then a push to each device not reached yet.</summary>
public sealed record DeliverNotificationCommand(Guid NotificationId) : IRequest;

/// <summary><c>GET notifications</c> (plan section 6).</summary>
public sealed record ListNotificationsQuery(Guid UserId, DateTime? Before, int Take) : IRequest<NotificationPage>;

/// <summary><c>POST notifications/{id}/read</c> (plan section 6).</summary>
public sealed record MarkNotificationReadCommand(Guid UserId, Guid NotificationId) : IRequest<NotificationView>;

/// <summary><c>POST users/me/fcm-token</c> (plan section 6).</summary>
/// <param name="Language">The language the app shows on that device: its pushes are written in it.</param>
/// <param name="Platform"><c>android</c>, <c>ios</c> or <c>web</c>.</param>
public sealed record RegisterPushDeviceCommand(Guid UserId, string Token, string? Language, string? Platform) : IRequest;

/// <summary><c>DELETE users/me/fcm-token</c>: signing out on a device stops its pushes.</summary>
public sealed record RemovePushDeviceCommand(Guid UserId, string Token) : IRequest;

/// <param name="Title">In the reader's language.</param>
/// <param name="Link">The app screen it opens (<c>neoprint://…</c>), or null.</param>
public sealed record NotificationView(
    Guid Id, string Type, string Title, string Body, string? Link, DateTime CreatedAt, DateTime? ReadAt);

/// <param name="Unread">All the user's unread, for the badge - not just this page's.</param>
/// <param name="Next">The <c>before</c> for the next page, or null at the end.</param>
public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, long Unread, DateTime? Next);

public sealed class NotificationHandlers :
    IRequestHandler<NotifyUserCommand>,
    IRequestHandler<DeliverNotificationCommand>,
    IRequestHandler<ListNotificationsQuery, NotificationPage>,
    IRequestHandler<MarkNotificationReadCommand, NotificationView>,
    IRequestHandler<RegisterPushDeviceCommand>,
    IRequestHandler<RemovePushDeviceCommand>
{
    public const int MaxPageSize = 50;

    private readonly INotificationRepository _notifications;
    private readonly IUserRepository _users;
    private readonly IPushSender _push;
    private readonly IUserNotifier _hub;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<NotificationHandlers> _logger;

    public NotificationHandlers(
        INotificationRepository notifications,
        IUserRepository users,
        IPushSender push,
        IUserNotifier hub,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<NotificationHandlers> logger)
    {
        _notifications = notifications;
        _users = users;
        _push = push;
        _hub = hub;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task Handle(NotifyUserCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A second insert would also be refused by the unique index; this keeps
        // the ordinary repeat from aborting the transaction.
        if (await _notifications.ExistsAsync(request.DedupKey, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var notification = Notification.Create(
            request.UserId, request.Type, request.Title, request.Body, request.Link, request.DedupKey, _clock.UtcNow);

        await _notifications.InsertAsync(notification, cancellationToken).ConfigureAwait(false);
        await _events
            .PublishAsync(
                new Contracts.UserNotificationCreated(notification.Id, notification.UserId, notification.Type.ToString(), _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task Handle(DeliverNotificationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var notification = await _notifications.FindByIdAsync(request.NotificationId, cancellationToken).ConfigureAwait(false);
        if (notification is null || notification.Push != PushState.Pending)
        {
            return;
        }

        // The open app hears it at once; a repeat refreshes the same list.
        await _hub
            .NotificationAsync(notification.UserId, new UserNotificationUpdate(notification.Id, notification.Type.ToString()), cancellationToken)
            .ConfigureAwait(false);

        var user = await _users.FindByIdAsync(notification.UserId, cancellationToken).ConfigureAwait(false);
        var devices = user is { Status: UserStatus.Active } ? user.FcmTokens.ToList() : [];
        var failed = 0;

        foreach (var device in devices)
        {
            var hash = PushDevice.Hash(device.Token);
            if (notification.WasPushedTo(hash))
            {
                continue;
            }

            var result = await _push.SendAsync(device.Token, Message(notification, device.Language), cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case PushResult.Delivered:
                    notification.NotePushedTo(hash);
                    break;

                case PushResult.TokenGone:
                    _logger.LogInformation(
                        "Push token {TokenHash} of user {UserId} is no longer valid; removed.", hash, notification.UserId);
                    await _users.RemovePushDeviceAsync(device.Token, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    failed++;
                    break;
            }
        }

        if (failed == 0)
        {
            notification.FinishPush(anyDevice: notification.PushedTo.Count > 0);
        }

        await _notifications.ReplaceAsync(notification, cancellationToken).ConfigureAwait(false);

        if (failed > 0)
        {
            // The message is retried; the devices already reached are skipped.
            throw new PushDeliveryException(notification.Id, failed);
        }

        _logger.LogInformation(
            "Notification {NotificationId} ({Type}) delivered: {Devices} device(s), push {Push}.",
            notification.Id, notification.Type, notification.PushedTo.Count, notification.Push);
    }

    public async Task<NotificationPage> Handle(ListNotificationsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var take = Math.Clamp(request.Take, 1, MaxPageSize);
        var items = await _notifications.ListAsync(request.UserId, request.Before, take + 1, cancellationToken).ConfigureAwait(false);
        var page = items.Take(take).Select(View).ToList();
        var unread = await _notifications.CountUnreadAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        return new NotificationPage(page, unread, items.Count > take ? page[^1].CreatedAt : null);
    }

    public async Task<NotificationView> Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var notification = await _notifications.FindByIdAsync(request.NotificationId, cancellationToken).ConfigureAwait(false);
        if (notification is null || notification.UserId != request.UserId)
        {
            throw new NotFoundException("notification");
        }

        if (!notification.IsRead)
        {
            notification.MarkRead(_clock.UtcNow);
            await _notifications.ReplaceAsync(notification, cancellationToken).ConfigureAwait(false);
        }

        return View(notification);
    }

    public async Task Handle(RegisterPushDeviceCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = request.Token?.Trim() ?? string.Empty;
        if (token.Length == 0 || token.Length > PushDevice.MaxTokenLength || token.Any(char.IsWhiteSpace))
        {
            throw new ValidationException([new ValidationError("token", "notifications.tokenInvalid")]);
        }

        var platform = request.Platform?.Trim().ToLowerInvariant() is "android" or "ios" or "web" ? request.Platform.Trim().ToLowerInvariant() : "unknown";
        var device = new PushDevice
        {
            Token = token,
            Language = Messages.Supported(request.Language) ?? Messages.DefaultLanguage,
            Platform = platform,
            RegisteredAt = _clock.UtcNow,
        };

        await _users.SavePushDeviceAsync(request.UserId, device, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Push device {TokenHash} ({Platform}, {Language}) registered for user {UserId}.",
            PushDevice.Hash(token), device.Platform, device.Language, request.UserId);
    }

    public async Task Handle(RemovePushDeviceCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only the caller's own: a token on someone else's account is left alone.
        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var token = request.Token?.Trim() ?? string.Empty;
        if (user is not null && user.FcmTokens.Any(device => device.Token == token))
        {
            await _users.RemovePushDeviceAsync(token, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Written in the device's language; the data carries what the app needs to open it.</summary>
    private static PushMessage Message(Notification notification, string language)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["notificationId"] = notification.Id.ToString(),
            ["type"] = notification.Type.ToString(),
        };

        if (notification.Link is { } link)
        {
            data["link"] = link;
        }

        return new PushMessage(
            Messages.WriteStored(notification.Title, language) ?? string.Empty,
            Messages.WriteStored(notification.Body, language) ?? string.Empty,
            data);
    }

    /// <summary>In the request's language (<see cref="Messages.Current"/>).</summary>
    private static NotificationView View(Notification notification) => new(
        notification.Id,
        notification.Type.ToString(),
        Messages.WriteStored(notification.Title) ?? string.Empty,
        Messages.WriteStored(notification.Body) ?? string.Empty,
        notification.Link,
        notification.CreatedAt,
        notification.ReadAt);
}

/// <summary>Some device could not be reached this time; the message is retried.</summary>
public sealed class PushDeliveryException : Exception
{
    public PushDeliveryException(Guid notificationId, int devices)
        : base($"Push for notification {notificationId} failed on {devices} device(s); retrying.")
    {
    }

    public PushDeliveryException()
    {
    }

    public PushDeliveryException(string message)
        : base(message)
    {
    }

    public PushDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
