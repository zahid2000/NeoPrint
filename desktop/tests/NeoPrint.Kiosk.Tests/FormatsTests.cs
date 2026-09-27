using NeoPrint.Kiosk.Localization;

namespace NeoPrint.Kiosk.Tests;

/// <summary>docs/DESIGN_SYSTEM.md section 6, as the kiosk writes it.</summary>
public sealed class FormatsTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 20, 0, 0);

    [Fact]
    public void Dates_read_the_same_in_every_language_with_words_for_today_and_yesterday()
    {
        foreach (var language in Strings.SupportedLanguages)
        {
            Formats.DateTime(new DateTime(2026, 9, 25, 18, 33, 0), language, Now).Should().Be("25.09.2026 18:33");
        }

        Formats.DateTime(new DateTime(2026, 9, 27, 14, 20, 0), "az", Now).Should().Be("Bu gün 14:20");
        Formats.DateTime(new DateTime(2026, 9, 26, 9, 5, 0), "en", Now).Should().Be("Yesterday 09:05");
        Formats.DateTime(new DateTime(2026, 9, 27, 14, 20, 0), "ru", Now).Should().Be("Сегодня 14:20");
    }

    [Fact]
    public void Money_follows_the_language()
    {
        Formats.Money(1240m, "az").Should().Be("1\u00a0240,00\u00a0₼");
        Formats.Money(12.5m, "ru").Should().Be("12,50\u00a0₼");
        Formats.Money(1240m, "en").Should().Be("1,240.00\u00a0₼");
        Formats.Money(-3m, "az").Should().Be("−3,00\u00a0₼");
    }

    [Theory]
    [InlineData(1, "ru", "1 страница")]
    [InlineData(3, "ru", "3 страницы")]
    [InlineData(5, "ru", "5 страниц")]
    [InlineData(11, "ru", "11 страниц")]
    [InlineData(21, "ru", "21 страница")]
    [InlineData(1, "en", "1 page")]
    [InlineData(12, "en", "12 pages")]
    [InlineData(12, "az", "12 səhifə")]
    public void Pages_are_written_out_in_full(int count, string language, string expected)
        => Formats.Pages(count, language).Should().Be(expected);

    [Fact]
    public void The_clock_is_24_hour()
        => Formats.Time(new DateTime(2026, 9, 27, 18, 3, 0)).Should().Be("18:03");
}
