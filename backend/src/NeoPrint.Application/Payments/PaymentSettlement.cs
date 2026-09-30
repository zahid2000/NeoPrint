using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.Payments;

/// <summary>Why money that was taken goes straight back (plan section 2.5; docs/DECISIONS.md 2026-09-30).</summary>
public static class OrphanRefundReasons
{
    /// <summary>The bank confirmed after the job had expired or been cancelled.</summary>
    public const string LatePayment = "LatePayment";

    /// <summary>The job was already paid by another payment: never charge twice.</summary>
    public const string DuplicatePayment = "DuplicatePayment";
}

/// <summary>
/// Applies "the provider took the money" - from a callback, the
/// reconciliation job or the expiry check - exactly once (plan section 9.1,
/// step 3; docs/IDEMPOTENCY.md).
/// </summary>
/// <remarks>
/// <para>
/// Runs inside the caller's transaction. The payment and the job carry row
/// versions, so two confirmations arriving together cannot both queue the
/// job: the second transaction conflicts, and when it is retried the payment
/// already reads as paid.
/// </para>
/// <para>
/// A job that can no longer be printed - it expired, was cancelled, or another
/// payment paid it - is not revived. The payment is still recorded as
/// succeeded (the money was taken) and a refund is requested in the same
/// transaction, so the customer is never charged for nothing and never twice.
/// </para>
/// </remarks>
public sealed class PaymentSettlement
{
    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _kiosks;
    private readonly IPaymentNotifier _paymentNotifier;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<PaymentSettlement> _logger;

    public PaymentSettlement(
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        PrintQueueService queue,
        IKioskNotifier kiosks,
        IPaymentNotifier paymentNotifier,
        IEventPublisher events,
        ICorrelationContext correlation,
        ILogger<PaymentSettlement> logger)
    {
        _payments = payments;
        _jobs = jobs;
        _queue = queue;
        _kiosks = kiosks;
        _paymentNotifier = paymentNotifier;
        _events = events;
        _correlation = correlation;
        _logger = logger;
    }

    /// <summary>
    /// Records the success and pays the job when it still can be.
    /// </summary>
    /// <returns>False when the payment was already paid: nothing was done.</returns>
    public async Task<bool> SucceedAsync(
        Payment payment,
        PaymentParts parts,
        PaymentMethod method,
        DateTime paidAt,
        ActorContext actor,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (!payment.Succeed(parts, method, paidAt, actor, now))
        {
            await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var job = await _jobs.FindByIdAsync(payment.JobId, cancellationToken).ConfigureAwait(false);
        var jobPaid = false;

        if (job is { Status: PrintJobStatus.PendingPayment, PaymentId: null })
        {
            job.MarkPaid(payment.Id, actor, now);
            job.Enqueue(actor, now);
            await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            jobPaid = true;
        }
        else
        {
            var reason = job?.PaymentId is { } other && other != payment.Id
                ? OrphanRefundReasons.DuplicatePayment
                : OrphanRefundReasons.LatePayment;

            payment.Note(now, "refundRequested", reason);

            _logger.LogWarning(
                "Payment {PaymentId} succeeded but job {JobId} is {JobStatus}; the money goes back ({Reason}).",
                payment.Id,
                payment.JobId,
                job?.Status,
                reason);

            await _events.PublishAsync(
                    new RefundRequested(payment.Id, payment.JobId, reason, null, $"orphan:{payment.Id}", _correlation.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(
                new PaymentSucceeded(
                    payment.Id,
                    payment.JobId,
                    payment.KioskId,
                    payment.UserId,
                    payment.Amount.Amount,
                    payment.Method.ToString(),
                    jobPaid,
                    _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (jobPaid)
        {
            await _queue.SyncAsync(job!, cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(new PrintJobQueued(job!.Id, job.KioskId, _correlation.CorrelationId), cancellationToken)
                .ConfigureAwait(false);

            if (payment.KioskId is { } kioskId)
            {
                await _paymentNotifier.PaymentConfirmedAsync(kioskId, payment.Id, job.Id, cancellationToken).ConfigureAwait(false);
            }

            await _kiosks.JobAddedAsync(job.KioskId, job.Id, cancellationToken).ConfigureAwait(false);
            await _queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Payment {PaymentId} succeeded ({Method}, {Amount} {Currency}); job {JobId} paid: {JobPaid}.",
            payment.Id,
            method,
            payment.Amount.Amount,
            payment.Amount.Currency,
            payment.JobId,
            jobPaid);

        return true;
    }
}
