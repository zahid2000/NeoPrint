using MediatR;

using NeoPrint.Application.Releases;

namespace NeoPrint.Infrastructure.Releases;

/// <summary>
/// Every five minutes: rollouts to chosen kiosks that all of them installed
/// are completed (T-381) - a heartbeat may be what says a kiosk runs the
/// version, after its update report.
/// </summary>
/// <remarks>Safe to run more than once: a completed rollout is not active any more.</remarks>
public sealed class RolloutCompletionJob
{
    public const string RecurringId = "rollout-completion";

    private readonly ISender _sender;

    public RolloutCompletionJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new CompleteInstalledRolloutsCommand(), cancellationToken);
}
