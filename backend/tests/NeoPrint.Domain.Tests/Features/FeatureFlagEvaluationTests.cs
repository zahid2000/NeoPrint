using NeoPrint.Domain.Features;

namespace NeoPrint.Domain.Tests.Features;

/// <summary>
/// Feature flag evaluation order (plan section 2.16).
/// </summary>
public sealed class FeatureFlagEvaluationTests
{
    private static FeatureFlag Flag(
        bool defaultEnabled = true,
        int rolloutPercent = 100,
        Dictionary<string, bool>? kioskOverrides = null,
        List<Guid>? allowlist = null) => new()
    {
        Key = "layout_editor",
        DefaultEnabled = defaultEnabled,
        RolloutPercent = rolloutPercent,
        KioskOverrides = kioskOverrides ?? [],
        UserAllowlist = allowlist ?? [],
    };

    [Fact]
    public void A_flag_that_is_on_by_default_is_on()
    {
        FeatureFlagEvaluation.Evaluate(Flag(), kioskId: null, userId: null).Should().BeTrue();
    }

    [Fact]
    public void A_flag_that_is_off_by_default_is_off()
    {
        FeatureFlagEvaluation.Evaluate(Flag(defaultEnabled: false), null, null).Should().BeFalse();
    }

    [Fact]
    public void A_kiosk_override_turns_a_feature_off_for_that_kiosk_alone()
    {
        var pilotKiosk = Guid.NewGuid();
        var flag = Flag(kioskOverrides: new Dictionary<string, bool> { [pilotKiosk.ToString()] = false });

        FeatureFlagEvaluation.Evaluate(flag, pilotKiosk, null).Should().BeFalse();
        FeatureFlagEvaluation.Evaluate(flag, Guid.NewGuid(), null).Should().BeTrue();
    }

    [Fact]
    public void A_kiosk_override_can_also_turn_a_feature_on_where_the_default_is_off()
    {
        var pilotKiosk = Guid.NewGuid();
        var flag = Flag(
            defaultEnabled: false,
            kioskOverrides: new Dictionary<string, bool> { [pilotKiosk.ToString()] = true });

        FeatureFlagEvaluation.Evaluate(flag, pilotKiosk, null).Should().BeTrue();
    }

    [Fact]
    public void A_kiosk_override_beats_the_rollout_percentage()
    {
        var pilotKiosk = Guid.NewGuid();
        var flag = Flag(
            rolloutPercent: 0,
            kioskOverrides: new Dictionary<string, bool> { [pilotKiosk.ToString()] = true });

        FeatureFlagEvaluation.Evaluate(flag, pilotKiosk, Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void An_allowlisted_user_always_gets_the_feature()
    {
        var tester = Guid.NewGuid();
        var flag = Flag(rolloutPercent: 0, allowlist: [tester]);

        FeatureFlagEvaluation.Evaluate(flag, null, tester).Should().BeTrue();
        FeatureFlagEvaluation.Evaluate(flag, null, Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void A_zero_percent_rollout_is_off_for_everyone_not_allowlisted()
    {
        var flag = Flag(rolloutPercent: 0);

        FeatureFlagEvaluation.Evaluate(flag, null, Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void A_partial_rollout_without_a_user_falls_back_to_the_default()
    {
        // An anonymous kiosk session has no user to bucket, so a percentage
        // cannot be applied; the default decides.
        FeatureFlagEvaluation.Evaluate(Flag(rolloutPercent: 50), null, null).Should().BeTrue();
        FeatureFlagEvaluation.Evaluate(Flag(defaultEnabled: false, rolloutPercent: 50), null, null)
            .Should().BeFalse();
    }

    [Fact]
    public void A_user_gets_the_same_answer_every_time()
    {
        var user = Guid.NewGuid();
        var flag = Flag(rolloutPercent: 50);

        var first = FeatureFlagEvaluation.Evaluate(flag, null, user);

        for (var i = 0; i < 20; i++)
        {
            FeatureFlagEvaluation.Evaluate(flag, null, user).Should().Be(first,
                "a feature must not flicker between requests");
        }
    }

    [Fact]
    public void A_fifty_percent_rollout_reaches_roughly_half_the_users()
    {
        var flag = Flag(rolloutPercent: 50);
        var users = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToList();

        var enabled = users.Count(user => FeatureFlagEvaluation.Evaluate(flag, null, user));

        // Wide bounds: this checks the bucketing is not badly skewed, not that
        // it is a perfect hash.
        enabled.Should().BeInRange(850, 1150);
    }

    [Fact]
    public void Two_flags_at_the_same_percentage_do_not_pick_the_same_people()
    {
        var users = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();

        var first = users.Where(user => FeatureFlagEvaluation.Bucket(user, "flag_a") < 50).ToHashSet();
        var second = users.Where(user => FeatureFlagEvaluation.Bucket(user, "flag_b") < 50).ToHashSet();

        // The key is part of the hash, so the two cohorts differ.
        first.SetEquals(second).Should().BeFalse();
    }

    [Fact]
    public void Buckets_stay_within_range()
    {
        for (var i = 0; i < 500; i++)
        {
            FeatureFlagEvaluation.Bucket(Guid.NewGuid(), "any_flag").Should().BeInRange(0, 99);
        }
    }

    [Fact]
    public void Every_mvp_flag_is_declared_with_at_least_one_platform()
    {
        FeatureFlag.MvpFlags.Should().HaveCount(11);
        FeatureFlag.MvpFlags.Should().OnlyContain(flag => flag.Platforms.Length > 0);
        FeatureFlag.MvpFlags.Select(flag => flag.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_mvp_flags_the_plan_names_are_all_present()
    {
        var keys = FeatureFlag.MvpFlags.Select(flag => flag.Key).ToList();

        keys.Should().Contain(
        [
            "mobile_payment_at_kiosk", "user_qr_print", "mobile_session_print", "scheduled_print",
            "file_sharing", "referral", "promo_codes", "layout_editor", "document_scan",
            "txt_editor", "data_export",
        ]);
    }
}
