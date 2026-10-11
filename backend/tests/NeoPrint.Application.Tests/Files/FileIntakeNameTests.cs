using NeoPrint.Application.Files;

namespace NeoPrint.Application.Tests.Files;

/// <summary>
/// The name a scan or a layout is saved under: a title the user typed (or the
/// app's own default), not a path (T-197).
/// </summary>
public sealed class FileIntakeNameTests
{
    [Theory]
    [InlineData("Şəxsiyyət vəsiqəsi / bank kartı", "Şəxsiyyət vəsiqəsi - bank kartı.pdf")]
    [InlineData("Dr. Əliyev arayış", "Dr. Əliyev arayış.pdf")]
    [InlineData("Foto 3×4", "Foto 3×4.pdf")]
    [InlineData("  Pasport  ", "Pasport.pdf")]
    [InlineData("a\\b/c", "a - b - c.pdf")]
    public void A_title_keeps_every_word(string title, string expected)
        => FileIntake.SafeNameWithExtension(title, "scan", "pdf").Should().Be(expected);

    [Theory]
    [InlineData("Vəsiqə.pdf", "Vəsiqə.pdf")]
    [InlineData("Vəsiqə.PDF", "Vəsiqə.pdf")]
    [InlineData("şəkil.jpg", "şəkil.jpg.pdf")]
    public void Only_its_own_extension_is_taken_off_the_end(string title, string expected)
        => FileIntake.SafeNameWithExtension(title, "scan", "pdf").Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(".pdf")]
    public void An_empty_title_gets_the_fallback(string? title)
        => FileIntake.SafeNameWithExtension(title, "scan-1", "pdf").Should().Be("scan-1.pdf");

    [Fact]
    public void A_title_cannot_be_used_as_a_path()
    {
        var name = FileIntake.SafeNameWithExtension("../../etc/passwd", "scan", "pdf");

        name.Should().Be(".. - .. - etc - passwd.pdf");
        name.Should().NotContain("/").And.NotContain("\\");
    }

    [Fact]
    public void An_uploaded_file_still_loses_its_folders()
        => FileIntake.SafeName("C:\\Users\\me/Documents/arayış.docx", "docx").Should().Be("arayış.docx");
}
