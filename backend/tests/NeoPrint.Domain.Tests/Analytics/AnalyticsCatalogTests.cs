using NeoPrint.Domain.Analytics;

namespace NeoPrint.Domain.Tests.Analytics;

/// <summary>
/// The names a kiosk built before T-214 sends for its payment screens are
/// read under today's names (T-282), so those kiosks still count until the
/// update system replaces them.
/// </summary>
public sealed class AnalyticsCatalogTests
{
    private static Dictionary<string, string?> Props(params (string Key, string? Value)[] props)
        => props.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void Payment_method_is_payment_started_with_its_method()
    {
        var (name, props) = AnalyticsCatalog.Current("payment_method", Props(("method", "BankQr")));

        name.Should().Be("payment_started");
        AnalyticsCatalog.KioskProps(name, props).Should().BeEquivalentTo(new Dictionary<string, string> { ["method"] = "BankQr" });
    }

    [Theory]
    [InlineData("Failed", "payment_failed")]
    [InlineData("Expired", "payment_expired")]
    [InlineData(null, "payment_expired")]
    public void Payment_ended_is_failed_when_the_bank_said_no_and_expired_otherwise(string? status, string expected)
    {
        var (name, props) = AnalyticsCatalog.Current("payment_ended", Props(("status", status)));

        name.Should().Be(expected);
        AnalyticsCatalog.KioskProps(name, props).Should().BeEmpty("the old event carried a status, which today's does not keep");
    }

    [Theory]
    [InlineData("file_selected")]
    [InlineData("payment_started")]
    [InlineData("something_new")]
    [InlineData(null)]
    public void Any_other_name_is_left_as_it_is(string? sent)
        => AnalyticsCatalog.Current(sent, null).Name.Should().Be(sent);
}
