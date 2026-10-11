using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Abstractions;

/// <summary>A page that could not be drawn, in words the user can act on.</summary>
public sealed class PageRenderException : Exception
{
    public PageRenderException(UserMessage text, Exception? inner = null)
        : base(text.Key, inner)
    {
        Text = text;
    }

    /// <summary>What to tell the user.</summary>
    public UserMessage Text { get; }
}

/// <summary>
/// Draws one page of a PDF as a PNG: the previews the mobile app and the
/// kiosk show (plan section 6, <c>GET files/{id}/pages/{n}.png</c>).
/// </summary>
public interface IPageRenderer
{
    /// <summary>Whether rendering is possible on this machine at all.</summary>
    bool IsAvailable { get; }

    /// <param name="pdf">The decrypted PDF. Read to the end; not disposed.</param>
    /// <param name="pageIndex">Zero-based.</param>
    /// <param name="width">Width in pixels; the height follows the page's aspect ratio.</param>
    /// <exception cref="PageRenderException">The page could not be drawn.</exception>
    Task<byte[]> RenderPngAsync(Stream pdf, int pageIndex, int width, CancellationToken cancellationToken = default);
}

/// <summary>
/// Page images that were drawn a moment ago, so that scrolling back through a
/// document - or a second phone looking at the same shared file - does not
/// start a converter process for a picture that already exists.
/// </summary>
/// <remarks>
/// A cache and nothing more: an image that is not there is drawn again. The
/// caller checks that the user may see the file before asking.
/// </remarks>
public interface IPagePreviewCache
{
    /// <param name="key">Names the content, not the file: the PDF's hash, the page and the width.</param>
    /// <param name="render">Draws the image when it is not cached. Requests for the same key share one drawing.</param>
    Task<byte[]> GetOrAddAsync(
        string key, Func<CancellationToken, Task<byte[]>> render, CancellationToken cancellationToken = default);
}
