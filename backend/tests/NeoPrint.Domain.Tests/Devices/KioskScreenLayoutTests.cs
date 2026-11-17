using FluentAssertions;

using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>The welcome screen's arrangement (T-441): arranged, never broken.</summary>
public sealed class KioskScreenLayoutTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ways_in_reordered_features_hidden_and_the_picture_moved_are_kept()
    {
        var branding = new KioskBranding { Id = KioskBranding.DefaultId };
        var layout = new KioskScreenLayout
        {
            Cards = ["code", "usb", "phone"],
            Features = ["featureFast", "featureSizes"],
            ImageLeft = true,
        };

        branding.SetLayout(layout, Now).Should().BeEmpty();

        branding.Layout.Should().BeSameAs(layout);
        branding.UpdatedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("usb,phone")]
    [InlineData("usb,phone,phone")]
    [InlineData("usb,phone,code,code")]
    [InlineData("usb,phone,camera")]
    public void A_way_in_cannot_be_dropped_doubled_or_made_up(string cards)
    {
        var branding = new KioskBranding { Id = KioskBranding.DefaultId };
        branding.SetLayout(KioskScreenLayout.Standard(), Now);

        var wrong = branding.SetLayout(new KioskScreenLayout { Cards = [.. cards.Split(',')] }, Now.AddMinutes(1));

        wrong.Should().Equal("kiosks.layoutCards");
        branding.Layout!.Cards.Should().Equal("usb", "phone", "code");
        branding.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void A_feature_shown_twice_or_unknown_is_refused_and_none_at_all_is_fine()
    {
        var branding = new KioskBranding { Id = KioskBranding.DefaultId };

        branding.SetLayout(new KioskScreenLayout { Features = ["featureFast", "featureFast"] }, Now)
            .Should().Equal("kiosks.layoutFeatures");
        branding.SetLayout(new KioskScreenLayout { Features = ["featurePrice"] }, Now)
            .Should().Equal("kiosks.layoutFeatures");
        branding.Layout.Should().BeNull();

        branding.SetLayout(new KioskScreenLayout { Features = [] }, Now).Should().BeEmpty();
        branding.Layout!.Features.Should().BeEmpty();
    }

    [Fact]
    public void No_layout_follows_the_default_again()
    {
        var branding = new KioskBranding { Id = Guid.NewGuid() };
        branding.SetLayout(new KioskScreenLayout { ImageLeft = true }, Now);

        branding.SetLayout(null, Now.AddMinutes(1)).Should().BeEmpty();

        branding.Layout.Should().BeNull();
    }
}
