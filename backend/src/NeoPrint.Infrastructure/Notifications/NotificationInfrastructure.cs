using MassTransit;

using MediatR;

using MongoDB.Driver;

using NeoPrint.Application.Notifications;
using NeoPrint.Contracts;
using NeoPrint.Domain.Notifications;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Notifications;

/// <inheritdoc cref="INotificationRepository" />
public sealed class NotificationRepository : INotificationRepository
{
    private readonly MongoContext _context;

    public NotificationRepository(MongoContext context) => _context = context;

    public Task<Notification?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Notifications.Find(n => n.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<bool> ExistsAsync(string dedupKey, CancellationToken cancellationToken = default)
        => _context.Notifications.Find(n => n.DedupKey == dedupKey).AnyAsync(cancellationToken);

    public Task InsertAsync(Notification notification, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Notifications, notification, cancellationToken);

    public Task ReplaceAsync(Notification notification, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Notifications, notification, cancellationToken);

    public async Task<IReadOnlyList<Notification>> ListAsync(
        Guid userId, DateTime? before, int take, CancellationToken cancellationToken = default)
    {
        var filter = Builders<Notification>.Filter.Eq(n => n.UserId, userId);
        if (before is { } cursor)
        {
            filter &= Builders<Notification>.Filter.Lt(n => n.CreatedAt, cursor);
        }

        return await _context.Notifications
            .Find(filter)
            .SortByDescending(n => n.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<long> CountUnreadAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.Notifications.CountDocumentsAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken: cancellationToken);
}

/// <summary>
/// Delivers a stored notification: the hub, then the push (plan section 8.4).
/// </summary>
/// <remarks>
/// A device FCM could not reach makes the handler throw and the bus retry it
/// (exponential, then the _error queue); the devices already reached are
/// remembered on the notification and skipped.
/// </remarks>
public sealed class NotificationDeliveryConsumer : IConsumer<UserNotificationCreated>
{
    private readonly ISender _sender;

    public NotificationDeliveryConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<UserNotificationCreated> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new DeliverNotificationCommand(context.Message.NotificationId), context.CancellationToken);
    }
}

/// <summary>Turns the events a user should hear about into notifications (plan section 8.4).</summary>
public sealed class NotificationTriggerConsumer :
    IConsumer<PrintJobStatusChanged>,
    IConsumer<RefundCompleted>
{
    private readonly ISender _sender;

    public NotificationTriggerConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<PrintJobStatusChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Message.To == nameof(Domain.PrintJobs.PrintJobStatus.Completed)
            ? _sender.Send(new NotifyJobCompletedCommand(context.Message.JobId), context.CancellationToken)
            : Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<RefundCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Message.UserId is null
            ? Task.CompletedTask
            : _sender.Send(new NotifyRefundCompletedCommand(context.Message.RefundId), context.CancellationToken);
    }
}
