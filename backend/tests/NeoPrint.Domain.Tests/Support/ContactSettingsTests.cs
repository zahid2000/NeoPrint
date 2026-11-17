using FluentAssertions;

using NeoPrint.Domain.Support;

namespace NeoPrint.Domain.Tests.Support;

/// <summary>The app's "Əlaqə" (T-438): only links to the four known places.</summary>
public sealed class ContactSettingsTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ContactKind.Phone, "+994 12 345-67-89", "tel:%2B994123456789")]
    [InlineData(ContactKind.Phone, "*1234", "tel:%2A1234")]
    [InlineData(ContactKind.WhatsApp, "+994 50 123 45 67", "https://wa.me/994501234567")]
    [InlineData(ContactKind.WhatsApp, "https://wa.me/994501234567", "https://wa.me/994501234567")]
    [InlineData(ContactKind.Telegram, "@neoprint_az", "https://t.me/neoprint_az")]
    [InlineData(ContactKind.Telegram, "https://t.me/neoprint_az", "https://t.me/neoprint_az")]
    [InlineData(ContactKind.Telegram, "+994501234567", "https://t.me/+994501234567")]
    [InlineData(ContactKind.Email, "help@neoprint.example", "mailto:help@neoprint.example")]
    public void Each_kind_makes_its_own_link(ContactKind kind, string value, string link)
        => ContactSettings.Link(kind, value).Should().Be(link);

    [Theory]
    [InlineData(ContactKind.Phone, "call us")]
    [InlineData(ContactKind.Phone, "12")]
    [InlineData(ContactKind.WhatsApp, "https://evil.example/wa.me")]
    [InlineData(ContactKind.WhatsApp, "http://wa.me/994501234567")]
    [InlineData(ContactKind.WhatsApp, "12345")]
    [InlineData(ContactKind.Telegram, "https://t.me.evil.example/x")]
    [InlineData(ContactKind.Telegram, "@ab")]
    [InlineData(ContactKind.Email, "javascript:alert(1)")]
    [InlineData(ContactKind.Email, "no-at-sign.example")]
    public void Nothing_links_anywhere_else(ContactKind kind, string value)
        => ContactSettings.Link(kind, value).Should().BeNull();

    [Fact]
    public void Channels_are_kept_one_per_kind_and_an_empty_one_is_off()
    {
        var settings = new ContactSettings { Id = ContactSettings.DefaultId };

        var wrong = settings.Set(
            [
                new ContactChannel { Kind = ContactKind.Phone, Value = " *1234 ", IsActive = true },
                new ContactChannel { Kind = ContactKind.Email, Value = "", IsActive = false },
            ],
            Now);

        wrong.Should().BeEmpty();
        settings.Channel(ContactKind.Phone).Value.Should().Be("*1234");
        settings.Channel(ContactKind.WhatsApp).IsActive.Should().BeFalse();
        settings.Channels.Should().HaveCount(4);
        settings.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void A_wrong_value_or_an_active_empty_one_changes_nothing()
    {
        var settings = new ContactSettings { Id = ContactSettings.DefaultId };
        settings.Set([new ContactChannel { Kind = ContactKind.Phone, Value = "*1234", IsActive = true }], Now);

        var wrong = settings.Set(
            [
                new ContactChannel { Kind = ContactKind.Phone, Value = "*5555", IsActive = true },
                new ContactChannel { Kind = ContactKind.WhatsApp, Value = "", IsActive = true },
                new ContactChannel { Kind = ContactKind.Email, Value = "nope", IsActive = true },
            ],
            Now.AddHours(1));

        wrong.Should().BeEquivalentTo(new[]
        {
            (ContactKind.WhatsApp, "contact.valueRequired"),
            (ContactKind.Email, "contact.invalidEmail"),
        });
        settings.Channel(ContactKind.Phone).Value.Should().Be("*1234");
        settings.UpdatedAt.Should().Be(Now);
    }
}
