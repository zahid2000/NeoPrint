using NeoPrint.Application.Office;

namespace NeoPrint.Application.Tests.Files;

/// <summary>
/// The origin Collabora posts the document's status to (T-465): only a plain
/// http(s) origin is handed on, never a path, a user or another scheme.
/// </summary>
public sealed class OfficeSessionOriginTests
{
    [Theory]
    [InlineData("https://print.example.az", "https://print.example.az")]
    [InlineData("https://print.example.az/", "https://print.example.az")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    public void A_plain_origin_is_kept(string value, string expected)
    {
        OfficeSessions.Origin(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("print.example.az")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://print.example.az")]
    [InlineData("https://print.example.az/browser/")]
    [InlineData("https://print.example.az/?a=1")]
    [InlineData("https://user:secret@print.example.az")]
    public void Anything_else_is_dropped(string? value)
    {
        OfficeSessions.Origin(value).Should().BeNull();
    }
}
