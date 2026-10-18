using NeoPrint.Domain.Settings;

namespace NeoPrint.Domain.Tests.Settings;

/// <summary>The map's style and attribution settings (T-217).</summary>
public sealed class MapSettingsTests
{
    [Fact]
    public void The_default_credits_openfreemap_openmaptiles_and_openstreetmap_each_as_a_link()
    {
        var links = MapSettings.ParseAttribution(MapSettings.DefaultAttribution);

        links.Should().NotBeNull();
        links!.Select(link => link.Label).Should().Equal("OpenFreeMap", "© OpenMapTiles", "© OpenStreetMap contributors");
        links.Should().AllSatisfy(link => link.Url.Should().StartWith("https://"));
        MapSettings.IsValidStyleUrl(MapSettings.DefaultStyleUrl).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://tiles.example.az/styles/liberty", true)]
    [InlineData("http://tiles.example.az/styles/liberty", false)]
    [InlineData("tiles.example.az/styles/liberty", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_style_is_a_full_https_address(string? value, bool valid)
        => MapSettings.IsValidStyleUrl(value).Should().Be(valid);

    [Theory]
    // An own server: its name may change, the data's licence may not go.
    [InlineData("NeoPrint xəritəsi|https://tiles.example.az;© OpenStreetMap contributors|https://www.openstreetmap.org/copyright", true)]
    [InlineData("OpenFreeMap|https://openfreemap.org", false)]
    [InlineData("OpenFreeMap|https://openfreemap.org;© OpenStreetMap|http://www.openstreetmap.org/copyright", false)]
    [InlineData("© OpenStreetMap contributors", false)]
    [InlineData("|https://www.openstreetmap.org/copyright", false)]
    [InlineData("  ", false)]
    public void An_attribution_is_text_and_https_links_and_always_keeps_openstreetmap(string value, bool valid)
        => (MapSettings.ParseAttribution(value) is not null).Should().Be(valid);
}
