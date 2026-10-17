using MassTransit;

using MediatR;

using NeoPrint.Application.Operations;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Wallets;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;

namespace NeoPrint.Infrastructure.Refunds;

/// <summary>
/// <c>RefundConsumer</c> (plan section 6): a refund decision becomes refunds,
/// which are carried out at once. Idempotent through the refunds' dedup keys:
/// a redelivery finds them made and only moves on what is still due.
/// </summary>
public sealed class RefundRequestedConsumer : IConsumer<RefundRequested>
{
    private readonly ISender _sender;
    private readonly IRefundRepository _refunds;
    private readonly ILogger<RefundRequestedConsumer> _logger;

    public RefundRequestedConsumer(ISender sender, IRefundRepository refunds, ILogger<RefundRequestedConsumer> logger)
    {
        _sender = sender;
        _refunds = refunds;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<RefundRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PaymentId"] = message.PaymentId,
            ["CorrelationId"] = message.CorrelationId,
        });

        await _sender.Send(new CreateRefundsCommand(message), context.CancellationToken).ConfigureAwait(false);

        // Every refund of this decision, including ones a previous delivery made
        // but did not get to carry out.
        foreach (var refund in await _refunds.ListForPaymentAsync(message.PaymentId, context.CancellationToken).ConfigureAwait(false))
        {
            if (refund.DedupKey.StartsWith(message.DedupKey + ":", StringComparison.Ordinal))
            {
                await _sender.Send(new ProcessRefundCommand(refund.Id), context.CancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// <c>AutoRefundOnKioskErrorConsumer</c> (plan section 6): scenario A only -
/// the kiosk reported the interruption, or that its printer stopped, so it is
/// known. A job whose kiosk went quiet is Suspected and never comes here.
/// </summary>
public sealed class AutoRefundOnKioskErrorConsumer :
    IConsumer<PrintJobInterrupted>,
    IConsumer<KioskIncidentRaised>
{
    private readonly ISender _sender;

    public AutoRefundOnKioskErrorConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<PrintJobInterrupted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new HandleInterruptedJobCommand(context.Message.JobId), context.CancellationToken);
    }

    /// <summary>The guests' paid orders that had not started when the printer stopped (plan section 9.6, A.4).</summary>
    public Task Consume(ConsumeContext<KioskIncidentRaised> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new HandleStoppedKioskCommand(context.Message.KioskId), context.CancellationToken);
    }
}

/// <summary>A cancelled job's open intents close and its payment goes back (plan section 2.5).</summary>
public sealed class PrintJobCancelledConsumer : IConsumer<PrintJobCancelled>
{
    private readonly ISender _sender;

    public PrintJobCancelledConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<PrintJobCancelled> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new HandleCancelledJobCommand(context.Message), context.CancellationToken);
    }
}

/// <summary>
/// The refund jobs of plan section 6: retries that are due (every minute) and
/// <c>RefundStatusPollingJob</c> (every five).
/// </summary>
public sealed class RefundJobs
{
    public const string RetryJobId = "refund-retry";

    public const string PollingJobId = "refund-status-polling";

    private const int Batch = 100;

    private readonly ISender _sender;
    private readonly IRefundRepository _refunds;
    private readonly Application.Payments.IPaymentProviders _providers;
    private readonly Application.Payments.IPaymentRepository _payments;
    private readonly IClock _clock;
    private readonly ILogger<RefundJobs> _logger;

    public RefundJobs(
        ISender sender,
        IRefundRepository refunds,
        Application.Payments.IPaymentProviders providers,
        Application.Payments.IPaymentRepository payments,
        IClock clock,
        ILogger<RefundJobs> logger)
    {
        _sender = sender;
        _refunds = refunds;
        _providers = providers;
        _payments = payments;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Refunds created but never started (a consumer that died) and provider tries that are due.</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RetryDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        foreach (var refund in await _refunds.ListDueAsync(now, now.AddMinutes(-1), Batch, cancellationToken).ConfigureAwait(false))
        {
            await RunAsync(() => _sender.Send(new ProcessRefundCommand(refund.Id), cancellationToken), refund.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Provider refunds accepted but not finished: the provider's status decides.</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        foreach (var refund in await _refunds.ListAwaitingProviderAsync(_clock.UtcNow.AddMinutes(-1), Batch, cancellationToken).ConfigureAwait(false))
        {
            var payment = await _payments.FindByIdAsync(refund.PaymentId, cancellationToken).ConfigureAwait(false);

            if (payment?.Provider is not { } name || _providers.FindRefunding(name) is not { } provider)
            {
                continue;
            }

            await RunAsync(
                    async () =>
                    {
                        var result = await provider.GetRefundStatusAsync(refund.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
                        await _sender
                            .Send(new RecordProviderRefundCommand(refund.Id, result.State.ToString(), result.RefundRef, result.FailureCode), cancellationToken)
                            .ConfigureAwait(false);
                    },
                    refund.Id)
                .ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Func<Task> work, Guid refundId)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Domain.Common.ConcurrencyException or MongoDB.Driver.MongoException or Application.Payments.PaymentProviderUnavailableException)
        {
            _logger.LogWarning(exception, "Refund {RefundId} did not move this time; the next run tries again.", refundId);
        }
    }
}

/// <summary>
/// <c>WalletLedgerReconciliationJob</c> (plan section 6, daily): every
/// wallet's balance against its ledger's sum. A difference opens an incident;
/// nothing is "fixed" automatically - which side is right is for a person to
/// find out.
/// </summary>
public sealed class WalletLedgerReconciliationJob
{
    public const string RecurringId = "wallet-ledger-reconciliation";

    private readonly ISender _sender;

    public WalletLedgerReconciliationJob(ISender sender) => _sender = sender;

    /// <summary>Finds, never fixes (T-215): <see cref="RunWalletLedgerCheckCommand"/>.</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public Task RunAsync(CancellationToken cancellationToken) => _sender.Send(new RunWalletLedgerCheckCommand(), cancellationToken);
}
