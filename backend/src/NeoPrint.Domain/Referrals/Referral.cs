using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Referrals;

/// <summary>Where an invitation is (plan sections 2.6, 5; docs/STATE_MACHINES.md, "Referral").</summary>
public enum ReferralStatus
{
    /// <summary>The invitee registered with the inviter's code.</summary>
    Registered = 0,

    /// <summary>The invitee's first paid print completed and both bonuses were written.</summary>
    Rewarded = 1,
}

/// <summary>Why a qualified invitation was not rewarded; it stays <see cref="ReferralStatus.Registered"/>.</summary>
public enum ReferralHold
{
    None = 0,

    /// <summary>The invitee's device is the inviter's, or another rewarded invitee's (plan section 2.6).</summary>
    SameDevice = 1,

    /// <summary>The inviter already had <c>Referral.MaxRewardsPerMonth</c> rewards that month.</summary>
    MonthlyLimit = 2,

    /// <summary>The inviter's account is blocked or deleted.</summary>
    InviterInactive = 3,
}

/// <summary>
/// One invitation (plan section 2.6; collection <c>referrals</c>): made when
/// the invitee registers with a code, decided once - at the invitee's first
/// paid print - and never undone, a refund included (docs/DECISIONS.md
/// 2026-10-05, referral rules).
/// </summary>
public sealed class Referral : Entity
{
    /// <summary>The transitions it may make (docs/STATE_MACHINES.md, "Referral").</summary>
    public static readonly StateMachine<ReferralStatus> Transitions =
        new StateMachine<ReferralStatus>(nameof(Referral))
            .Allow(ReferralStatus.Registered, ReferralStatus.Rewarded);

    public Guid InviterId { get; set; }

    public Guid InviteeId { get; set; }

    public ReferralStatus Status { get; private set; } = ReferralStatus.Registered;

    /// <summary>When the invitee's first paid print completed: the moment it was decided.</summary>
    public DateTime? QualifiedAt { get; private set; }

    /// <summary>The job that decided it.</summary>
    public Guid? QualifyingJobId { get; private set; }

    /// <summary>Why it was decided without a reward; <see cref="ReferralHold.None"/> otherwise.</summary>
    public ReferralHold Hold { get; private set; }

    public DateTime? RewardedAt { get; private set; }

    public int InviterBonusPages { get; private set; }

    public int InviteeBonusPages { get; private set; }

    /// <summary>Whether the first paid print already decided it, either way.</summary>
    public bool IsDecided => QualifiedAt is not null;

    /// <exception cref="DomainException">Someone invites themselves.</exception>
    public static Referral Register(Guid inviterId, Guid inviteeId, DateTime now)
    {
        if (inviterId == inviteeId)
        {
            throw new DomainException("referrals.self");
        }

        return new Referral { InviterId = inviterId, InviteeId = inviteeId, CreatedAt = now };
    }

    /// <summary>The first paid print came, but no reward: <paramref name="hold"/> says why. A repeat is a no-op.</summary>
    public void Withhold(Guid jobId, ReferralHold hold, DateTime now)
    {
        if (hold == ReferralHold.None)
        {
            throw new ArgumentException("A withheld reward has a reason.", nameof(hold));
        }

        if (IsDecided)
        {
            return;
        }

        QualifiedAt = now;
        QualifyingJobId = jobId;
        Hold = hold;
    }

    /// <summary>The first paid print came and both bonuses go now. A repeat is a no-op.</summary>
    public void Reward(Guid jobId, int inviterBonusPages, int inviteeBonusPages, ActorContext actor, DateTime now)
    {
        if (IsDecided)
        {
            return;
        }

        Record(Transitions.Transition(Id, Status, ReferralStatus.Rewarded, actor, now));

        Status = ReferralStatus.Rewarded;
        QualifiedAt = now;
        QualifyingJobId = jobId;
        RewardedAt = now;
        InviterBonusPages = Math.Max(0, inviterBonusPages);
        InviteeBonusPages = Math.Max(0, inviteeBonusPages);
    }
}
