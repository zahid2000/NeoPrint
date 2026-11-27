using System.Globalization;
using System.Net.Http.Headers;

using NeoPrint.Kiosk.Core.Camera;

namespace NeoPrint.Kiosk.Core;

public sealed partial class KioskApiClient
{
    /// <summary>One sealed camera minute, for the file server (<c>POST kiosks/me/camera/segments</c>, T-485).</summary>
    public async Task SendCameraSegmentAsync(SealedSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var seconds = (int)Math.Ceiling((segment.End - segment.Start).TotalSeconds);
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"api/kiosks/me/camera/segments?start={segment.Start:yyyy-MM-dd'T'HH:mm:ss'Z'}&seconds={seconds}");

        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, path, cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        request.Content = new StreamContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
