using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Notifications;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.UserQr;
using NeoPrint.Application.Wallets;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Referrals;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Users;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Application.Referrals;

/// <summary>Persistence for <see cref="Referral"/> (collection <c>referrals</c>).</summary>
public interface IReferralRepository
{
    Task AddAsync(Referral referral, CancellationToken cancellationToken = default);

    Task<Referral?> FindByInviteeAsync(Guid inviteeId, CancellationToken cancellationToken = default);

    /// <exception cref="ConcurrencyException">Somebody else decided it first.</exception>
    Task UpdateAsync(Referral referral, CancellationToken cancellationToken = default);

    /// <summary>The inviter's rewards since <paramref name="since"/>: the monthly limit.</summary>
    Task<long> CountRewardedSinceAsync(Guid inviterId, DateTime since, CancellationToken cancellationToken = default);

    /// <summary>The inviter's invitations: registered, rewarded, and the bonus pages earned.</summary>
    Task<ReferralTotals> TotalsAsync(Guid inviterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether another invitee, already rewarded, used any of these devices
    /// (plan section 2.6: "eyni cihazdan təkrar referral").
    /// </summary>
    Task<bool> AnyRewardedInviteeOnDevicesAsync(
        Guid inviteeId, IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken = default);
}

/// <param name="Registered">Everyone who registered with the code, rewarded or not.</param>
/// <param name="Waiting">Registered and not decided yet: their first paid print is still to come.</param>
public sealed record ReferralTotals(long Registered, long Rewarded, long Waiting, long BonusPagesEarned);

/// <summary>
/// The invitee's first paid print completed (plan section 6,
/// <c>ReferralRewardConsumer</c> on <c>PrintJobCompleted</c>): the invitation
/// is decided, and when nothing holds it back both bonuses are written.
/// </summary>
public sealed record RewardReferralCommand(Guid JobId) : IRequest, ITransactionalRequest;

/// <summary>Tells the inviter, and the invitee when they got pages too (plan section 8.4, <c>ReferralRewarded</c>).</summary>
public sealed record NotifyReferralRewardedCommand(
    Guid ReferralId, Guid InviterId, Guid InviteeId, int InviterBonusPages, int InviteeBonusPages) : IRequest;

/// <summary><c>GET referrals/me</c> (plan section 6): the code, the link and the numbers.</summary>
public sealed record GetMyReferralQuery(Guid UserId) : IRequest<MyReferral>;

/// <summary><c>GET referrals/me/qr.png</c>: the link as a QR code.</summary>
public sealed record GetMyReferralQrQuery(Guid UserId) : IRequest<byte[]>;

/// <param name="BonusPagesPerInvite">What the user gets for an invitation now (<c>Referral.BonusPages</c>).</param>
/// <param name="InviteeBonusPages">What the invited friend gets (<c>Referral.InviteeBonusPages</c>).</param>
public sealed record MyReferral(
    string Code,
    string Link,
    long Invited,
    long Rewarded,
    long Waiting,
    long BonusPagesEarned,
    int BonusPagesPerInvite,
    int InviteeBonusPages);

/// <summary>Referral rewards and the user's own numbers (plan section 2.6; docs/DECISIONS.md 2026-10-05).</summary>
public sealed class ReferralHandlers :
    IRequestHandler<RewardReferralCommand>,
    IRequestHandler<NotifyReferralRewardedCommand>,
    IRequestHandler<GetMyReferralQuery, MyReferral>,
    IRequestHandler<GetMyReferralQrQuery, byte[]>
{
    /// <summary>The link a referral code is shared as (plan section 2.9).</summary>
    public const string LinkPrefix = "https://neoprint.az/r/";

    private readonly IReferralRepository _referrals;
    private readonly IPrintJobRepository _jobs;
    private readonly IPaymentRepository _payments;
    private readonly IUserRepository _users;
    private readonly WalletLedger _ledger;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IQrImages _qr;
    private readonly ISender _sender;
    private readonly IClock _clock;
    private readonly ILogger<ReferralHandlers> _logger;

    public ReferralHandlers(
        IReferralRepository referrals,
        IPrintJobRepository jobs,
        IPaymentRepository payments,
        IUserRepository users,
        WalletLedger ledger,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        IQrImages qr,
        ISender sender,
        IClock clock,
        ILogger<ReferralHandlers> logger)
    {
        _referrals = referrals;
        _jobs = jobs;
        _payments = payments;
        _users = users;
        _ledger = ledger;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _qr = qr;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    public async Task Handle(RewardReferralCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (job is not { UserId: { } inviteeId, Status: PrintJobStatus.Completed })
        {
            return;
        }

        // Decided once, by the first paid print: a later print, or this one
        // again, finds it decided and leaves it.
        var referral = await _referrals.FindByInviteeAsync(inviteeId, cancellationToken).ConfigureAwait(false);
        if (referral is null || referral.IsDecided)
        {
            return;
        }

        // Paid with money - balance, card or bank: only bonus pages or a full
        // promo code is not a paid print (plan section 2.6).
        var money = (await _payments.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false))
            .Where(payment => payment.WasPaid)
            .Aggregate(Money.Zero, (sum, payment) => sum + (payment.Parts?.Money ?? payment.Amount));
        if (!money.IsPositive)
        {
            return;
        }

        var now = _clock.UtcNow;
        var hold = await HoldAsync(referral, now, cancellationToken).ConfigureAwait(false);

        if (hold != ReferralHold.None)
        {
            referral.Withhold(job.Id, hold, now);
            await _referrals.UpdateAsync(referral, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Referral {ReferralId} qualified by job {JobId} but not rewarded: {Hold}.", referral.Id, job.Id, hold);
            return;
        }

        var inviterBonus = Math.Max(0, await _settings
            .GetIntAsync(SettingKeys.ReferralBonusPages, 10, cancellationToken).ConfigureAwait(false));
        var inviteeBonus = Math.Max(0, await _settings
            .GetIntAsync(SettingKeys.ReferralInviteeBonusPages, 0, cancellationToken).ConfigureAwait(false));

        referral.Reward(job.Id, inviterBonus, inviteeBonus, ActorContext.System("referral"), now);
        await _referrals.UpdateAsync(referral, cancellationToken).ConfigureAwait(false);

        // One ledger line each, keyed by the invitation: a redelivered event
        // or a retried transaction credits once (docs/IDEMPOTENCY.md).
        await CreditAsync(referral.InviterId, inviterBonus, referral, "inviter", now, cancellationToken).ConfigureAwait(false);
        await CreditAsync(referral.InviteeId, inviteeBonus, referral, "invitee", now, cancellationToken).ConfigureAwait(false);

        await _events
            .PublishAsync(
                new ReferralQualified(
                    referral.Id, referral.InviterId, referral.InviteeId, inviterBonus, inviteeBonus, _correlation.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Referral {ReferralId} rewarded by job {JobId}: {InviterBonus} bonus page(s) to the inviter, {InviteeBonus} to the invitee.",
            referral.Id,
            job.Id,
            inviterBonus,
            inviteeBonus);
    }

    /// <summary>
    /// What holds a reward back: an inviter no longer active, a shared device
    /// (plan section 2.6), or the inviter's monthly limit (docs/DECISIONS.md 2026-10-05).
    /// </summary>
    private async Task<ReferralHold> HoldAsync(Referral referral, DateTime now, CancellationToken cancellationToken)
    {
        var inviter = await _users.FindByIdAsync(referral.InviterId, cancellationToken).ConfigureAwait(false);
        if (inviter is not { Status: UserStatus.Active })
        {
            return ReferralHold.InviterInactive;
        }

        var invitee = await _users.FindByIdAsync(referral.InviteeId, cancellationToken).ConfigureAwait(false);
        var devices = invitee?.DeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];

        if (devices.Intersect(inviter.DeviceIds, StringComparer.Ordinal).Any()
            || await _referrals.AnyRewardedInviteeOnDevicesAsync(referral.InviteeId, devices, cancellationToken).ConfigureAwait(false))
        {
            return ReferralHold.SameDevice;
        }

        var limit = await _settings
            .GetIntAsync(SettingKeys.ReferralMaxRewardsPerMonth, 20, cancellationToken).ConfigureAwait(false);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return limit > 0
            && await _referrals.CountRewardedSinceAsync(referral.InviterId, monthStart, cancellationToken).ConfigureAwait(false) >= limit
                ? ReferralHold.MonthlyLimit
                : ReferralHold.None;
    }

    private async Task CreditAsync(
        Guid userId, int pages, Referral referral, string side, DateTime now, CancellationToken cancellationToken)
    {
        if (pages <= 0)
        {
            return;
        }

        await _ledger
            .PostAsync(
                userId,
                WalletTransactionType.Bonus,
                Money.Zero,
                pages,
                referral.Id.ToString(),
                $"referral:{referral.Id:N}:{side}",
                actorId: null,
                now,
                $"referral:{side}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task Handle(NotifyReferralRewardedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InviterBonusPages > 0)
        {
            await _sender
                .Send(
                    new NotifyUserCommand(
                        request.InviterId,
                        NotificationType.ReferralRewarded,
                        UserMessage.Of("notifications.referralRewarded.title"),
                        UserMessage.Of("notifications.referralRewarded.body", ("pages", request.InviterBonusPages)),
                        NotificationLinks.Referral,
                        $"ReferralRewarded:{request.ReferralId}:inviter"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.InviteeBonusPages > 0)
        {
            await _sender
                .Send(
                    new NotifyUserCommand(
                        request.InviteeId,
                        NotificationType.ReferralRewarded,
                        UserMessage.Of("notifications.referralWelcome.title"),
                        UserMessage.Of("notifications.referralWelcome.body", ("pages", request.InviteeBonusPages)),
                        NotificationLinks.Wallet,
                        $"ReferralRewarded:{request.ReferralId}:invitee"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<MyReferral> Handle(GetMyReferralQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("user");
        var totals = await _referrals.TotalsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return new MyReferral(
            user.ReferralCode,
            LinkPrefix + user.ReferralCode,
            totals.Registered,
            totals.Rewarded,
            totals.Waiting,
            totals.BonusPagesEarned,
            Math.Max(0, await _settings.GetIntAsync(SettingKeys.ReferralBonusPages, 10, cancellationToken).ConfigureAwait(false)),
            Math.Max(0, await _settings.GetIntAsync(SettingKeys.ReferralInviteeBonusPages, 0, cancellationToken).ConfigureAwait(false)));
    }

    public async Task<byte[]> Handle(GetMyReferralQrQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("user");

        return _qr.Png(LinkPrefix + user.ReferralCode);
    }
}
