using NeoPrint.Pdfium;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips when the PDFium native library is
/// not on the machine.
/// </summary>
/// <remarks>
/// <para>
/// The backend image carries <c>libpdfium.so</c> (backend/Dockerfile), and
/// CI fetches it from the same release before running the backend job, so
/// these tests really do run in both places. A developer machine usually has
/// neither, and somebody working on the pricing rules should not have to
/// install a native library to run the suite.
/// </para>
/// <para>
/// Both need Docker as well, since the file has to come out of storage, so
/// this also carries the Docker check. Combining them in one attribute keeps
/// the reason a test skipped honest: xUnit reports only one.
/// </para>
/// </remarks>
public sealed class PdfiumFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(
        () => PdfiumLibrary.IsAvailable, isThreadSafe: true);

    public PdfiumFactAttribute()
    {
        if (new DockerFactAttribute().Skip is { } dockerSkip)
        {
            Skip = dockerSkip;
            return;
        }

        if (!Available.Value)
        {
            Skip = "The PDFium native library is not available on this machine; "
                   + "conversion tests are skipped. CI and the backend image both have it.";
        }
    }
}
