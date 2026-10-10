using NeoPrint.Domain.Common;
using NeoPrint.Domain.Referrals;

namespace NeoPrint.Domain.Tests.Referrals;

/// <summary>An invitation is decided once and never undone (plan section 2.6; docs/STATE_MACHINES.md, "Referral").</summary>
public sealed class ReferralTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext System = ActorContext.System("test");

    [Fact]
    public void Nobody_invites_themselves()
    {
        var id = Guid.NewGuid();

        FluentActions.Invoking(() => Referral.Register(id, id, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("referrals.self");
    }

    [Fact]
    public void A_reward_is_written_once_with_the_bonuses_of_that_moment()
    {
        var referral = Referral.Register(Guid.NewGuid(), Guid.NewGuid(), Now);
        var job = Guid.NewGuid();

        referral.Reward(job, 10, 2, System, Now);
        referral.Reward(Guid.NewGuid(), 99, 99, System, Now.AddDays(1));
        referral.Withhold(Guid.NewGuid(), ReferralHold.SameDevice, Now.AddDays(2));

        (referral.Status, referral.QualifyingJobId, referral.InviterBonusPages, referral.InviteeBonusPages, referral.RewardedAt)
            .Should().Be((ReferralStatus.Rewarded, job, 10, 2, Now));
        referral.Hold.Should().Be(ReferralHold.None);
        referral.PendingTransitions.Should().ContainSingle();
    }

    [Fact]
    public void A_withheld_reward_stays_registered_and_decided()
    {
        var referral = Referral.Register(Guid.NewGuid(), Guid.NewGuid(), Now);

        referral.Withhold(Guid.NewGuid(), ReferralHold.MonthlyLimit, Now);
        referral.Reward(Guid.NewGuid(), 10, 0, System, Now);

        (referral.Status, referral.Hold, referral.IsDecided).Should().Be((ReferralStatus.Registered, ReferralHold.MonthlyLimit, true));
        referral.RewardedAt.Should().BeNull();
        FluentActions.Invoking(() => referral.Withhold(Guid.NewGuid(), ReferralHold.None, Now)).Should().Throw<ArgumentException>();
    }
}
