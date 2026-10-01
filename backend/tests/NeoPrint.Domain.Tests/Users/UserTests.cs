using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Users;

namespace NeoPrint.Domain.Tests.Users;

public sealed class UserTests
{
    [Fact]
    public void Public_ids_are_shaped_so_they_can_be_shared_by_hand()
    {
        for (var i = 0; i < 200; i++)
        {
            var publicId = User.NewPublicId();

            publicId.Should().MatchRegex("^NP-[A-Z2-9]{6}$");
            publicId.Should().NotContainAny("0", "O", "1", "I", "L");
        }
    }

    [Fact]
    public void Referral_codes_are_shaped_as_the_plan_describes()
    {
        User.NewReferralCode().Should().MatchRegex("^NP[A-Z2-9]{6}$");
    }

    [Fact]
    public void Generated_identifiers_do_not_repeat_in_practice()
    {
        Enumerable.Range(0, 500).Select(_ => User.NewPublicId()).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("User@Example.COM", "user@example.com")]
    [InlineData("  spaced@example.com  ", "spaced@example.com")]
    public void Emails_are_normalized_so_one_address_cannot_register_twice(string input, string expected)
    {
        User.NormalizeEmail(input).Should().Be(expected);
    }

    [Fact]
    public void Only_an_active_account_may_sign_in()
    {
        new User { Status = UserStatus.Active }.CanSignIn.Should().BeTrue();
        new User { Status = UserStatus.Blocked }.CanSignIn.Should().BeFalse();
        new User { Status = UserStatus.Deleted }.CanSignIn.Should().BeFalse();
    }

    [Fact]
    public void Confirmation_codes_are_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            EmailConfirmation.NewCode().Should().MatchRegex("^[0-9]{6}$");
        }
    }

    [Fact]
    public void A_refresh_token_is_active_only_while_unrevoked_and_unexpired()
    {
        var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        new RefreshToken { ExpiresAt = now.AddDays(30) }.IsActive(now).Should().BeTrue();
        new RefreshToken { ExpiresAt = now.AddDays(-1) }.IsActive(now).Should().BeFalse();
        new RefreshToken { ExpiresAt = now.AddDays(30), RevokedAt = now }.IsActive(now).Should().BeFalse();
    }
}

public sealed class SettingDefaultsTests
{
    [Fact]
    public void Every_setting_key_appears_once()
    {
        SettingKeys.Defaults.Select(setting => setting.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_defaults_match_the_specification()
    {
        // Spot checks against the table in plan section 2.4.
        Default(SettingKeys.QueueDisplayLimit).Should().Be("5");
        Default(SettingKeys.PaymentTimeoutMinutes).Should().Be("5");
        Default(SettingKeys.FilesDefaultMaxSizeMb).Should().Be("50");
        Default(SettingKeys.FilesDefaultMaxPages).Should().Be("200");
        Default(SettingKeys.KioskOfflineAfterSeconds).Should().Be("60");
        Default(SettingKeys.ReferralBonusPages).Should().Be("10");
        Default(SettingKeys.EnrollmentTokenTtlHours).Should().Be("72");
        Default(SettingKeys.KioskUpdateWindow).Should().Be("02:00-05:00");
        Default(SettingKeys.StorageReconciliationCron).Should().Be("0 3 * * *");
    }

    [Fact]
    public void Numeric_settings_declare_a_numeric_type()
    {
        foreach (var setting in SettingKeys.Defaults.Where(s => s.ValueType == SettingValueType.Integer))
        {
            int.TryParse(setting.DefaultValue, out _).Should().BeTrue(
                $"{setting.Key} is declared as an integer but defaults to '{setting.DefaultValue}'");
        }
    }

    [Fact]
    public void Boolean_settings_declare_a_boolean_default()
    {
        foreach (var setting in SettingKeys.Defaults.Where(s => s.ValueType == SettingValueType.Boolean))
        {
            bool.TryParse(setting.DefaultValue, out _).Should().BeTrue(
                $"{setting.Key} is declared as a boolean but defaults to '{setting.DefaultValue}'");
        }
    }

    [Fact]
    public void Every_setting_is_described_so_the_admin_panel_can_explain_it()
    {
        SettingKeys.Defaults.Should().OnlyContain(setting => setting.Description.Length > 0);
    }

    [Fact]
    public void The_default_format_list_covers_every_mvp_group()
    {
        var formats = SettingKeys.DefaultAllowedFormats.Split(',');

        formats.Should().Contain(["pdf", "docx", "odt", "txt", "html"]);    // text documents
        formats.Should().Contain(["xlsx", "ods", "csv"]);                     // spreadsheets
        formats.Should().Contain(["pptx", "odp"]);                            // presentations
        formats.Should().Contain(["jpg", "png", "bmp", "gif", "webp"]);       // images
        formats.Should().Contain(["ps", "eps", "pcl", "prn", "xps", "oxps"]); // printer languages
        // Deferred to step 9 (plan section 2.2).
        formats.Should().NotContain(["tiff", "svg", "heic"]);
    }

    private static string Default(string key)
        => SettingKeys.Defaults.Single(setting => setting.Key == key).DefaultValue;

    [Fact]
    public void Blocking_stops_sign_in_once_and_unblocking_gives_it_back()
    {
        var user = new User();
        var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

        user.Block(now).Should().BeTrue();
        user.CanSignIn.Should().BeFalse();
        user.BlockedAt.Should().Be(now);
        user.Block(now.AddMinutes(1)).Should().BeFalse("blocking twice changes nothing");
        user.BlockedAt.Should().Be(now);

        user.Unblock().Should().BeTrue();
        user.CanSignIn.Should().BeTrue();
        user.BlockedAt.Should().BeNull();
        user.Unblock().Should().BeFalse();
    }

    [Fact]
    public void A_deleted_account_cannot_be_blocked()
    {
        var user = new User { Status = UserStatus.Deleted };

        user.Invoking(u => u.Block(DateTime.UtcNow)).Should().Throw<NeoPrint.Domain.Common.DomainException>();
        user.Unblock().Should().BeFalse();
    }
}
