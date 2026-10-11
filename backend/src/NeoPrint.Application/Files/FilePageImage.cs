using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Application.Files;

/// <summary>
/// One page of a file as a PNG (plan section 6, <c>GET files/{id}/pages/{n}.png</c>):
/// the preview the mobile app's "Fayllarım" shows.
/// </summary>
/// <param name="Page">One-based, as people count pages.</param>
/// <param name="Width">Requested width; snapped to one of <see cref="FilePageImage.Widths"/>.</param>
/// <param name="IfNoneMatch">The client's cached ETag, if any.</param>
public sealed record GetFilePageImageQuery(Guid UserId, Guid FileId, int Page, int? Width, string? IfNoneMatch)
    : IRequest<FilePageImage>;

/// <summary>The rendered page, or a note that the client's copy is current.</summary>
/// <param name="Png">Null when <see cref="NotModified"/>.</param>
public sealed record FilePageImage(byte[]? Png, string ETag, bool NotModified)
{
    /// <summary>
    /// The widths a page is rendered at. A handful of sizes rather than any
    /// number: clients share cached copies, and a caller cannot make the
    /// server render the same page a thousand ways.
    /// </summary>
    public static readonly int[] Widths = [320, 640, 960, 1280, 1600];

    public const int DefaultWidth = 960;

    /// <summary>The smallest listed width at least as wide as asked for, capped at the largest.</summary>
    public static int Snap(int? requested)
        => requested is not { } width
            ? DefaultWidth
            : Widths.FirstOrDefault(candidate => candidate >= width, Widths[^1]);
}

/// <inheritdoc cref="GetFilePageImageQuery" />
public sealed class GetFilePageImageHandler : IRequestHandler<GetFilePageImageQuery, FilePageImage>
{
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly IPageRenderer _renderer;
    private readonly IPagePreviewCache _cache;

    public GetFilePageImageHandler(IFileRepository files, IFileStorage storage, IPageRenderer renderer, IPagePreviewCache cache)
    {
        _files = files;
        _storage = storage;
        _renderer = renderer;
        _cache = cache;
    }

    public async Task<FilePageImage> Handle(GetFilePageImageQuery request, CancellationToken cancellationToken)
    {
        var file = await _files.FindByIdAsync(request.FileId, cancellationToken).ConfigureAwait(false);

        // Someone else's file is "not found", not "forbidden": the answer does
        // not confirm it exists.
        if (file is null || file.UserId != request.UserId || file.DeletedAt is not null)
        {
            throw new NotFoundException("file");
        }

        return await RenderAsync(file, request.Page, request.Width, request.IfNoneMatch, _storage, _renderer, _cache, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A page of a file the caller may see: its owner's, or one shared with
    /// them (<c>GET shares/{id}/pages/{n}.png</c>).
    /// </summary>
    internal static async Task<FilePageImage> RenderAsync(
        StoredFile file,
        int page,
        int? requestedWidth,
        string? ifNoneMatch,
        IFileStorage storage,
        IPageRenderer renderer,
        IPagePreviewCache cache,
        CancellationToken cancellationToken)
    {
        if (file.Status != FileStatus.Ready || file.Pdf is null)
        {
            throw Invalid(file.Status == FileStatus.Failed
                ? file.FailureReason is { } reason ? UserMessage.Decode(reason) : new UserMessage("files.conversionFailed")
                : new UserMessage("files.notReadyRetry"));
        }

        if (page < 1 || page > file.PageCount)
        {
            throw Invalid(UserMessage.Of("files.pageMissing", ("count", file.PageCount), ("page", page)));
        }

        var width = FilePageImage.Snap(requestedWidth);

        // The PDF's hash identifies its content: a new version of the file is
        // a different PDF and a different tag.
        var etag = $"\"{file.Pdf.Sha256[..Math.Min(16, file.Pdf.Sha256.Length)]}-{page}-{width}\"";

        if (string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
        {
            return new FilePageImage(null, etag, NotModified: true);
        }

        if (!renderer.IsAvailable)
        {
            throw Invalid(new UserMessage("files.previewUnavailable"));
        }

        var stored = new StoredObject(
            file.Pdf.ProviderId,
            file.Pdf.Key,
            file.Pdf.Sha256,
            file.Pdf.EncryptedLength,
            file.Pdf.PlaintextLength,
            file.Pdf.KekId);

        try
        {
            // The whole hash names the content: the same PDF drawn at the
            // same size is the same picture, whoever asks and under whatever
            // file. That the caller may see it was settled before this point.
            var png = await cache.GetOrAddAsync(
                    $"{file.Pdf.Sha256}:{page}:{width}",
                    async token =>
                    {
                        await using var pdf = await storage.OpenReadAsync(stored, StorageBucket.Files, token)
                            .ConfigureAwait(false);

                        return await renderer.RenderPngAsync(pdf, page - 1, width, token).ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new FilePageImage(png, etag, NotModified: false);
        }
        catch (PageRenderException exception)
        {
            throw Invalid(exception.Text);
        }
    }

    private static ValidationException Invalid(UserMessage message) => new([new ValidationError("page", message)]);
}
