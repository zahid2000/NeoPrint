using System.IO.Compression;
using System.Text;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Crypto.Privacy;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Kiosks;

// ---------------------------------------------------------------------------
// Kiosk logs (plan section 7.12 "log toplama"; docs/KIOSK_UPDATES.md section 6;
// T-321): a kiosk answers an UploadLogs command with one gzip file. Cleaned of
// personal data again here, kept encrypted, deleted after Logs.RetentionDays.
// ---------------------------------------------------------------------------

public interface IKioskLogRepository
{
    Task AddAsync(KioskLog log, CancellationToken cancellationToken = default);

    Task<KioskLog?> FindAsync(Guid kioskId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>A kiosk's logs, newest first.</summary>
    Task<IReadOnlyList<KioskLog>> ListAsync(Guid kioskId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KioskLog>> ListExpiredAsync(DateTime now, int limit, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>The kiosk's logs (<c>POST kiosks/me/logs</c>): a gzip file of text lines.</summary>
public sealed record UploadKioskLogCommand(Guid DeviceId, Stream Body, Guid? CommandId, string? Ip = null) : IRequest<KioskLogView>;

/// <summary>A kiosk's logs, for an account that may download them and sees the kiosk.</summary>
public sealed record ListKioskLogsQuery(Guid AdminUserId, Guid KioskId) : IRequest<IReadOnlyList<KioskLogView>>;

/// <summary>One log file of a kiosk the account sees; the download is on the record.</summary>
public sealed record OpenKioskLogQuery(Guid AdminUserId, Guid KioskId, Guid LogId, string? Ip = null) : IRequest<KioskLogDownload>;

/// <summary>Deletes the logs past <c>Logs.RetentionDays</c>.</summary>
public sealed record PurgeExpiredKioskLogsCommand : IRequest<int>;

public sealed record KioskLogView(Guid Id, Guid KioskId, Guid? CommandId, long Size, int Lines, int Redacted, DateTime CreatedAt, DateTime ExpiresAt);

public sealed record KioskLogDownload(string FileName, long Size, Stream Content);

public sealed class KioskLogHandlers :
    IRequestHandler<UploadKioskLogCommand, KioskLogView>,
    IRequestHandler<ListKioskLogsQuery, IReadOnlyList<KioskLogView>>,
    IRequestHandler<OpenKioskLogQuery, KioskLogDownload>,
    IRequestHandler<PurgeExpiredKioskLogsCommand, int>
{
    /// <summary>
    /// What a log may grow to once uncompressed: text compresses about tenfold,
    /// a gzip bomb far more - that is refused, not unpacked.
    /// </summary>
    private const int MaxExpansion = 20;

    private const int ListLimit = 50;

    private readonly IKioskLogRepository _logs;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IFileStorage _storage;
    private readonly IPermissionResolver _permissions;
    private readonly ISettingsReader _settings;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<KioskLogHandlers> _logger;

    public KioskLogHandlers(
        IKioskLogRepository logs,
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IFileStorage storage,
        IPermissionResolver permissions,
        ISettingsReader settings,
        IAuditWriter audit,
        IClock clock,
        ILogger<KioskLogHandlers> logger)
    {
        _logs = logs;
        _kiosks = kiosks;
        _devices = devices;
        _storage = storage;
        _permissions = permissions;
        _settings = settings;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="UploadKioskLogCommand" />
    public async Task<KioskLogView> Handle(UploadKioskLogCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device?.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        var maxMb = Math.Clamp(await _settings.GetIntAsync(SettingKeys.LogsMaxUploadMb, 10, cancellationToken).ConfigureAwait(false), 1, 100);
        var limit = maxMb * 1024L * 1024L;
        var compressed = await ReadLimitedAsync(request.Body, limit, maxMb, cancellationToken).ConfigureAwait(false);

        // Cleaned once more here: an older kiosk, or a changed one, cannot put
        // a token or a phone number into what the panel hands out.
        var (cleaned, lines, redacted) = Clean(compressed, limit * MaxExpansion, maxMb);

        var now = _clock.UtcNow;
        var retention = Math.Clamp(await _settings.GetIntAsync(SettingKeys.LogsRetentionDays, 30, cancellationToken).ConfigureAwait(false), 1, 365);
        var log = new KioskLog
        {
            KioskId = kioskId,
            CommandId = request.CommandId,
            Size = cleaned.Length,
            Lines = lines,
            Redacted = redacted,
            CreatedAt = now,
            ExpiresAt = now.AddDays(retention),
        };

        using var content = new MemoryStream(cleaned, writable: false);
        var stored = await _storage
            .WriteAsync(StorageBucket.Files, $"kiosk-logs/{kioskId:N}/{log.Id:N}.log.gz", content, "application/gzip", cancellationToken)
            .ConfigureAwait(false);

        log.ProviderId = stored.ProviderId;
        log.Key = stored.Key;
        log.StoredSha256 = stored.Sha256;
        log.EncryptedLength = stored.EncryptedLength;
        log.KekId = stored.KekId;

        await _logs.AddAsync(log, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(null, "kiosks.logs.upload", nameof(Kiosk), kioskId.ToString(), null,
                    $"{log.Id} {log.Lines} lines, {log.Size} bytes, {log.Redacted} redacted", request.Ip, null, ActorType.Kiosk),
                cancellationToken)
            .ConfigureAwait(false);

        if (redacted > 0)
        {
            _logger.LogWarning("Kiosk {KioskId} sent logs with {Count} values the backend had to take out.", kioskId, redacted);
        }

        return View(log);
    }

    /// <inheritdoc cref="ListKioskLogsQuery" />
    public async Task<IReadOnlyList<KioskLogView>> Handle(ListKioskLogsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        return [.. (await _logs.ListAsync(kiosk.Id, ListLimit, cancellationToken).ConfigureAwait(false)).Select(View)];
    }

    /// <inheritdoc cref="OpenKioskLogQuery" />
    public async Task<KioskLogDownload> Handle(OpenKioskLogQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var log = await _logs.FindAsync(kiosk.Id, request.LogId, cancellationToken).ConfigureAwait(false)
                  ?? throw new NotFoundException("kioskLog");

        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "kiosks.logs.download", nameof(Kiosk), kiosk.Id.ToString(), null, log.Id.ToString(), request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        var stream = await _storage
            .OpenReadAsync(new StoredObject(log.ProviderId, log.Key, log.StoredSha256, log.EncryptedLength, log.Size, log.KekId),
                StorageBucket.Files, cancellationToken)
            .ConfigureAwait(false);

        return new KioskLogDownload($"{kiosk.Code}-{log.CreatedAt:yyyyMMdd-HHmmss}.log.gz", log.Size, stream);
    }

    /// <inheritdoc cref="PurgeExpiredKioskLogsCommand" />
    public async Task<int> Handle(PurgeExpiredKioskLogsCommand request, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var log in await _logs.ListExpiredAsync(_clock.UtcNow, 500, cancellationToken).ConfigureAwait(false))
        {
            // The file first: a record without its file can be deleted again,
            // a file without its record would never be found.
            await _storage
                .DeleteAsync(new StoredObject(log.ProviderId, log.Key, log.StoredSha256, log.EncryptedLength, log.Size, log.KekId),
                    StorageBucket.Files, cancellationToken)
                .ConfigureAwait(false);
            await _logs.DeleteAsync(log.Id, cancellationToken).ConfigureAwait(false);
            deleted++;
        }

        return deleted;
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream body, long limit, int maxMb, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new PayloadTooLargeException(UserMessage.Of("kioskLogs.tooLarge", ("max", maxMb)));
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new ValidationException([new ValidationError("logs", "kioskLogs.notGzip")]);
        }

        return buffer.ToArray();
    }

    /// <summary>Unpacks, takes out what may not be kept, packs again.</summary>
    private static (byte[] Cleaned, int Lines, int Redacted) Clean(byte[] compressed, long maxUnpacked, int maxMb)
    {
        using var output = new MemoryStream();
        var lines = 0;
        var redacted = 0;
        long unpacked = 0;

        try
        {
            using (var gzipOut = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new StreamWriter(gzipOut, new UTF8Encoding(false)))
            using (var gzipIn = new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress))
            using (var reader = new StreamReader(gzipIn, Encoding.UTF8))
            {
                while (reader.ReadLine() is { } line)
                {
                    unpacked += line.Length + 1;
                    if (unpacked > maxUnpacked)
                    {
                        throw new PayloadTooLargeException(UserMessage.Of("kioskLogs.tooLarge", ("max", maxMb)));
                    }

                    writer.WriteLine(LogRedactor.Redact(line, out var count));
                    redacted += count;
                    lines++;
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new ValidationException([new ValidationError("logs", "kioskLogs.notGzip")]);
        }

        return (output.ToArray(), lines, redacted);
    }

    private async Task<Kiosk> VisibleKioskAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null || !effective.CanSee(kiosk.Id) ? throw new NotFoundException("kiosk") : kiosk;
    }

    private static KioskLogView View(KioskLog log)
        => new(log.Id, log.KioskId, log.CommandId, log.Size, log.Lines, log.Redacted, log.CreatedAt, log.ExpiresAt);
}
