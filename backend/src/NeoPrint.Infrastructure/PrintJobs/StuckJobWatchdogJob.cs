using MediatR;

using NeoPrint.Application.PrintJobs;

namespace NeoPrint.Infrastructure.PrintJobs;

/// <summary>
/// <c>StuckJobWatchdogJob</c> (plan section 6, every minute): jobs whose kiosk
/// went quiet become Suspected; nothing is cancelled or refunded
/// (plan section 2.5, scenario B).
/// </summary>
public sealed class StuckJobWatchdogJob
{
    public const string JobId = "stuck-job-watchdog";

    private readonly ISender _sender;

    public StuckJobWatchdogJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunAsync(CancellationToken cancellationToken) => _sender.Send(new WatchStuckJobsCommand(), cancellationToken);
}
