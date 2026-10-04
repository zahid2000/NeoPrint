using NeoPrint.Pdfium;

namespace NeoPrint.Shared.Tests.Layout;

/// <summary>
/// A <see cref="FactAttribute"/> that skips when the PDFium native library is
/// not on the machine.
/// </summary>
/// <remarks>
/// CI installs <c>libpdfium.so</c> before the backend job and the backend image
/// carries it, so these measurements always run there. A developer machine
/// may not have it; put the kiosk build's copy on <c>PATH</c>
/// (<c>desktop/src/NeoPrint.Kiosk/obj/pdfium/pdfium-win-x64/bin</c>) to run them.
/// </remarks>
public sealed class PdfiumFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() => PdfiumLibrary.IsAvailable, isThreadSafe: true);

    public PdfiumFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "The PDFium native library is not available on this machine. CI and the backend image both have it.";
        }
    }
}
