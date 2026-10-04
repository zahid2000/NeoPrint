using NeoPrint.Layout;
using NeoPrint.Pdfium;

using SkiaSharp;

namespace NeoPrint.Shared.Tests.Layout;

/// <summary>
/// The layout renderer (plan section 2.11): pictures print at their real size.
/// </summary>
/// <remarks>
/// Sizes are measured the way a ruler would: the PDF is rasterised at 300 dpi
/// with PDFium and the black picture's extent is counted in pixels. One pixel
/// is 0.085 mm, so the 0.25 mm tolerance is three pixels of anti-aliasing,
/// well inside the 1 mm the plan allows on paper.
/// </remarks>
public sealed class LayoutRendererTests
{
    private const double Tolerance = 0.25;
    private const int A4WidthPixels = 2480;
    private const double MmPerPixel = 210.0 / A4WidthPixels;

    [PdfiumFact]
    public void An_id_card_prints_at_85_6_by_53_98_mm()
    {
        var project = Page(new LayoutElement("card", 62.2, 89.52, 85.6, 53.98));

        var box = DarkBox(Render(project, ("card", Solid(1000, 631))));

        box.Width.Should().BeApproximately(85.6, Tolerance);
        box.Height.Should().BeApproximately(53.98, Tolerance);
        box.Left.Should().BeApproximately(62.2, Tolerance);
        box.Top.Should().BeApproximately(89.52, Tolerance);
    }

    [PdfiumFact]
    public void A_turned_card_keeps_its_size_and_swaps_its_sides()
    {
        var project = Page(new LayoutElement("card", 62.2, 89.52, 85.6, 53.98, RotationDeg: 90));

        var box = DarkBox(Render(project, ("card", Solid(1000, 631))));

        box.Width.Should().BeApproximately(53.98, Tolerance);
        box.Height.Should().BeApproximately(85.6, Tolerance);

        // Turned about its centre, which stays where it was.
        (box.Left + (box.Width / 2)).Should().BeApproximately(62.2 + 42.8, Tolerance);
        (box.Top + (box.Height / 2)).Should().BeApproximately(89.52 + 26.99, Tolerance);
    }

    [PdfiumFact]
    public void The_page_is_the_paper_the_layout_names()
    {
        var landscape = new LayoutProject(
            LayoutProject.CurrentVersion,
            LayoutPaper.A3,
            LayoutOrientation.Landscape,
            10,
            [new LayoutPage([new LayoutElement("card", 10, 10, 85.6, 53.98)])]);

        using var document = PdfDocument.Load(Render(landscape, ("card", Solid(100, 63))));

        var (width, height) = document.PageSize(0);
        (width / LayoutGeometry.PointsPerMm).Should().BeApproximately(420, 0.01);
        (height / LayoutGeometry.PointsPerMm).Should().BeApproximately(297, 0.01);
    }

    [Fact]
    public void Every_page_of_the_layout_is_a_page_of_the_pdf()
    {
        var project = new LayoutProject(
            LayoutProject.CurrentVersion,
            LayoutPaper.A4,
            LayoutOrientation.Portrait,
            10,
            [
                new LayoutPage([new LayoutElement("a", 10, 10, 50, 50)]),
                new LayoutPage([new LayoutElement("b", 10, 10, 50, 50)]),
            ]);

        var pdf = Render(project, ("a", Solid(100, 100)), ("b", Solid(100, 100)));

        using var stream = new MemoryStream(pdf);
        using var read = PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        read.PageCount.Should().Be(2);
    }

    [PdfiumFact]
    public void The_same_layout_draws_the_same_page()
    {
        var project = Page(new LayoutElement("card", 62.2, 89.52, 85.6, 53.98, RotationDeg: 12.5));
        var card = Photo(1200, 760);

        var first = Pixels(Render(project, ("card", card)));
        var second = Pixels(Render(project, ("card", card)));

        second.Should().Equal(first);
    }

    [PdfiumFact]
    public void A_moved_picture_draws_a_different_page()
    {
        var card = Photo(1200, 760);

        var first = Pixels(Render(Page(new LayoutElement("card", 62.2, 89.52, 85.6, 53.98)), ("card", card)));
        var moved = Pixels(Render(Page(new LayoutElement("card", 62.2, 90.52, 85.6, 53.98)), ("card", card)));

        moved.Should().NotEqual(first);
    }

    [Fact]
    public void A_picture_is_prepared_the_same_way_every_time()
    {
        var photo = Photo(4000, 2523);

        LayoutRenderer.Prepare(photo, 85.6, 53.98).Should().Equal(LayoutRenderer.Prepare(photo, 85.6, 53.98));
    }

    [Fact]
    public void A_picture_is_reduced_to_300_dpi_at_its_printed_size()
    {
        var prepared = LayoutRenderer.Prepare(Photo(4000, 2523), 85.6, 53.98);

        using var bitmap = SKBitmap.Decode(prepared);

        // 85.6 mm at 300 dpi is 1011.0 pixels; 53.98 mm is 637.6.
        bitmap.Width.Should().BeLessThanOrEqualTo(1012);
        bitmap.Height.Should().BeLessThanOrEqualTo(638);
        bitmap.Width.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void A_small_picture_is_never_enlarged()
    {
        var prepared = LayoutRenderer.Prepare(Photo(300, 200), 85.6, 53.98);

        using var bitmap = SKBitmap.Decode(prepared);
        bitmap.Width.Should().Be(300);
        bitmap.Height.Should().Be(200);
    }

    [Fact]
    public void A_photo_the_phone_stored_sideways_is_turned_upright()
    {
        // Pixels 400 wide and 200 tall, with EXIF orientation 6: "turn 90°
        // clockwise to show". Upright it is 200 wide and 400 tall.
        var sideways = WithExifOrientation(Photo(400, 200), 6);

        using var bitmap = SKBitmap.Decode(LayoutRenderer.Prepare(sideways, 100, 200));

        bitmap.Width.Should().Be(200);
        bitmap.Height.Should().Be(400);
    }

    [Fact]
    public void A_transparent_picture_stays_a_png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(50, 50, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        var prepared = LayoutRenderer.Prepare(png.ToArray(), 20, 20);

        prepared.Take(4).Should().Equal(0x89, (byte)'P', (byte)'N', (byte)'G');
    }

    [Fact]
    public void A_picture_that_is_not_there_is_refused()
    {
        var project = Page(new LayoutElement("missing", 10, 10, 50, 50));

        var render = () => LayoutRenderer.Render(project, _ => null, new MemoryStream());

        render.Should().Throw<LayoutException>().WithMessage("layout.imageMissing");
    }

    [Fact]
    public void Bytes_that_are_not_a_picture_are_refused()
    {
        var project = Page(new LayoutElement("text", 10, 10, 50, 50));

        var render = () => LayoutRenderer.Render(project, _ => "not a picture"u8.ToArray(), new MemoryStream());

        render.Should().Throw<LayoutException>().WithMessage("layout.imageUnreadable");
    }

    private static LayoutProject Page(params LayoutElement[] elements)
        => new(LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, 10, [new LayoutPage(elements)]);

    private static byte[] Render(LayoutProject project, params (string Key, byte[] Bytes)[] images)
    {
        var lookup = images.ToDictionary(image => image.Key, image => image.Bytes, StringComparer.Ordinal);
        using var output = new MemoryStream();

        LayoutRenderer.Render(project, key => lookup.GetValueOrDefault(key), output);

        return output.ToArray();
    }

    /// <summary>A black picture, so its printed extent can be found on a white page.</summary>
    internal static byte[] Solid(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);

        return data.ToArray();
    }

    /// <summary>A picture with detail in it, so resampling and encoding have something to do.</summary>
    internal static byte[] Photo(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(width, height), [SKColors.SteelBlue, SKColors.Orange], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);

        return data.ToArray();
    }

    /// <summary>The JPEG with an EXIF segment carrying only an orientation tag, as a phone camera writes it.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, // APP1, length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian TIFF, IFD at 8
            0x00, 0x01, // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];

        return [.. jpeg.Take(2), .. exif, .. jpeg.Skip(2)];
    }

    /// <summary>Page 1 rasterised at 150 dpi.</summary>
    private static byte[] Pixels(byte[] pdf)
    {
        using var document = PdfDocument.Load(pdf);

        return document.RenderPage(0, A4WidthPixels / 2).Pixels;
    }

    /// <summary>The extent of the dark pixels on page 1, in millimetres.</summary>
    private static RectMm DarkBox(byte[] pdf)
    {
        using var document = PdfDocument.Load(pdf);
        var (pixels, width, height, stride) = document.RenderPage(0, A4WidthPixels);

        int left = width, top = height, right = -1, bottom = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);

                // BGRA; anything darker than mid-grey belongs to the picture.
                if (pixels[offset] + pixels[offset + 1] + pixels[offset + 2] < 3 * 128)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        right.Should().BeGreaterThan(-1, "the picture must be on the page");

        return new RectMm(left * MmPerPixel, top * MmPerPixel, (right + 1) * MmPerPixel, (bottom + 1) * MmPerPixel);
    }
}
