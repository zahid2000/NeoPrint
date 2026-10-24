using MediatR;

using NeoPrint.Application.Kiosks;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary>
/// Once a day: kiosk logs past <c>Logs.RetentionDays</c> are deleted, file and
/// record (docs/KIOSK_UPDATES.md section 6, T-321). Safe to run again.
/// </summary>
public sealed class KioskLogJobs
{
    public const string PurgeJobId = "kiosk-logs-purge";

    /// <summary>04:30 UTC: after the night's kiosk updates.</summary>
    public const string Daily = "30 4 * * *";

    private readonly ISender _sender;

    public KioskLogJobs(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task PurgeAsync(CancellationToken cancellationToken)
        => _sender.Send(new PurgeExpiredKioskLogsCommand(), cancellationToken);
}
