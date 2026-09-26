using System.Runtime.InteropServices;

namespace NeoPrint.Pdfium;

/// <summary>
/// The PDFium entry points this project uses.
/// </summary>
/// <remarks>
/// <para>
/// Our own P/Invoke rather than a managed wrapper package (plan section 2.0):
/// the surface needed is a dozen functions, and a binding is easier to audit
/// than a dependency. Only the prebuilt native library is taken from
/// bblanchon/pdfium-binaries, which the backend image copies to
/// <c>/usr/local/lib/libpdfium.so</c>.
/// </para>
/// <para>
/// PDFium is a C++ PDF parser reading untrusted input, so nothing here is
/// called on a file a user supplied directly: the pipeline converts first in
/// the sandbox, and this reads the PDF the sandbox produced. Even then the
/// library is initialised once per process and every handle is released, so
/// a malformed document costs memory for one call rather than for the life
/// of the process.
/// </para>
/// </remarks>
internal static partial class PdfiumNative
{
    /// <summary>
    /// The library name. The runtime resolves it per platform:
    /// <c>libpdfium.so</c>, <c>pdfium.dll</c>, <c>libpdfium.dylib</c>.
    /// </summary>
    private const string Library = "pdfium";

    [LibraryImport(Library, EntryPoint = "FPDF_InitLibrary")]
    internal static partial void InitLibrary();

    [LibraryImport(Library, EntryPoint = "FPDF_DestroyLibrary")]
    internal static partial void DestroyLibrary();

    /// <summary>Loads a document already in memory.</summary>
    /// <param name="password">Null for an unencrypted document.</param>
    [LibraryImport(Library, EntryPoint = "FPDF_LoadMemDocument64", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr LoadMemDocument64(IntPtr data, nuint size, string? password);

    [LibraryImport(Library, EntryPoint = "FPDF_CloseDocument")]
    internal static partial void CloseDocument(IntPtr document);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageCount")]
    internal static partial int GetPageCount(IntPtr document);

    /// <summary>The error from the last failed call, as an <see cref="PdfiumError"/>.</summary>
    [LibraryImport(Library, EntryPoint = "FPDF_GetLastError")]
    internal static partial uint GetLastError();

    [LibraryImport(Library, EntryPoint = "FPDF_LoadPage")]
    internal static partial IntPtr LoadPage(IntPtr document, int index);

    [LibraryImport(Library, EntryPoint = "FPDF_ClosePage")]
    internal static partial void ClosePage(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageWidthF")]
    internal static partial float GetPageWidth(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageHeightF")]
    internal static partial float GetPageHeight(IntPtr page);

    /// <summary>Creates a bitmap PDFium will render into.</summary>
    /// <param name="format">4 is BGRA, which is what the PNG encoder wants.</param>
    [LibraryImport(Library, EntryPoint = "FPDFBitmap_CreateEx")]
    internal static partial IntPtr BitmapCreateEx(
        int width, int height, int format, IntPtr firstScan, int stride);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_FillRect")]
    internal static partial void BitmapFillRect(
        IntPtr bitmap, int left, int top, int width, int height, uint colour);

    [LibraryImport(Library, EntryPoint = "FPDFBitmap_Destroy")]
    internal static partial void BitmapDestroy(IntPtr bitmap);

    /// <param name="flags">2 is LCD text off plus annotations; 0x01 renders annotations.</param>
    [LibraryImport(Library, EntryPoint = "FPDF_RenderPageBitmap")]
    internal static partial void RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int width, int height, int rotate, int flags);
}

/// <summary>The error codes <c>FPDF_GetLastError</c> returns.</summary>
public enum PdfiumError : uint
{
    Success = 0,
    Unknown = 1,
    FileNotFoundOrUnavailable = 2,
    FormatError = 3,

    /// <summary>The document is encrypted and needs a password.</summary>
    PasswordRequired = 4,

    UnsupportedSecurityScheme = 5,
    PageNotFoundOrContentError = 6,
}
