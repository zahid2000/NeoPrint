using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Wallets;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Application.Refunds;

/// <summary>
/// Refunds (plan section 2.5; docs/STATE_MACHINES.md "Refund";
/// docs/IDEMPOTENCY.md: no double refund).
/// </summary>
/// <remarks>
/// <para>
/// Three guards keep money from going back twice: the refund's unique dedup
/// key (the same decision makes one refund), the planner's sum per part (all
/// refunds of a payment never exceed what that part paid), and the provider's
/// own key (our refund's id: asking again returns the first result).
/// </para>
/// <para>
/// A provider refund is tried <c>Refund.ProviderMaxAttempts</c> times, one,
/// two, four ... minutes apart; then it is Failed, the payment says
/// RefundFailed, an incident opens, and an admin retries it or sends it to a
/// wallet or a voucher.
/// </para>
/// </remarks>
public sealed class RefundHandlers :
    IRequestHandler<CreateRefundsCommand, IReadOnlyList<Guid>>,
    IRequestHandler<ProcessRefundCommand, string>,
    IRequestHandler<StartRefundCommand, RefundStep>,
    IRequestHandler<RecordProviderRefundCommand, string>,
    IRequestHandler<HandleInterruptedJobCommand, Unit>,
    IRequestHandler<HandleCancelledJobCommand, Unit>
{
    /// <summary>The reason a cancellation carries when the refund was decided with it: the cancel consumer leaves it alone.</summary>
    public const string CancelledForRefund = "printJobs.reason.cancelledForRefund";

    private readonly ISender _sender;
    private readonly RefundPlanner _planner;
    private readonly IRefundRepository _refunds;
    private readonly IRefundVoucherRepository _vouchers;
    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly IPrintAttemptRepository _attempts;
    private readonly IPaymentProviders _providers;
    private readonly WalletLedger _wallet;
    private readonly IncidentReporter _incidents;
    private readonly IPaymentsAdminNotifier _panel;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _kiosks;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<RefundHandlers> _logger;

    public RefundHandlers(
        ISender sender,
        RefundPlanner planner,
        IRefundRepository refunds,
        IRefundVoucherRepository vouchers,
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        IPrintAttemptRepository attempts,
        IPaymentProviders providers,
        WalletLedger wallet,
        IncidentReporter incidents,
        IPaymentsAdminNotifier panel,
        PrintQueueService queue,
        IKioskNotifier kiosks,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<RefundHandlers> logger)
    {
        _sender = sender;
        _planner = planner;
        _refunds = refunds;
        _vouchers = vouchers;
        _payments = payments;
        _jobs = jobs;
        _attempts = attempts;
        _providers = providers;
        _wallet = wallet;
        _incidents = incidents;
        _panel = panel;
        _queue = queue;
        _kiosks = kiosks;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Planning
    // -----------------------------------------------------------------------

    /// <inheritdoc cref="CreateRefundsCommand" />
    public Task<IReadOnlyList<Guid>> Handle(CreateRefundsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _planner.PlanAsync(request.Request, cancellationToken);
    }

    /// <inheritdoc cref="RefundPlanner.AllowedExternal" />
    public static IReadOnlyList<RefundDestination> AllowedExternal(Payment payment) => RefundPlanner.AllowedExternal(payment);

    // -----------------------------------------------------------------------
    // Carrying a refund out
    // -----------------------------------------------------------------------

    /// <inheritdoc cref="ProcessRefundCommand" />
    public async Task<string> Handle(ProcessRefundCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var step = await _sender.Send(new StartRefundCommand(request.RefundId), cancellationToken).ConfigureAwait(false);

        if (step.Done || step.Provider is null || step.ProviderRef is null || step.RefundKey is null)
        {
            return step.Done ? "Done" : "Waiting";
        }

        ProviderRefundResult result;

        try
        {
            // Outside any transaction: the provider is another system; the key
            // is our refund's id, so asking again cannot pay out twice.
            result = _providers.FindRefunding(step.Provider) is { } provider
                ? await provider.RefundAsync(step.ProviderRef, step.RefundKey, Money.FromAzn(step.Amount), cancellationToken).ConfigureAwait(false)
                : new ProviderRefundResult(ProviderRefundState.Failed, FailureCode: "noProvider");
        }
        catch (Exception exception) when (exception is PaymentProviderUnavailableException or PaymentOutcomeUnknownException)
        {
            result = new ProviderRefundResult(ProviderRefundState.Failed, FailureCode: "providerUnavailable");
        }

        return await _sender
            .Send(new RecordProviderRefundCommand(request.RefundId, result.State.ToString(), result.RefundRef, result.FailureCode), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="StartRefundCommand" />
    public async Task<RefundStep> Handle(StartRefundCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refund = await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("refund");

        if (refund.Status is RefundStatus.Succeeded or RefundStatus.Failed || refund.AwaitingProvider)
        {
            return new RefundStep(refund.Status is RefundStatus.Succeeded or RefundStatus.Failed, null, null, null, 0);
        }

        var payment = await _payments.FindByIdAsync(refund.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");
        var now = _clock.UtcNow;
        var actor = ActorContext.System("refunds.reason.processing", _correlation.CorrelationId);

        if (refund.Status == RefundStatus.Pending)
        {
            refund.Start(actor, now);
        }
        else if (refund.NextAttemptAt is { } due && due > now)
        {
            return new RefundStep(false, null, null, null, 0);
        }

        switch (refund.Destination)
        {
            case RefundDestination.Wallet or RefundDestination.Bonus:
                var owner = refund.UserId ?? throw new DomainException("refunds.noAccount");
                await _wallet
                    .PostAsync(
                        owner,
                        WalletTransactionType.Refund,
                        refund.Amount,
                        refund.BonusPages,
                        refund.Id.ToString(),
                        $"refund:{refund.Id}",
                        refund.InitiatorId,
                        now,
                        new UserMessage($"refunds.reasons.{refund.Reason}").Encode(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await CompleteAsync(refund, payment, null, actor, now, cancellationToken).ConfigureAwait(false);
                return new RefundStep(true, null, null, null, 0);

            case RefundDestination.Voucher:
                var voucher = await _vouchers.FindByRefundAsync(refund.Id, cancellationToken).ConfigureAwait(false);

                if (voucher is null)
                {
                    var days = await _settings.GetIntAsync(SettingKeys.RefundVoucherValidDays, 90, cancellationToken).ConfigureAwait(false);
                    voucher = RefundVoucher.Issue(payment.Id, refund.Id, refund.Amount, now, days);
                    await _vouchers.AddAsync(voucher, cancellationToken).ConfigureAwait(false);
                }

                await CompleteAsync(refund, payment, null, actor, now, cancellationToken, voucher.Code).ConfigureAwait(false);
                return new RefundStep(true, null, null, null, 0);

            default:
                if (payment.Provider is not { } provider || payment.ProviderRef is not { } providerRef)
                {
                    // Nothing external to give back to: the refund must go elsewhere.
                    await FailAsync(refund, payment, "refunds.noProviderToReturnTo", final: true, actor, now, cancellationToken).ConfigureAwait(false);
                    return new RefundStep(true, null, null, null, 0);
                }

                refund.CountAttempt();
                await _refunds.UpdateAsync(refund, cancellationToken).ConfigureAwait(false);
                return new RefundStep(false, provider, providerRef, refund.Id.ToString("N"), refund.Amount.Amount);
        }
    }

    /// <inheritdoc cref="RecordProviderRefundCommand" />
    public async Task<string> Handle(RecordProviderRefundCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refund = await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("refund");

        if (refund.Status is RefundStatus.Succeeded or RefundStatus.Failed)
        {
            return refund.Status.ToString();
        }

        var payment = await _payments.FindByIdAsync(refund.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");
        var now = _clock.UtcNow;
        var actor = ActorContext.System("refunds.reason.providerAnswered", _correlation.CorrelationId);

        switch (request.State)
        {
            case nameof(ProviderRefundState.Succeeded):
                await CompleteAsync(refund, payment, request.RefundRef, actor, now, cancellationToken).ConfigureAwait(false);
                return refund.Status.ToString();

            case nameof(ProviderRefundState.Pending):
                refund.AwaitProvider(request.RefundRef);
                await _refunds.UpdateAsync(refund, cancellationToken).ConfigureAwait(false);
                return "Waiting";

            default:
                var max = await _settings.GetIntAsync(SettingKeys.RefundProviderMaxAttempts, 3, cancellationToken).ConfigureAwait(false);
                var final = refund.Attempts >= Math.Max(1, max);

                await FailAsync(refund, payment, $"refunds.provider.{request.FailureCode ?? "failed"}", final, actor, now, cancellationToken)
                    .ConfigureAwait(false);
                return refund.Status.ToString();
        }
    }

    private async Task CompleteAsync(
        Refund refund, Payment payment, string? providerRef, ActorContext actor, DateTime now, CancellationToken cancellationToken, string? voucherCode = null)
    {
        refund.Succeed(providerRef, actor, now, voucherCode);

        if (refund.Amount.IsPositive)
        {
            // Refuses more than was paid: the last guard against a double refund.
            payment.ApplyRefund(refund.Amount, actor, now);
            await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
        }

        await _refunds.UpdateAsync(refund, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(
                new RefundCompleted(
                    refund.Id, refund.PaymentId, refund.JobId, refund.UserId, refund.Amount.Amount, refund.BonusPages, refund.Destination.ToString(), _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);
        await _panel.RefundStatusChangedAsync(refund.Id, refund.PaymentId, refund.Status.ToString(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Refund {RefundId} of {Amount} {Currency} to {Destination} completed.",
            refund.Id,
            refund.Amount.Amount,
            refund.Amount.Currency,
            refund.Destination);
    }

    private async Task FailAsync(
        Refund refund, Payment payment, string errorKey, bool final, ActorContext actor, DateTime now, CancellationToken cancellationToken)
    {
        // One, two, four ... minutes between tries.
        var retryAt = final ? (DateTime?)null : now.AddMinutes(Math.Pow(2, Math.Max(0, refund.Attempts - 1)));
        refund.Fail(errorKey, retryAt, actor, now);
        await _refunds.UpdateAsync(refund, cancellationToken).ConfigureAwait(false);

        if (!final)
        {
            _logger.LogWarning("Refund {RefundId} try {Attempt} failed ({Error}); next try at {RetryAt}.", refund.Id, refund.Attempts, errorKey, retryAt);
            return;
        }

        if (payment.Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded)
        {
            payment.MarkRefundFailed(actor, now);
            await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
        }

        await _incidents
            .RaiseAsync(
                IncidentType.RefundFailed,
                IncidentSeverity.High,
                UserMessage.Of("incidents.refundFailed", ("amount", refund.Amount.Amount), ("attempts", refund.Attempts)),
                $"refund-failed:{refund.Id}",
                cancellationToken,
                payment.KioskId,
                nameof(Refund),
                refund.Id)
            .ConfigureAwait(false);

        await _events.PublishAsync(
                new RefundFailed(refund.Id, refund.PaymentId, refund.JobId, refund.Destination.ToString(), _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);
        await _panel.RefundStatusChangedAsync(refund.Id, refund.PaymentId, refund.Status.ToString(), cancellationToken).ConfigureAwait(false);

        _logger.LogError("Refund {RefundId} failed after {Attempts} tries ({Error}); an admin decides.", refund.Id, refund.Attempts, errorKey);
    }

    // -----------------------------------------------------------------------
    // What triggers a refund
    // -----------------------------------------------------------------------

    /// <inheritdoc cref="HandleInterruptedJobCommand" />
    public async Task<Unit> Handle(HandleInterruptedJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is not { Status: PrintJobStatus.Interrupted, PaymentId: { } paymentId })
        {
            return Unit.Value;
        }

        var payment = await _payments.FindByIdAsync(paymentId, cancellationToken).ConfigureAwait(false);

        // Automatic only for an anonymous bank payment at the kiosk (plan
        // section 2.5): a registered user is offered a refund instead, and the
        // job prints when the kiosk recovers if they wait.
        if (payment is not { WasPaid: true, UserId: null, Method: PaymentMethod.BankQr, Parts: { } parts })
        {
            return Unit.Value;
        }

        var attempts = await _attempts.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var evidence = RefundEvidencePolicy.Decide(
            job.Price.Price.Sides,
            job.Options.Duplex,
            attempts.Select(a => new AttemptEvidence(a.PrinterCounterBefore, a.PrinterCounterAfter, a.CounterUnit, a.SpoolerJobId, a.Status)).ToList(),
            job.PrintedSides);
        var amount = RefundEvidencePolicy.Unprinted(parts.Money, evidence);
        var now = _clock.UtcNow;
        var actor = ActorContext.System(CancelledForRefund, _correlation.CorrelationId);

        await PrintJobCancellation
            .CancelAsync(job, new UserMessage(CancelledForRefund), actor, now, _jobs, _queue, _kiosks, _events, _correlation, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Job {JobId} stopped at the printer: {Printed}/{Ordered} sides by {Source}; {Amount} goes back to the card.",
            job.Id,
            evidence.PrintedSides,
            evidence.OrderedSides,
            evidence.Source,
            amount.Amount);

        if (amount.IsPositive)
        {
            await _events.PublishAsync(
                    new RefundRequested(
                        payment.Id,
                        job.Id,
                        (evidence.PrintedSides > 0 ? RefundReason.PartialPrint : RefundReason.SystemError).ToString(),
                        amount.Amount,
                        $"auto:{job.Id}",
                        _correlation.CorrelationId,
                        Evidence: new RefundEvidenceData(evidence.Source.ToString(), evidence.PrintedSides, evidence.OrderedSides, evidence.Detail)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Unit.Value;
    }

    /// <inheritdoc cref="HandleCancelledJobCommand" />
    public async Task<Unit> Handle(HandleCancelledJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cancelled = request.Cancelled;

        // An intent still open for it must not be paid any more.
        await _sender.Send(new CloseJobPaymentsCommand(cancelled.JobId), cancellationToken).ConfigureAwait(false);

        if (cancelled.PaymentId is not { } paymentId || cancelled.ReasonKey == CancelledForRefund)
        {
            return Unit.Value;
        }

        var reason = cancelled.CancelledBy switch
        {
            nameof(ActorType.Admin) => RefundReason.AdminManual,
            nameof(ActorType.System) => RefundReason.SystemError,
            _ => RefundReason.UserCancel,
        };

        await _events.PublishAsync(
                new RefundRequested(paymentId, cancelled.JobId, reason.ToString(), null, $"cancel:{cancelled.JobId}", _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}
