using NeoPrint.Application.Kiosks;

namespace NeoPrint.Application.Tests.Kiosks;

/// <summary>
/// A kiosk's picture is taken by its bytes, not by what the request says it
/// is (T-340): a script or an SVG renamed to .jpg is refused.
/// </summary>
public sealed class KioskPhotoTypeTests
{
    [Fact]
    public void Jpeg_png_and_webp_are_known_by_their_signature()
    {
        KioskPhotoHandlers.ImageType([0xFF, 0xD8, 0xFF, 0xE0, 0x00]).Should().Be("image/jpeg");
        KioskPhotoHandlers.ImageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]).Should().Be("image/png");
        KioskPhotoHandlers.ImageType("RIFF\0\0\0\0WEBPVP8 "u8).Should().Be("image/webp");
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<html><script>alert(1)</script>")]
    [InlineData("GIF89a")]
    [InlineData("")]
    public void Anything_else_is_not_a_picture(string content)
        => KioskPhotoHandlers.ImageType(System.Text.Encoding.UTF8.GetBytes(content)).Should().BeNull();
}
