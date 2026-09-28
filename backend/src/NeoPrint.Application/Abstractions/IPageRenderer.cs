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
