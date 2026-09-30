using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Payments;

/// <summary>
/// Payments at the kiosk and from the providers (plan sections 2.5, 9.1,
/// 9.2; docs/PAYMENT_FLOWS.md).
/// </summary>
public sealed class PaymentHandlers :
    IRequestHandler<CreateKioskPaymentIntentCommand, PaymentResponse>,
    IRequestHandler<GetKioskPaymentQuery, PaymentResponse>,
    IRequestHandler<HandlePaymentCallbackCommand, PaymentCallbackOutcome>,
    IRequestHandler<ExpirePaymentCommand, Unit>,
    IRequestHandler<ExpireUnpaidJobCommand, Unit>,
    IRequestHandler<CloseJobPaymentsCommand, Unit>,
    IRequestHandler<ReconcilePaymentCommand, Unit>
{
    /// <summary>Bounds for <c>Payment.TimeoutMinutes</c>, whatever the panel holds.</summary>
    private const int MinTimeoutMinutes = 1;

    private const int MaxTimeoutMinutes = 60;

    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskDeviceRepository _devices;
    private readonly IPaymentProviders _providers;
    private readonly IProviderEventStore _providerEvents;
    private readonly PaymentSettlement _settlement;
    private readonly PrintQueueService _queue;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<PaymentHandlers> _logger;

    public PaymentHandlers(
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        IKioskDeviceRepository devices,
        IPaymentProviders providers,
        IProviderEventStore providerEvents,
        PaymentSettlement settlement,
        PrintQueueService queue,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<PaymentHandlers> logger)
    {
        _payments = payments;
        _jobs = jobs;
        _devices = devices;
        _providers = providers;
        _providerEvents = providerEvents;
        _settlement = settlement;
        _queue = queue;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="CreateKioskPaymentIntentCommand" />
    public async Task<PaymentResponse> Handle(CreateKioskPaymentIntentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Method is not (PaymentMethod.BankQr or PaymentMethod.MobileApp))
        {
            // Wallet, card and bonus are chosen in the app, never at the kiosk.
            throw new ValidationException([new ValidationError("method", "payments.methodNotAtKiosk")]);
        }

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is null || job.KioskId != kioskId)
        {
            throw new NotFoundException("printJob");
        }

        if (job.Status != PrintJobStatus.PendingPayment)
        {
            throw new DomainException(UserMessage.Of("payments.jobNotAwaitingPayment", ("status", job.Status)));
        }

        var now = _clock.UtcNow;
        var actor = ActorContext.Kiosk(request.DeviceId, correlationId: _correlation.CorrelationId);
        var earlier = await _payments.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);

        if (earlier.FirstOrDefault(payment => payment.WasPaid) is { } paid)
        {
            // Paid a moment ago: show the receipt, do not take the money twice.
            return Response(paid, job, now);
        }

        foreach (var open in earlier.Where(payment => payment.Status == PaymentStatus.Pending))
        {
            if (open.Method == request.Method && open.ExpiresAt > now)
            {
                return Response(open, job, now);
            }

            // Another method, or an intent that ran out: close it at the bank
            // first. If the customer paid it in the meantime, that payment wins.
            if (await CloseAsync(open, actor.Because("payments.reason.methodChanged"), now, cancellationToken).ConfigureAwait(false))
            {
                return Response(open, job, now);
            }
        }

        var minutes = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.PaymentTimeoutMinutes, 5, cancellationToken).ConfigureAwait(false),
            MinTimeoutMinutes,
            MaxTimeoutMinutes);

        var payment = Payment.Open(
            job.Id,
            kioskId,
            request.Method,
            PaymentChannel.Kiosk,
            job.Price.Total,
            now,
            now.AddMinutes(minutes),
            request.IdempotencyKey);

        if (request.Method == PaymentMethod.BankQr)
        {
            var bank = _providers.BankQr;
            var intent = await bank.CreateQrAsync(payment.Id, payment.Amount, payment.ExpiresAt, cancellationToken).ConfigureAwait(false);
            payment.AttachProvider(bank.Name, intent.ProviderRef, intent.QrContent, now);
        }
        else
        {
            payment.ShowAppQr(now);
        }

        await _payments.AddAsync(payment, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Payment {PaymentId} opened for job {JobId}: {Method}, {Amount} {Currency}, expires in {Minutes} min.",
            payment.Id,
            job.Id,
            payment.Method,
            payment.Amount.Amount,
            payment.Amount.Currency,
            minutes);

        return Response(payment, job, now);
    }

    /// <inheritdoc cref="GetKioskPaymentQuery" />
    public async Task<PaymentResponse> Handle(GetKioskPaymentQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false);

        if (payment is null || payment.KioskId != kioskId)
        {
            throw new NotFoundException("payment");
        }

        var job = payment.JobId is { } jobId
            ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("printJob")
            : throw new NotFoundException("printJob");

        return Response(payment, job, _clock.UtcNow);
    }

    /// <inheritdoc cref="HandlePaymentCallbackCommand" />
    public async Task<PaymentCallbackOutcome> Handle(HandlePaymentCallbackCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = _providers.Find(request.Provider) ?? throw new NotFoundException("paymentProvider");

        if (provider.ReadCallback(request.Body, request.Signature) is not { } callback)
        {
            // Not from the provider. The body is not logged: it may be anything.
            _logger.LogWarning("A {Provider} callback with a signature that does not hold was refused.", provider.Name);
            throw new AuthenticationException("payments.callbackSignatureInvalid");
        }

        if (!await _providerEvents
                .TryRecordAsync(provider.Name, callback.EventId, callback.Type, callback.PayloadHash, _correlation.CorrelationId, cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation("{Provider} event {EventId} ({Type}) arrived again; ignored.", provider.Name, callback.EventId, callback.Type);
            return PaymentCallbackOutcome.Duplicate;
        }

        var payment = await _payments.FindByProviderRefAsync(provider.Name, callback.ProviderRef, cancellationToken).ConfigureAwait(false);

        if (payment is null)
        {
            _logger.LogWarning("{Provider} event {EventId} ({Type}) names a transaction we do not have.", provider.Name, callback.EventId, callback.Type);
            return PaymentCallbackOutcome.Ignored;
        }

        var now = _clock.UtcNow;
        var actor = ActorContext.System("payments.reason.providerCallback", _correlation.CorrelationId);

        switch (callback.Type)
        {
            case ProviderCallbackTypes.PaymentSucceeded:
                // The provider's own figure is what was taken; Succeed refuses a mismatch.
                var taken = callback.Amount ?? payment.Amount;

                if (taken != payment.Amount)
                {
                    payment.Note(now, "amountMismatch", taken.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);

                    _logger.LogCritical(
                        "{Provider} reports {Taken} for payment {PaymentId} of {Amount}; not applied.",
                        provider.Name,
                        taken.Amount,
                        payment.Id,
                        payment.Amount.Amount);

                    return PaymentCallbackOutcome.Ignored;
                }

                await _settlement
                    .SucceedAsync(payment, PaymentParts.ExternalOnly(taken), payment.Method, callback.OccurredAt, actor, now, cancellationToken)
                    .ConfigureAwait(false);
                return PaymentCallbackOutcome.Applied;

            case ProviderCallbackTypes.PaymentFailed:
                if (payment.Status == PaymentStatus.Pending)
                {
                    payment.Fail("payments.declined", actor.Because(callback.FailureCode), now);
                    await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
                }

                return PaymentCallbackOutcome.Applied;

            default:
                _logger.LogWarning("{Provider} event {EventId} has a type we do not act on: {Type}.", provider.Name, callback.EventId, callback.Type);
                return PaymentCallbackOutcome.Ignored;
        }
    }

    /// <inheritdoc cref="ExpirePaymentCommand" />
    public async Task<Unit> Handle(ExpirePaymentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        if (payment is not { Status: PaymentStatus.Pending } || payment.ExpiresAt > now)
        {
            return Unit.Value;
        }

        var actor = ActorContext.System("payments.reason.timeout", _correlation.CorrelationId);

        if (!await CloseAsync(payment, actor, now, cancellationToken).ConfigureAwait(false))
        {
            if (payment.JobId is { } jobId)
            {
                await ExpireJobIfNothingPendingAsync(jobId, actor, now, cancellationToken).ConfigureAwait(false);
            }
        }

        return Unit.Value;
    }

    /// <inheritdoc cref="ExpireUnpaidJobCommand" />
    public async Task<Unit> Handle(ExpireUnpaidJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorContext.System("payments.reason.timeout", _correlation.CorrelationId);
        await ExpireJobIfNothingPendingAsync(request.JobId, actor, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }

    /// <inheritdoc cref="CloseJobPaymentsCommand" />
    /// <remarks>Runs inside the caller's transaction (the cancellation handler).</remarks>
    public async Task<Unit> Handle(CloseJobPaymentsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var actor = ActorContext.System("payments.reason.jobCancelled", _correlation.CorrelationId);

        foreach (var open in (await _payments.ListForJobAsync(request.JobId, cancellationToken).ConfigureAwait(false))
                     .Where(payment => payment.Status == PaymentStatus.Pending))
        {
            await CloseAsync(open, actor, now, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }

    /// <inheritdoc cref="ReconcilePaymentCommand" />
    public async Task<Unit> Handle(ReconcilePaymentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false);

        if (payment is null
            || payment.ReconciledAt is not null
            || payment.WasPaid
            || payment.Provider is not { } name
            || payment.ProviderRef is not { } providerRef
            || _providers.Find(name) is not { } provider)
        {
            return Unit.Value;
        }

        var status = await provider.GetStatusAsync(providerRef, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var actor = ActorContext.System("payments.reason.reconciled", _correlation.CorrelationId);

        switch (status.State)
        {
            case ProviderPaymentState.Succeeded:
                // The callback never came, or came after the intent expired.
                payment.MarkReconciled("succeeded", now);
                await _settlement
                    .SucceedAsync(payment, PaymentParts.ExternalOnly(status.Amount ?? payment.Amount), payment.Method, status.PaidAt ?? now, actor, now, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ProviderPaymentState.Failed when payment.Status == PaymentStatus.Pending:
                payment.Fail("payments.declined", actor, now);
                payment.MarkReconciled("failed", now);
                await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
                break;

            case ProviderPaymentState.Pending when payment.Status == PaymentStatus.Pending:
                // Still open at the bank and here: the expiry job owns it.
                break;

            default:
                payment.MarkReconciled(status.State.ToString(), now);
                await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
                break;
        }

        return Unit.Value;
    }

    /// <summary>
    /// Closes an unpaid intent at the provider, then here.
    /// </summary>
    /// <returns>True when the provider says it was paid after all; the success has been applied.</returns>
    private async Task<bool> CloseAsync(Payment payment, ActorContext actor, DateTime now, CancellationToken cancellationToken)
    {
        if (payment.Reserved is { } reserved)
        {
            // An app payment set money aside: if its card was charged after
            // all, it is paid; otherwise the wallet gets its part back.
            if (payment.Provider == _providers.Card.Name && payment.ProviderRef is { } cardRef
                && (await _providers.Card.GetStatusAsync(cardRef, cancellationToken).ConfigureAwait(false)) is { State: ProviderPaymentState.Succeeded } charged)
            {
                await _settlement.SucceedAsync(payment, reserved, payment.Method, charged.PaidAt ?? now, actor, now, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await _settlement.ReleaseReservationAsync(payment, now, cancellationToken).ConfigureAwait(false);
        }

        if (payment.Provider is { } name && payment.ProviderRef is { } providerRef && _providers.Find(name) is { } provider)
        {
            var status = await provider.CancelAsync(providerRef, cancellationToken).ConfigureAwait(false);

            if (status.State == ProviderPaymentState.Succeeded)
            {
                await _settlement
                    .SucceedAsync(payment, PaymentParts.ExternalOnly(status.Amount ?? payment.Amount), payment.Method, status.PaidAt ?? now, actor, now, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        payment.Expire(actor, now);
        await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new PaymentExpired(payment.Id, payment.JobId, payment.KioskId, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>The job expires with its last intent; a newer intent keeps it waiting.</summary>
    private async Task ExpireJobIfNothingPendingAsync(Guid jobId, ActorContext actor, DateTime now, CancellationToken cancellationToken)
    {
        var job = await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (job is not { Status: PrintJobStatus.PendingPayment })
        {
            return;
        }

        var payments = await _payments.ListForJobAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (payments.Any(payment => payment.Status == PaymentStatus.Pending || payment.WasPaid))
        {
            return;
        }

        var before = job.Status;
        job.Expire(actor, now);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(
                new PrintJobStatusChanged(job.Id, job.KioskId, before.ToString(), job.Status.ToString(), _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Job {JobId} expired unpaid.", job.Id);
    }

    private async Task<Guid> KioskOfAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);

        return device?.KioskId ?? throw new DomainException("devices.notBound");
    }

    private static PaymentResponse Response(Payment payment, PrintJob job, DateTime now)
        => new(
            payment.Id,
            job.Id,
            job.JobCode,
            payment.Method.ToString(),
            payment.Status.ToString(),
            payment.Amount.Amount,
            payment.Amount.Currency,
            payment.Status == PaymentStatus.Pending ? payment.QrContent : null,
            payment.ExpiresAt,
            payment.Status == PaymentStatus.Pending ? Math.Max(0, (int)Math.Ceiling((payment.ExpiresAt - now).TotalSeconds)) : 0,
            payment.PaidAt);
}

/// <summary>The callback types every provider adapter maps its own names to.</summary>
public static class ProviderCallbackTypes
{
    public const string PaymentSucceeded = "payment.succeeded";

    public const string PaymentFailed = "payment.failed";

    public const string RefundSucceeded = "refund.succeeded";

    public const string RefundFailed = "refund.failed";
}
