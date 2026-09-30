using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Printing;

/// <summary>
/// The flash file of an order paid with the NeoPrint app, on its way to the
/// account (plan sections 9.2 step 5, 7.4: "uğursuz olarsa şifrəli spool-da
/// saxlanılıb təkrarlanır"). Kept encrypted in the spool with a small list
/// beside it, so an upload that failed - no network, a restart - is sent
/// again until the backend has it.
/// </summary>
public sealed class FlashUploads
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly KioskApiClient _api;
    private readonly SpoolStore _spool;
    private readonly string _listPath;
    private readonly ILogger<FlashUploads> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FlashUploads(KioskApiClient api, SpoolStore spool, IAppPaths paths, ILogger<FlashUploads> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _api = api;
        _spool = spool;
        _listPath = Path.Combine(paths.SpoolDirectory, "uploads.json");
        _logger = logger;
    }

    /// <summary>Uploads waiting to be sent.</summary>
    public int Pending => Load().Count;

    /// <summary>Keeps the file for the job and sends it; a failure leaves it for <see cref="SendPendingAsync"/>.</summary>
    public async Task KeepAndSendAsync(Guid jobId, string sourcePath, string fileName, CancellationToken cancellationToken)
    {
        await using (var source = File.OpenRead(sourcePath))
        {
            await _spool.PutAsync(SpoolName(jobId), source, cancellationToken).ConfigureAwait(false);
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var list = Load();
            list.RemoveAll(item => item.JobId == jobId);
            list.Add(new PendingUpload(jobId, fileName, DateTime.UtcNow));
            Save(list);
        }
        finally
        {
            _lock.Release();
        }

        await SendPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends every upload still waiting; returns how many are left.</summary>
    public async Task<int> SendPendingAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var list = Load();

            foreach (var item in list.ToList())
            {
                if (!_spool.Exists(SpoolName(item.JobId)))
                {
                    list.Remove(item);
                    continue;
                }

                string? plain = null;

                try
                {
                    plain = await _spool.OpenAsync(SpoolName(item.JobId), cancellationToken).ConfigureAwait(false);

                    await using (var content = File.OpenRead(plain))
                    {
                        await _api.FlashUploadAsync(item.JobId, content, item.FileName, cancellationToken).ConfigureAwait(false);
                    }

                    Forget(list, item);
                    _logger.LogInformation("The flash file of job {JobId} reached the account.", item.JobId);
                }
                catch (KioskApiException exception) when (exception.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                {
                    // Not an app-paid flash order after all, or gone: nothing to keep it for.
                    Forget(list, item);
                    _logger.LogWarning("The flash file of job {JobId} was refused ({Code}); dropped.", item.JobId, exception.Code);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("The flash file of job {JobId} could not be sent yet; it is tried again later.", item.JobId);
                }
                finally
                {
                    _spool.Discard(plain);
                }
            }

            Save(list);
            return list.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Forget(List<PendingUpload> list, PendingUpload item)
    {
        _spool.Delete(SpoolName(item.JobId));
        list.Remove(item);
    }

    private static string SpoolName(Guid jobId) => $"upload-{jobId:N}";

    private List<PendingUpload> Load()
    {
        try
        {
            return File.Exists(_listPath) ? JsonSerializer.Deserialize<List<PendingUpload>>(File.ReadAllText(_listPath), Json) ?? [] : [];
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The upload list could not be read; starting a new one.");
            return [];
        }
    }

    private void Save(List<PendingUpload> list)
    {
        var temporary = _listPath + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(list, Json));
        File.Move(temporary, _listPath, overwrite: true);
    }

    private sealed record PendingUpload(Guid JobId, string FileName, DateTime Since);
}
