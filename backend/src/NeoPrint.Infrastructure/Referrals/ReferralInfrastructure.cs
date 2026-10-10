using MassTransit;

using MediatR;

using MongoDB.Driver;

using NeoPrint.Application.Referrals;
using NeoPrint.Contracts;
using NeoPrint.Domain.Referrals;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Referrals;

/// <inheritdoc cref="IReferralRepository" />
public sealed class ReferralRepository : IReferralRepository
{
    private readonly MongoContext _context;

    public ReferralRepository(MongoContext context) => _context = context;

    public Task AddAsync(Referral referral, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Referrals, referral, cancellationToken);

    public Task<Referral?> FindByInviteeAsync(Guid inviteeId, CancellationToken cancellationToken = default)
        => _context.Referrals.Find(r => r.InviteeId == inviteeId).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(Referral referral, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Referrals, referral, cancellationToken);

    public Task<long> CountRewardedSinceAsync(Guid inviterId, DateTime since, CancellationToken cancellationToken = default)
        => _context.Referrals.CountDocumentsAsync(
            r => r.InviterId == inviterId && r.Status == ReferralStatus.Rewarded && r.RewardedAt >= since,
            cancellationToken: cancellationToken);

    public async Task<ReferralTotals> TotalsAsync(Guid inviterId, CancellationToken cancellationToken = default)
    {
        var all = await _context.Referrals
            .Find(r => r.InviterId == inviterId)
            .Project(r => new { r.Status, r.QualifiedAt, r.InviterBonusPages })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ReferralTotals(
            all.Count,
            all.Count(r => r.Status == ReferralStatus.Rewarded),
            all.Count(r => r.QualifiedAt == null),
            all.Where(r => r.Status == ReferralStatus.Rewarded).Sum(r => (long)r.InviterBonusPages));
    }

    public async Task<bool> AnyRewardedInviteeOnDevicesAsync(
        Guid inviteeId, IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken = default)
    {
        if (deviceIds.Count == 0)
        {
            return false;
        }

        var others = await _context.Users
            .Find(Builders<Domain.Users.User>.Filter.AnyIn(u => u.DeviceIds, deviceIds)
                & Builders<Domain.Users.User>.Filter.Ne(u => u.Id, inviteeId)
                & Builders<Domain.Users.User>.Filter.Ne(u => u.ReferredBy, null))
            .Project(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return others.Count > 0
            && await _context.Referrals
                .Find(r => others.Contains(r.InviteeId) && r.Status == ReferralStatus.Rewarded)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);
    }
}

/// <summary>
/// An order printed (plan section 6, <c>ReferralRewardConsumer</c>): when it is
/// an invitee's first paid print, the invitation is rewarded.
/// </summary>
/// <remarks>A redelivered message finds the invitation decided; the ledger's dedup keys credit once.</remarks>
public sealed class ReferralRewardConsumer : IConsumer<PrintJobCompleted>
{
    private readonly ISender _sender;

    public ReferralRewardConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<PrintJobCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new RewardReferralCommand(context.Message.JobId), context.CancellationToken);
    }
}

/// <summary>A reward was written: the two users hear it (plan section 8.4, <c>ReferralRewarded</c>).</summary>
public sealed class ReferralNotificationConsumer : IConsumer<ReferralQualified>
{
    private readonly ISender _sender;

    public ReferralNotificationConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<ReferralQualified> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        return _sender.Send(
            new NotifyReferralRewardedCommand(
                message.ReferralId, message.InviterId, message.InviteeId, message.InviterBonusPages, message.InviteeBonusPages),
            context.CancellationToken);
    }
}
