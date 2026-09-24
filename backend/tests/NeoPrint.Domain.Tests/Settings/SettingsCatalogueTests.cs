using NeoPrint.Domain.Settings;

namespace NeoPrint.Domain.Tests.Settings;

/// <summary>
/// The settings catalogue against plan section 2.4.
/// </summary>
/// <remarks>
/// Everything in the plan's table has to be in the catalogue, because the
/// catalogue is what the seeder writes and what the panel edits. A setting the
/// code reads but nobody seeded falls back to a default written at the call
/// site, which is the drift these tests exist to catch.
/// </remarks>
public sealed class SettingsCatalogueTests
{
    [Fact]
    public void Every_key_appears_once()
    {
        var keys = SettingKeys.Defaults.Select(setting => setting.Key).ToList();

        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_setting_says_what_it_is_for()
    {
        // A default may legitimately be empty - a fiscal terminal id has no
        // sensible value until somebody installs the terminal - but a setting
        // nobody can explain has no business being editable from the panel.
        foreach (var setting in SettingKeys.Defaults)
        {
            setting.Description.Should().NotBeNullOrWhiteSpace(setting.Key);
            setting.DefaultValue.Should().NotBeNull(setting.Key);
        }
    }

    [Theory]
    // Added with the offline behaviour the plan describes for a kiosk whose
    // backend is unreachable but whose cache is still fresh (section 7.3).
    [InlineData(SettingKeys.KioskConfigCacheTtlHours, "24")]
    // Added for the late provider callback the payment flows describe
    // (docs/PAYMENT_FLOWS.md).
    [InlineData(SettingKeys.PaymentReconcileAfterMinutes, "10")]
    public void A_setting_the_plan_lists_is_in_the_catalogue(string key, string expectedDefault)
    {
        var setting = SettingKeys.Defaults.SingleOrDefault(candidate => candidate.Key == key);

        setting.Should().NotBeNull($"plan section 2.4 lists {key}");
        setting!.DefaultValue.Should().Be(expectedDefault);
        setting.ValueType.Should().Be(SettingValueType.Integer);
    }

    [Fact]
    public void An_integer_setting_holds_an_integer()
    {
        foreach (var setting in SettingKeys.Defaults
                     .Where(candidate => candidate.ValueType == SettingValueType.Integer))
        {
            int.TryParse(setting.DefaultValue, out _)
                .Should().BeTrue($"{setting.Key} is declared as an integer");
        }
    }

    [Fact]
    public void A_boolean_setting_holds_a_boolean()
    {
        foreach (var setting in SettingKeys.Defaults
                     .Where(candidate => candidate.ValueType == SettingValueType.Boolean))
        {
            bool.TryParse(setting.DefaultValue, out _)
                .Should().BeTrue($"{setting.Key} is declared as a boolean");
        }
    }
}
