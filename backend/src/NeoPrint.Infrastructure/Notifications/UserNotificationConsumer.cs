using MassTransit;

using MediatR;

using NeoPrint.Application.Notifications;
using NeoPrint.Application.Refunds;
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
    private readonly IPaymentsAdminNotifier _panels;

    public UserNotificationConsumer(ISender sender, IPaymentsAdminNotifier panels)
    {
        _sender = sender;
        _panels = panels;
    }

    // A paid job joins the queue with this event alone, not with a status change.
    public Task Consume(ConsumeContext<PrintJobQueued> context)
        => _sender.Send(new NotifyJobOwnerCommand(Message(context).JobId), context.CancellationToken);

    public Task Consume(ConsumeContext<PrintJobStatusChanged> context)
        => _sender.Send(new NotifyJobOwnerCommand(Message(context).JobId), context.CancellationToken);

    public Task Consume(ConsumeContext<PrintJobCancelled> context)
        => _sender.Send(new NotifyJobOwnerCommand(Message(context).JobId), context.CancellationToken);

    // The payer's app, and the panels: the payments list and the dashboard's
    // money card follow a payment without a reload. The notifier method was
    // there from step 4 and nothing called it (T-261) - from here it is sent
    // after the payment is committed, to those who may see it.
    public async Task Consume(ConsumeContext<PaymentSucceeded> context)
    {
        var payment = Message(context);

        await _sender.Send(new NotifyPayerCommand(payment.PaymentId), context.CancellationToken).ConfigureAwait(false);
        await _panels.PaymentSucceededAsync(payment.PaymentId, payment.KioskId, payment.Amount, context.CancellationToken).ConfigureAwait(false);
    }

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
