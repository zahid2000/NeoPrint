using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using NeoPrint.Kiosk.Core.Camera;

namespace NeoPrint.Kiosk.Core;

public sealed partial class KioskApiClient
{
    /// <summary>One sealed camera minute, for the file server (<c>POST kiosks/me/camera/segments</c>, T-485).</summary>
    public async Task SendCameraSegmentAsync(SealedSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await SendSealedAsync(segment, "api/kiosks/me/camera/segments", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the panel asked of this kiosk's camera and it has not answered (T-483).</summary>
    public async Task<IReadOnlyList<CameraFootageRequest>> CameraRequestsAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, "api/kiosks/me/camera/requests", cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<List<CameraFootageRequest>>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One sealed minute of a span the panel asked for (T-483).</summary>
    public async Task SendRequestedCameraMinuteAsync(Guid requestId, SealedSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await SendSealedAsync(segment, $"api/kiosks/me/camera/requests/{requestId}/minutes", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything the kiosk had of the span was sent.</summary>
    public async Task CompleteCameraRequestAsync(Guid requestId, int minutes, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, $"api/kiosks/me/camera/requests/{requestId}/done", cancellationToken)
            .ConfigureAwait(false);
        request.Content = JsonContent.Create(new { minutes }, options: JsonOptions);

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One live frame (T-483).</summary>
    /// <returns>Whether somebody still watches.</returns>
    public async Task<bool> SendCameraFrameAsync(byte[] jpeg, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/kiosks/me/camera/live", cancellationToken)
            .ConfigureAwait(false);
        request.Content = new ByteArrayContent(jpeg);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return (await ReadAsync<LiveAnswer>(request, cancellationToken).ConfigureAwait(false)).Live;
    }

    private async Task SendSealedAsync(SealedSegment segment, string path, CancellationToken cancellationToken)
    {
        var seconds = (int)Math.Ceiling((segment.End - segment.Start).TotalSeconds);
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{path}?start={segment.Start:yyyy-MM-dd'T'HH:mm:ss'Z'}&seconds={seconds}");

        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, url, cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        request.Content = new StreamContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record LiveAnswer(bool Live);
}
