using MassTransit;

using MediatR;

using NeoPrint.Application.Notifications;
using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Notifications;

/// <summary>
/// Tells the user's app that one of their jobs, payments or refunds moved on
/// (<c>/hubs/user</c>, plan section 6).
/// </summary>
/// <remarks>
/// One queue for every API instance is enough: the hub's Redis backplane
/// carries the message to whichever instance holds the user's connection.
/// Each message is the current status read again, so a redelivery repeats a
/// harmless nudge.
/// </remarks>
public sealed class UserNotificationConsumer :
    IConsumer<PrintJobQueued>,
    IConsumer<PrintJobStatusChanged>,
    IConsumer<PrintJobCancelled>,
    IConsumer<PaymentSucceeded>,
    IConsumer<PaymentExpired>,
    IConsumer<RefundCompleted>,
    IConsumer<RefundFailed>
{
    private readonly ISender _sender;

    public UserNotificationConsumer(ISender sender) => _sender = sender;

    // A paid job joins the queue with this event alone, not with a status change.
    public Task Consume(ConsumeContext<PrintJobQueued> context)
        => _sender.Send(new NotifyJobOwnerCommand(Message(context).JobId), context.CancellationToken);

    public Task Consume(ConsumeContext<PrintJobStatusChanged> context)
        => _sender.Send(new NotifyJobOwnerCommand(Message(context).JobId), context.CancellationToken);

    public Task Consume(ConsumeContext<PrintJobCancelled> context)
        => _sender.Send(new NotifyJobOwnerCommand(Message(context).JobId), context.CancellationToken);

    public Task Consume(ConsumeContext<PaymentSucceeded> context)
        => _sender.Send(new NotifyPayerCommand(Message(context).PaymentId), context.CancellationToken);

    public Task Consume(ConsumeContext<PaymentExpired> context)
        => _sender.Send(new NotifyPayerCommand(Message(context).PaymentId), context.CancellationToken);

    public Task Consume(ConsumeContext<RefundCompleted> context)
        => _sender.Send(new NotifyRefundOwnerCommand(Message(context).RefundId), context.CancellationToken);

    public Task Consume(ConsumeContext<RefundFailed> context)
        => _sender.Send(new NotifyRefundOwnerCommand(Message(context).RefundId), context.CancellationToken);

    private static T Message<T>(ConsumeContext<T> context)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Message;
    }
}
