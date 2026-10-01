using MediatR;

using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;

namespace NeoPrint.Application.Notifications;

/// <summary>
/// Real-time messages to the user's own app over <c>/hubs/user</c> (plan
/// section 6: group <c>user:{userId}</c>).
/// </summary>
/// <remarks>
/// A nudge, not the record: each message carries the current status, and the
/// app reads the details from the API. A lost or repeated message changes
/// nothing - the next read is right either way.
/// </remarks>
public interface IUserNotifier
{
    Task JobStatusChangedAsync(Guid userId, UserJobUpdate update, CancellationToken cancellationToken = default);

    Task PaymentStatusChangedAsync(Guid userId, UserPaymentUpdate update, CancellationToken cancellationToken = default);

    Task RefundStatusChangedAsync(Guid userId, UserRefundUpdate update, CancellationToken cancellationToken = default);
}

public sealed record UserJobUpdate(Guid JobId, string JobCode, string Status);

public sealed record UserPaymentUpdate(Guid PaymentId, Guid? JobId, string Status);

public sealed record UserRefundUpdate(Guid RefundId, Guid PaymentId, Guid? JobId, string Status, decimal Amount, string Destination);

/// <summary>A job moved on: its owner hears the status it has now.</summary>
public sealed record NotifyJobOwnerCommand(Guid JobId) : IRequest;

/// <summary>A payment moved on: its payer hears the status it has now.</summary>
public sealed record NotifyPayerCommand(Guid PaymentId) : IRequest;

/// <summary>A refund moved on: the customer it goes back to hears the status it has now.</summary>
public sealed record NotifyRefundOwnerCommand(Guid RefundId) : IRequest;

public sealed class UserNotificationHandlers :
    IRequestHandler<NotifyJobOwnerCommand>,
    IRequestHandler<NotifyPayerCommand>,
    IRequestHandler<NotifyRefundOwnerCommand>
{
    private readonly IPrintJobRepository _jobs;
    private readonly IPaymentRepository _payments;
    private readonly IRefundRepository _refunds;
    private readonly IUserNotifier _notifier;

    public UserNotificationHandlers(
        IPrintJobRepository jobs, IPaymentRepository payments, IRefundRepository refunds, IUserNotifier notifier)
    {
        _jobs = jobs;
        _payments = payments;
        _refunds = refunds;
        _notifier = notifier;
    }

    public async Task Handle(NotifyJobOwnerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An anonymous kiosk order has nobody to tell.
        if (await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false) is { UserId: { } userId } job)
        {
            await _notifier
                .JobStatusChangedAsync(userId, new UserJobUpdate(job.Id, job.JobCode, job.Status.ToString()), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task Handle(NotifyPayerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false) is { UserId: { } userId } payment)
        {
            await _notifier
                .PaymentStatusChangedAsync(userId, new UserPaymentUpdate(payment.Id, payment.JobId, payment.Status.ToString()), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task Handle(NotifyRefundOwnerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false) is { UserId: { } userId } refund)
        {
            await _notifier
                .RefundStatusChangedAsync(
                    userId,
                    new UserRefundUpdate(
                        refund.Id, refund.PaymentId, refund.JobId, refund.Status.ToString(), refund.Amount.Amount, refund.Destination.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
