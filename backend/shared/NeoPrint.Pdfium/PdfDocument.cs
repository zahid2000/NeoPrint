using System.Runtime.InteropServices;

namespace NeoPrint.Pdfium;

/// <summary>PDFium could not read the document.</summary>
public sealed class PdfiumException : Exception
{
    public PdfiumException(string message, PdfiumError error)
        : base(message)
    {
        Error = error;
    }

    public PdfiumError Error { get; }

    /// <summary>
    /// A sentence for the person who uploaded the file.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Exception.Message"/>, which goes to the log:
    /// the user is told what to do about it, not what PDFium called it.
    /// </remarks>
    public string UserMessage => Error switch
    {
        PdfiumError.PasswordRequired =>
            "Fayl şifrələnib, açmaq mümkün deyil. Şifrəni çıxarıb yenidən yükləyin.",
        PdfiumError.UnsupportedSecurityScheme =>
            "Faylın qorunma üsulu dəstəklənmir. Şifrəni çıxarıb yenidən yükləyin.",
        PdfiumError.FormatError =>
            "Fayl zədələnib və ya düzgün PDF deyil.",
        _ => "Faylı oxumaq mümkün olmadı.",
    };
}

/// <summary>
/// A PDF loaded into memory, for counting and rendering pages.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are pinned for the lifetime of the document: PDFium reads them
/// lazily and would follow a moved pointer. Dispose releases the document and
/// unpins them, so this is always used with <c>using</c>.
/// </para>
/// <para>
/// Not thread-safe, and neither is PDFium: one document per thread, which is
/// how the conversion pipeline uses it.
/// </para>
/// </remarks>
public sealed class PdfDocument : IDisposable
{
    private readonly GCHandle _pinned;
    private IntPtr _handle;

    private PdfDocument(GCHandle pinned, IntPtr handle, int pageCount)
    {
        _pinned = pinned;
        _handle = handle;
        PageCount = pageCount;
    }

    /// <summary>Pages in the document.</summary>
    public int PageCount { get; }

    /// <summary>
    /// Loads a document from memory.
    /// </summary>
    /// <exception cref="PdfiumException">
    /// The document is encrypted, malformed, or has no pages.
    /// </exception>
    public static PdfDocument Load(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        PdfiumLibrary.EnsureInitialised();

        var pinned = GCHandle.Alloc(content, GCHandleType.Pinned);

        try
        {
            var handle = PdfiumNative.LoadMemDocument64(
                pinned.AddrOfPinnedObject(), (nuint)content.Length, password: null);

            if (handle == IntPtr.Zero)
            {
                var error = (PdfiumError)PdfiumNative.GetLastError();

                throw new PdfiumException(
                    $"PDFium refused the document: {error}.", error);
            }

            var pageCount = PdfiumNative.GetPageCount(handle);

            if (pageCount <= 0)
            {
                PdfiumNative.CloseDocument(handle);

                // A zero-page PDF would be priced at the minimum order and
                // print nothing, so it is a failure rather than a result.
                throw new PdfiumException(
                    "The document reports no pages.", PdfiumError.FormatError);
            }

            return new PdfDocument(pinned, handle, pageCount);
        }
        catch
        {
            pinned.Free();
            throw;
        }
    }

    /// <summary>Loads a document from a file.</summary>
    public static async Task<PdfDocument> LoadAsync(
        string path, CancellationToken cancellationToken = default)
        => Load(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>The size of a page, in PDF points (1/72 inch).</summary>
    public (float Width, float Height) PageSize(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PageCount);

        var page = PdfiumNative.LoadPage(_handle, index);

        if (page == IntPtr.Zero)
        {
            throw new PdfiumException(
                $"Page {index + 1} could not be loaded.", PdfiumError.PageNotFoundOrContentError);
        }

        try
        {
            return (PdfiumNative.GetPageWidth(page), PdfiumNative.GetPageHeight(page));
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>
    /// Renders one page to BGRA pixels.
    /// </summary>
    /// <param name="index">Zero-based page number.</param>
    /// <param name="targetWidth">
    /// Width in pixels. The height follows from the page's aspect ratio, so a
    /// preview never distorts the document.
    /// </param>
    /// <returns>The pixels, their dimensions and the row stride.</returns>
    public (byte[] Pixels, int Width, int Height, int Stride) RenderPage(int index, int targetWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);

        // A preview is a preview: an unbounded width on a poster-sized page
        // would allocate hundreds of megabytes.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetWidth, 4096);

        var page = PdfiumNative.LoadPage(_handle, index);

        if (page == IntPtr.Zero)
        {
            throw new PdfiumException(
                $"Page {index + 1} could not be loaded.", PdfiumError.PageNotFoundOrContentError);
        }

        try
        {
            var pageWidth = PdfiumNative.GetPageWidth(page);
            var pageHeight = PdfiumNative.GetPageHeight(page);

            if (pageWidth <= 0 || pageHeight <= 0)
            {
                throw new PdfiumException(
                    $"Page {index + 1} has no usable size.", PdfiumError.PageNotFoundOrContentError);
            }

            var height = Math.Max(1, (int)Math.Round(targetWidth * pageHeight / pageWidth));
            var stride = targetWidth * 4;
            var pixels = new byte[stride * height];

            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                // Format 4 is BGRA.
                var bitmap = PdfiumNative.BitmapCreateEx(
                    targetWidth, height, 4, pinned.AddrOfPinnedObject(), stride);

                if (bitmap == IntPtr.Zero)
                {
                    throw new PdfiumException(
                        "PDFium could not allocate the preview bitmap.", PdfiumError.Unknown);
                }

                try
                {
                    // White, not transparent: a page rendered onto
                    // transparency looks blank on a white viewer background,
                    // and a preview that looks blank reads as a broken file.
                    PdfiumNative.BitmapFillRect(bitmap, 0, 0, targetWidth, height, 0xFFFFFFFF);

                    // flags 0x01: render annotations, so a filled-in form
                    // preview matches what will be printed.
                    PdfiumNative.RenderPageBitmap(
                        bitmap, page, 0, 0, targetWidth, height, rotate: 0, flags: 0x01);
                }
                finally
                {
                    PdfiumNative.BitmapDestroy(bitmap);
                }
            }
            finally
            {
                pinned.Free();
            }

            return (pixels, targetWidth, height, stride);
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            PdfiumNative.CloseDocument(_handle);
            _handle = IntPtr.Zero;
        }

        if (_pinned.IsAllocated)
        {
            _pinned.Free();
        }
    }
}

/// <summary>
/// Initialises PDFium once per process.
/// </summary>
/// <remarks>
/// <c>FPDF_InitLibrary</c> must be called before anything else and is not
/// re-entrant. It is never torn down: the library outlives every document,
/// and destroying it while another thread holds one would crash the process.
/// </remarks>
public static class PdfiumLibrary
{
    // System.Threading.Lock is .NET 9; this targets .NET 8.
    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>Whether the native library can be loaded at all.</summary>
    /// <remarks>
    /// The backend image ships it; a developer machine may not have it, and
    /// the tests skip rather than fail there.
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                EnsureInitialised();
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    internal static void EnsureInitialised()
    {
        if (_initialised)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialised)
            {
                return;
            }

            PdfiumNative.InitLibrary();
            _initialised = true;
        }
    }
}
