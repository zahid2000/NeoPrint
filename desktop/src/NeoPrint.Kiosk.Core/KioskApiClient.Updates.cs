using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NeoPrint.Kiosk.Core;

// ---------------------------------------------------------------------------
// Wire contracts: updates and logs (plan section 7.12; docs/KIOSK_UPDATES.md, T-321)
// ---------------------------------------------------------------------------

/// <summary>One package this kiosk should run (backend <c>KioskReleaseOffer</c>).</summary>
/// <param name="Manifest">The signed manifest, exactly as signed.</param>
/// <param name="Signature">Its detached signature.</param>
/// <param name="Download">Where to fetch the package, relative to the API.</param>
/// <param name="Urgent">A security fix: not only in the update window.</param>
public sealed record KioskReleaseOffer(
    string Component,
    Guid ReleaseId,
    string Version,
    string Manifest,
    string Signature,
    long Size,
    string Download,
    bool Urgent = false);

/// <summary>What the backend offers this kiosk (<c>GET kiosks/me/update</c>).</summary>
public sealed record KioskUpdateOffers(
    string? Platform,
    string KeySet,
    IReadOnlyList<KioskReleaseOffer> Offers,
    IReadOnlyList<string> Withdrawn);

/// <summary>When and how this kiosk updates itself (configuration <c>update</c>).</summary>
public sealed record KioskUpdateSettings(
    string Window = "02:00-05:00",
    int CheckIntervalMinutes = 15,
    int HealthTimeoutMinutes = 5,
    int KeepVersions = 3,
    int MaxIdleWaitMinutes = 30,
    int MaxLogUploadMb = 10);

/// <summary>A package download did not go through.</summary>
public sealed class ReleaseDownloadException(string message, Exception? inner = null) : Exception(message, inner);

public sealed partial class KioskApiClient
{
    /// <summary>What this kiosk should run, component by component.</summary>
    public Task<KioskUpdateOffers> GetUpdateOffersAsync(CancellationToken cancellationToken = default)
        => GetAsync<KioskUpdateOffers>("api/kiosks/me/update", cancellationToken);

    /// <summary>
    /// Downloads a package, from <paramref name="from"/> bytes on when part of
    /// it is there already.
    /// </summary>
    /// <param name="open">
    /// Called once the answer is in: with true when the backend goes on from
    /// <paramref name="from"/> (206) - the caller appends - and with false when
    /// it sends the whole file - the caller starts the file over.
    /// </param>
    /// <exception cref="ReleaseDownloadException">The package is not offered any more, or the transfer broke off.</exception>
    public async Task DownloadReleaseAsync(
        string download, long from, Func<bool, Stream> open, Func<long, CancellationToken, Task>? throttle, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, download, cancellationToken).ConfigureAwait(false);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (from > 0)
        {
            request.Headers.Range = new RangeHeaderValue(from, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await Sent(_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ReleaseDownloadException("The package download could not start.", exception);
        }

        using (response)
        {
            // Not offered any more (withdrawn, rollout paused): a 404 here is not "the device was detached".
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new ReleaseDownloadException($"The package is not offered any more ({(int)response.StatusCode}).");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new DeviceRevokedException("Bu cihaz ləğv edilib. Yeni cihaz açarı yaradılacaq.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseDownloadException($"The package download was answered with {(int)response.StatusCode}.");
            }

            var resumed = response.StatusCode == HttpStatusCode.PartialContent;
            await using var destination = open(resumed);
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (throttle is not null)
                    {
                        await throttle(read, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                // What arrived is kept; the next attempt goes on from there.
                await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ReleaseDownloadException("The package download broke off.", exception);
            }
        }
    }

    /// <summary>Where an update of one component stands here (<c>POST kiosks/me/update-status</c>).</summary>
    public async Task ReportUpdateStatusAsync(
        string component, string version, string status, string? messageKey, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/kiosks/me/update-status", cancellationToken)
            .ConfigureAwait(false);
        request.Content = JsonContent.Create(new { component, version, status, messageKey }, options: JsonOptions);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The kiosk's logs, gzip compressed (<c>POST kiosks/me/logs</c>).</summary>
    /// <returns>The id the backend keeps them under.</returns>
    public async Task<Guid> UploadLogsAsync(byte[] gzip, Guid? commandId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, commandId is { } id ? $"api/kiosks/me/logs?commandId={id}" : "api/kiosks/me/logs", cancellationToken)
            .ConfigureAwait(false);
        request.Content = new ByteArrayContent(gzip);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

        var answer = await ReadAsync<UploadedLog>(request, cancellationToken).ConfigureAwait(false);
        return answer.Id;
    }

    private sealed record UploadedLog(Guid Id);
}
