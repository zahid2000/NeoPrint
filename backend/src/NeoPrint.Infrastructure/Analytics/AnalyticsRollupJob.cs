using MediatR;

using NeoPrint.Application.Analytics;

namespace NeoPrint.Infrastructure.Analytics;

/// <summary>
/// <c>AnalyticsRollupJob</c> (plan section 6, hourly): the pilot report's
/// days, worked out again from the records. Running it twice writes the same
/// documents - nothing is added to - so a retry or an overlap is harmless.
/// </summary>
public sealed class AnalyticsRollupJob
{
    public const string JobId = "analytics-rollup";

    /// <summary>Not on the hour: the supply forecast is at :07 and the low stock at :37.</summary>
    public const string Hourly = "23 * * * *";

    private readonly ISender _sender;

    public AnalyticsRollupJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task RunAsync(CancellationToken cancellationToken) => _sender.Send(new RunAnalyticsRollupCommand(), cancellationToken);
}
