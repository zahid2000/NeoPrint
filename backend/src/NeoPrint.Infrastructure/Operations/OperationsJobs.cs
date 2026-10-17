using MediatR;

using NeoPrint.Application.Operations;

namespace NeoPrint.Infrastructure.Operations;

/// <summary>
/// The system's own recurring checks (plan section 6, T-215): the file
/// cleanup, the dead-letter queues and the money check. Each one writes a run
/// record and is safe to run twice - the Hangfire panel may start any of them
/// by hand.
/// </summary>
public sealed class OperationsJobs
{
    public const string FileCleanupJobId = "file-cleanup";

    public const string DeadLetterJobId = "dead-letter-monitor";

    public const string PaymentConsistencyJobId = "payment-consistency";

    /// <summary>Hourly, at :41 - away from the analytics rollup (:23) and the supply jobs (:07, :37).</summary>
    public const string FileCleanupCron = "41 * * * *";

    public const string EveryFiveMinutes = "*/5 * * * *";

    /// <summary>Hourly, at :51.</summary>
    public const string PaymentConsistencyCron = "51 * * * *";

    private readonly ISender _sender;

    public OperationsJobs(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public Task FileCleanupAsync(CancellationToken cancellationToken) => _sender.Send(new RunFileCleanupCommand(), cancellationToken);

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task DeadLettersAsync(CancellationToken cancellationToken) => _sender.Send(new RunDeadLetterCheckCommand(), cancellationToken);

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task PaymentConsistencyAsync(CancellationToken cancellationToken) => _sender.Send(new RunPaymentConsistencyCheckCommand(), cancellationToken);
}
