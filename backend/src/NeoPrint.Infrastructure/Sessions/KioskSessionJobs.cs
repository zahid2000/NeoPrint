using MediatR;

using NeoPrint.Application.Sessions;

namespace NeoPrint.Infrastructure.Sessions;

/// <summary>
/// Every minute: sessions whose time ran out are announced to their kiosk and
/// their app (plan section 8.2, "sessiya TTL bitəndə bildiriş").
/// </summary>
/// <remarks>
/// Safe to run anywhere and more than once at a time: each expired session is
/// handed to one run only (<see cref="IKioskSessionStore.TakeExpiredAsync"/>).
/// </remarks>
public sealed class KioskSessionJobs
{
    public const string ExpiryJobId = "kiosk-session-expiry";

    private readonly ISender _sender;

    public KioskSessionJobs(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new ExpireKioskSessionsCommand(), cancellationToken);
}
