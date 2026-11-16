using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Pricing;

namespace NeoPrint.Infrastructure.Pricing;

/// <summary>
/// Every minute: a pricing campaign that started or ended since the last run
/// makes every kiosk fetch its prices again (T-436) - nothing else tells a
/// kiosk the hour came.
/// </summary>
/// <remarks>
/// The window looks back a little more than a minute, so a late run misses
/// nothing; a boundary seen twice only fetches the prices twice. After a
/// longer outage the kiosks reconnect and fetch them anyway, and the server
/// prices every order itself.
/// </remarks>
public sealed class CampaignBoundaryJob
{
    public const string RecurringId = "pricing-campaign-boundaries";

    /// <summary>How far back each run looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    private readonly ISender _sender;
    private readonly IClock _clock;

    public CampaignBoundaryJob(ISender sender, IClock clock)
    {
        _sender = sender;
        _clock = clock;
    }

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new AnnounceCampaignBoundariesCommand(_clock.UtcNow - Window), cancellationToken);
}
