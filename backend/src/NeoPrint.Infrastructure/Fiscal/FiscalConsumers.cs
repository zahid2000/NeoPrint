using MassTransit;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Fiscal;
using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Fiscal;

/// <summary>
/// <c>FiscalizationConsumer</c> (plan section 6): a payment's sale or top-up
/// receipt. Idempotent through the receipt's dedup key; the provider is asked
/// at once, and a failure waits for <see cref="FiscalJobs"/>.
/// </summary>
public sealed class PaymentFiscalizationConsumer : IConsumer<PaymentSucceeded>
{
    private readonly ISender _sender;
    private readonly ILogger<PaymentFiscalizationConsumer> _logger;

    public PaymentFiscalizationConsumer(ISender sender, ILogger<PaymentFiscalizationConsumer> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentSucceeded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PaymentId"] = context.Message.PaymentId,
            ["CorrelationId"] = context.Message.CorrelationId,
        });

        if (await _sender.Send(new FiscalizePaymentCommand(context.Message), context.CancellationToken).ConfigureAwait(false) is { } receiptId)
        {
            await _sender.Send(new IssueFiscalReceiptCommand(receiptId), context.CancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The refund receipt of money that went back (plan section 2.5: "hər refund fiskal qaytarma çeki ilə").</summary>
public sealed class RefundFiscalizationConsumer : IConsumer<RefundCompleted>
{
    private readonly ISender _sender;
    private readonly ILogger<RefundFiscalizationConsumer> _logger;

    public RefundFiscalizationConsumer(ISender sender, ILogger<RefundFiscalizationConsumer> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<RefundCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["RefundId"] = context.Message.RefundId,
            ["CorrelationId"] = context.Message.CorrelationId,
        });

        if (await _sender.Send(new FiscalizeRefundCommand(context.Message), context.CancellationToken).ConfigureAwait(false) is { } receiptId)
        {
            await _sender.Send(new IssueFiscalReceiptCommand(receiptId), context.CancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The fiscal background work (plan section 6): tries that are due, every
/// minute, and the incident for receipts older than <c>Fiscal.MaxPendingMinutes</c>.
/// </summary>
public sealed class FiscalJobs
{
    public const string RetryJobId = "fiscal-retry";

    private const int Batch = 100;

    private readonly ISender _sender;
    private readonly IFiscalReceiptRepository _receipts;
    private readonly IClock _clock;
    private readonly ILogger<FiscalJobs> _logger;

    public FiscalJobs(ISender sender, IFiscalReceiptRepository receipts, IClock clock, ILogger<FiscalJobs> logger)
    {
        _sender = sender;
        _receipts = receipts;
        _clock = clock;
        _logger = logger;
    }

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var receipt in await _receipts.ListDueAsync(_clock.UtcNow, Batch, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _sender.Send(new IssueFiscalReceiptCommand(receipt.Id), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One receipt that cannot be tried does not stop the others.
                _logger.LogError(exception, "Fiscal receipt {ReceiptId} could not be tried.", receipt.Id);
            }
        }

        await _sender.Send(new ReportLateFiscalReceiptsCommand(), cancellationToken).ConfigureAwait(false);
    }
}
