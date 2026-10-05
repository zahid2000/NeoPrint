using MediatR;

using MongoDB.Driver;

using NeoPrint.Application.Privacy;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Schedules;
using NeoPrint.Domain.Support;
using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Privacy;

/// <inheritdoc cref="IPrivacyRecords" />
/// <remarks>
/// The one place that writes to other modules' collections, and only to empty
/// personal fields of a deleted user's records (<c>docs/DECISIONS.md</c> T-147);
/// each update is a plain field set, safe to repeat.
/// </remarks>
public sealed class PrivacyRecords : IPrivacyRecords
{
    /// <summary>What a support claim's own words become.</summary>
    public const string Erased = "—";

    private static readonly PrintJobStatus[] Printing = [PrintJobStatus.Queued, PrintJobStatus.Printing];
    private static readonly ScheduledPrintStatus[] Waiting = [ScheduledPrintStatus.Scheduled, ScheduledPrintStatus.AwaitingUser];
    private static readonly RefundStatus[] OnItsWay = [RefundStatus.Pending, RefundStatus.Processing];

    private readonly MongoContext _context;

    public PrivacyRecords(MongoContext context) => _context = context;

    public async Task<DeletionBlockers> BlockersAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var printing = await _context.PrintJobs
            .CountDocumentsAsync(j => j.UserId == userId && Printing.Contains(j.Status), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var scheduled = await _context.ScheduledPrints
            .CountDocumentsAsync(s => s.UserId == userId && Waiting.Contains(s.Status), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var uncollected = await _context.PrintJobs
            .CountDocumentsAsync(
                j => j.UserId == userId && j.HeldForUser && j.Status == PrintJobStatus.Paid && j.Source != PrintJobSource.Scheduled,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var refunds = await _context.Refunds
            .CountDocumentsAsync(r => r.UserId == userId && OnItsWay.Contains(r.Status), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new DeletionBlockers((int)printing, (int)scheduled, (int)uncollected, (int)refunds);
    }

    public async Task ScrubAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Receipts stay (plan section 2.13); the addresses they were emailed to go.
        await _context.Receipts
            .UpdateManyAsync(
                r => r.UserId == userId,
                Builders<Domain.Receipts.Receipt>.Update.Set(r => r.EmailedTo, []),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Which version was accepted and when stays as proof; where from goes.
        await _context.Consents
            .UpdateManyAsync(
                c => c.UserId == userId,
                Builders<Consent>.Update.Set(c => c.Ip, null).Set(c => c.DeviceId, null),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // The claim, its money and its decision stay; the words, both ways, go.
        await _context.SupportClaims
            .UpdateManyAsync(
                c => c.UserId == userId,
                Builders<SupportClaim>.Update.Set(c => c.Text, Erased).Set(c => c.Contact, null),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _context.SupportClaims
            .UpdateManyAsync(
                Builders<SupportClaim>.Filter.Eq(c => c.UserId, userId) & Builders<SupportClaim>.Filter.Ne(c => c.Reply, null),
                Builders<SupportClaim>.Update.Set(c => c.Reply, Erased),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await _context.Notifications.DeleteManyAsync(n => n.UserId == userId, cancellationToken).ConfigureAwait(false);

        // Shares both ways: the files they point at are erased with the
        // account, and a copy someone saved is theirs (docs/DATA_RETENTION.md).
        await _context.Shares
            .DeleteManyAsync(s => s.FromUserId == userId || s.ToUserId == userId, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Every ten minutes (plan section 6, <c>AccountDeletionJob</c>): deleted
/// accounts past <c>Privacy.AccountDeletionGraceHours</c> are erased.
/// </summary>
/// <remarks>Safe to run twice: an erased account is no longer listed, and every step is repeatable.</remarks>
public sealed class AccountDeletionJob
{
    public const string RecurringId = "account-deletion";

    private readonly ISender _sender;

    public AccountDeletionJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new EraseDeletedAccountsCommand(), cancellationToken);
}
