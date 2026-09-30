using MassTransit;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Receipts;
using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Receipts;

/// <summary>
/// <c>ReceiptConsumer</c> (plan section 6): a paid payment gets its numbered
/// receipt and its PDF. The receipt's id is the payment's, so a redelivery
/// finds it and only renders it again.
/// </summary>
public sealed class ReceiptConsumer : IConsumer<PaymentSucceeded>
{
    private readonly ISender _sender;
    private readonly ILogger<ReceiptConsumer> _logger;

    public ReceiptConsumer(ISender sender, ILogger<ReceiptConsumer> logger)
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

        if (await _sender.Send(new CreateReceiptCommand(context.Message), context.CancellationToken).ConfigureAwait(false) is { } receiptId)
        {
            await _sender.Send(new RenderReceiptCommand(receiptId), context.CancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The fiscal receipt arrived: the payment's receipt is re-made with it and sent again (plan section 2.12).</summary>
public sealed class ReceiptFiscalConsumer : IConsumer<FiscalReceiptIssued>
{
    private readonly ISender _sender;

    public ReceiptFiscalConsumer(ISender sender) => _sender = sender;

    public async Task Consume(ConsumeContext<FiscalReceiptIssued> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A refund receipt belongs to the refund status page, not to the payment's receipt.
        if (context.Message.Type != "Refund" && context.Message.PaymentId is { } paymentId)
        {
            await _sender.Send(new ReceiptFiscalIssuedCommand(paymentId), context.CancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Receipts that missed their fiscal part (an event lost to a crash) are re-made every five minutes.</summary>
public sealed class ReceiptJobs
{
    public const string CatchUpJobId = "receipt-catch-up";

    private readonly ISender _sender;

    public ReceiptJobs(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task CatchUpAsync(CancellationToken cancellationToken) => _sender.Send(new CatchUpReceiptsCommand(), cancellationToken);
}
