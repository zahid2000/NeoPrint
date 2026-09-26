using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Files;

/// <summary>
/// Accepts an upload: checks it, stores it encrypted, records it
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// The order of the checks is deliberate. The cheap ones that need no bytes
/// come first - the declared size, the quota - so an oversized upload is
/// refused before it is read. The format is decided from the content, which
/// needs only the first block. The page count cannot be known until the file
/// has been converted, so that limit is enforced when conversion finishes,
/// not here.
/// </remarks>
public sealed class UploadFileHandler : IRequestHandler<UploadFileCommand, UserFileResult>
{
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly IKioskRepository _kiosks;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<UploadFileHandler> _logger;

    public UploadFileHandler(
        IFileRepository files,
        IFileStorage storage,
        IKioskRepository kiosks,
        ISettingsReader settings,
        IClock clock,
        IEventPublisher events,
        ICorrelationContext correlation,
        ILogger<UploadFileHandler> logger)
    {
        _files = files;
        _storage = storage;
        _kiosks = kiosks;
        _settings = settings;
        _clock = clock;
        _events = events;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task<UserFileResult> Handle(UploadFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxBytes = await MaxBytesAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        if (request.DeclaredLength > maxBytes)
        {
            throw Invalid(
                "file",
                $"Fayl {Megabytes(request.DeclaredLength)} MB-dır; icazə verilən maksimum {Megabytes(maxBytes)} MB.");
        }

        await EnsureQuotaAsync(request.UserId, request.DeclaredLength, cancellationToken)
            .ConfigureAwait(false);

        // The first block decides what the file is. Buffered here rather than
        // seeking, because the request body is not seekable.
        var header = new byte[ContentSignature.HeaderLength];
        var headerLength = await ReadAtMostAsync(request.Content, header, cancellationToken)
            .ConfigureAwait(false);

        var signature = ContentSignature.Identify(header.AsSpan(0, headerLength), request.FileName);

        if (!signature.Recognised)
        {
            throw Invalid("file", signature.Reason ?? "Fayl formatı tanınmadı.");
        }

        var format = signature.Format!;

        await EnsureFormatEnabledAsync(format, cancellationToken).ConfigureAwait(false);

        var file = new StoredFile
        {
            UserId = request.UserId,
            FileName = SafeName(request.FileName, format.Extension),
            Extension = format.Extension,
            MediaType = format.MediaType,
            Source = request.Source,
            IsSensitive = request.IsSensitive,
            AutoDelete = await DefaultAutoDeleteAsync(request.IsSensitive, cancellationToken)
                .ConfigureAwait(false),
        };

        // The header was already consumed, so storage sees the whole file
        // again: what was read, then the rest.
        await using var content = new PrefixedStream(header.AsMemory(0, headerLength), request.Content);

        var stored = await _storage.WriteAsync(
                StorageBucket.Files,
                $"files/{request.UserId:N}/{file.Id:N}/original.{format.Extension}",
                content,
                format.MediaType,
                cancellationToken)
            .ConfigureAwait(false);

        // What was declared is a claim too. A body longer than the limit is
        // refused even when the header said otherwise.
        if (stored.PlaintextLength > maxBytes)
        {
            await _storage.DeleteAsync(stored, StorageBucket.Files, cancellationToken).ConfigureAwait(false);

            throw Invalid(
                "file",
                $"Fayl {Megabytes(stored.PlaintextLength)} MB-dır; icazə verilən maksimum {Megabytes(maxBytes)} MB.");
        }

        file.Original = ToFileObject(stored);

        await _files.AddAsync(file, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} uploaded {Extension} file {FileId} ({Bytes} bytes).",
            request.UserId,
            format.Extension,
            file.Id,
            stored.PlaintextLength);

        // After the document exists, so a consumer that picks it up
        // immediately finds something to read. Conversion runs off the
        // request: it can take a minute, and the upload is finished once the
        // bytes are safe (plan section 2.2).
        await _events.PublishAsync(new FileUploaded(file.Id, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        return Map(file);
    }

    private async Task<long> MaxBytesAsync(Guid? kioskId, CancellationToken cancellationToken)
    {
        var defaultMb = await _settings
            .GetIntAsync(SettingKeys.FilesDefaultMaxSizeMb, 50, cancellationToken)
            .ConfigureAwait(false);

        if (kioskId is null)
        {
            return defaultMb * 1024L * 1024L;
        }

        // The kiosk's own limit wins when it has one (plan section 2.2).
        var kiosk = await _kiosks.FindByIdAsync(kioskId.Value, cancellationToken).ConfigureAwait(false);

        return (kiosk?.MaxFileSizeMb ?? defaultMb) * 1024L * 1024L;
    }

    private async Task EnsureQuotaAsync(Guid userId, long incoming, CancellationToken cancellationToken)
    {
        var quotaGb = await _settings
            .GetIntAsync(SettingKeys.FilesUserQuotaGb, 1, cancellationToken)
            .ConfigureAwait(false);

        var quota = quotaGb * 1024L * 1024L * 1024L;
        var used = await _files.TotalBytesAsync(userId, cancellationToken).ConfigureAwait(false);

        if (used + incoming > quota)
        {
            throw Invalid(
                "file",
                $"Saxlama kvotası dolub: {Megabytes(used)} MB / {quotaGb} GB. "
                + "Yer boşaltmaq üçün köhnə faylları silin.");
        }
    }

    private async Task EnsureFormatEnabledAsync(FileFormat format, CancellationToken cancellationToken)
    {
        var allowed = await _settings
            .GetAsync(SettingKeys.FilesAllowedFormats, cancellationToken)
            .ConfigureAwait(false);

        // An empty setting means the catalogue default - every MVP format -
        // rather than "nothing is allowed", which would break a fresh install.
        if (string.IsNullOrWhiteSpace(allowed))
        {
            return;
        }

        var enabled = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!enabled.Contains(format.Extension, StringComparer.OrdinalIgnoreCase))
        {
            throw Invalid("file", $"'{format.Extension}' formatı hazırda qəbul edilmir.");
        }
    }

    private async Task<AutoDeleteRule> DefaultAutoDeleteAsync(
        bool isSensitive, CancellationToken cancellationToken)
    {
        if (!isSensitive)
        {
            return AutoDeleteRule.Never;
        }

        // Plan section 2.13: a scanned identity document defaults to deleting
        // itself after the print, not to living in the account forever.
        var configured = await _settings
            .GetAsync(SettingKeys.PrivacySensitiveAutoDeleteDefault, cancellationToken)
            .ConfigureAwait(false);

        return Enum.TryParse<AutoDeleteRule>(configured, ignoreCase: true, out var rule)
            ? rule
            : AutoDeleteRule.AfterPrint;
    }

    /// <summary>
    /// A display name that cannot be used as a path.
    /// </summary>
    /// <remarks>
    /// The object key is built from identifiers, so this is only what the user
    /// sees - but a name carrying <c>../</c> or a NUL byte would still end up
    /// in a Content-Disposition header and in the admin panel.
    /// </remarks>
    private static string SafeName(string? fileName, string extension)
    {
        var candidate = Path.GetFileName(fileName ?? string.Empty).Trim();

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(c, '_');
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"sənəd.{extension}";
        }

        return candidate.Length > 200 ? candidate[..200] : candidate;
    }

    private static async Task<int> ReadAtMostAsync(
        Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await source.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return read;
    }

    internal static FileObject ToFileObject(StoredObject stored) => new(
        stored.ProviderId,
        stored.Key,
        stored.Sha256,
        stored.EncryptedLength,
        stored.PlaintextLength,
        stored.KekId);

    internal static UserFileResult Map(StoredFile file) => new(
        file.Id,
        file.FileName,
        file.Extension,
        file.MediaType,
        file.Source.ToString(),
        file.Status.ToString(),
        file.FailureReason,
        file.Original.PlaintextLength,
        file.PageCount,
        file.IsSensitive,
        file.AutoDelete.ToString(),
        file.Versions.Count,
        file.CreatedAt);

    private static long Megabytes(long bytes) => bytes / (1024 * 1024);

    private static ValidationException Invalid(string field, string message)
        => new([new ValidationError(field, message)]);
}

/// <summary>
/// A stream that replays bytes already read, then continues with the rest.
/// </summary>
/// <remarks>
/// The format has to be decided from the first block, and a request body
/// cannot be rewound. Buffering the whole upload to get a seekable stream
/// would put a 50 MB file in memory per request, so only the block that was
/// consumed is held.
/// </remarks>
internal sealed class PrefixedStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _rest;
    private int _prefixPosition;

    public PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest)
    {
        _prefix = prefix;
        _rest = rest;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
            _prefix.Slice(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;

            return take;
        }

        return await _rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Lists a user's files, newest first.</summary>
public sealed class ListFilesHandler : IRequestHandler<ListFilesQuery, FileListResult>
{
    private readonly IFileRepository _files;

    public ListFilesHandler(IFileRepository files) => _files = files;

    public async Task<FileListResult> Handle(ListFilesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var items = await _files
            .ListAsync(request.UserId, (page - 1) * pageSize, pageSize, cancellationToken)
            .ConfigureAwait(false);

        var total = await _files.CountAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        return new FileListResult([.. items.Select(UploadFileHandler.Map)], total);
    }
}

/// <summary>One file's metadata, for its owner only.</summary>
public sealed class GetFileHandler : IRequestHandler<GetFileQuery, UserFileResult>
{
    private readonly IFileRepository _files;

    public GetFileHandler(IFileRepository files) => _files = files;

    public async Task<UserFileResult> Handle(GetFileQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await FileAccess
            .LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        return UploadFileHandler.Map(file);
    }
}

/// <summary>Changes a file's auto-delete rule.</summary>
public sealed class SetAutoDeleteHandler : IRequestHandler<SetAutoDeleteCommand, UserFileResult>
{
    private readonly IFileRepository _files;

    public SetAutoDeleteHandler(IFileRepository files) => _files = files;

    public async Task<UserFileResult> Handle(SetAutoDeleteCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await FileAccess
            .LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        file.AutoDelete = request.Rule;

        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        return UploadFileHandler.Map(file);
    }
}

/// <summary>Deletes a file and everything stored for it.</summary>
public sealed class DeleteFileHandler : IRequestHandler<DeleteFileCommand>
{
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly IClock _clock;
    private readonly ILogger<DeleteFileHandler> _logger;

    public DeleteFileHandler(
        IFileRepository files,
        IFileStorage storage,
        IClock clock,
        ILogger<DeleteFileHandler> logger)
    {
        _files = files;
        _storage = storage;
        _clock = clock;
        _logger = logger;
    }

    public async Task Handle(DeleteFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await FileAccess
            .LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        // Every object the file owns, not only the original: the converted
        // PDF and the saved versions are the same bytes to somebody who finds
        // them (plan section 2.13).
        foreach (var stored in Objects(file))
        {
            await _storage.DeleteAsync(
                    new StoredObject(
                        stored.ProviderId,
                        stored.Key,
                        stored.Sha256,
                        stored.EncryptedLength,
                        stored.PlaintextLength,
                        stored.KekId),
                    StorageBucket.Files,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        file.DeletedAt = _clock.UtcNow;

        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("File {FileId} was deleted by its owner.", file.Id);
    }

    private static IEnumerable<FileObject> Objects(StoredFile file)
    {
        yield return file.Original;

        if (file.Pdf is not null && file.Pdf.Key != file.Original.Key)
        {
            yield return file.Pdf;
        }

        foreach (var version in file.Versions)
        {
            yield return version.Object;
        }
    }
}

/// <summary>Loading a file as the person who owns it.</summary>
internal static class FileAccess
{
    /// <summary>
    /// The file, or a not-found error.
    /// </summary>
    /// <remarks>
    /// Somebody else's file answers "not found" rather than "forbidden": a
    /// 403 would confirm that the id exists, which is enough to enumerate
    /// them.
    /// </remarks>
    public static async Task<StoredFile> LoadOwnedAsync(
        IFileRepository files, Guid userId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false);

        if (file is null || file.UserId != userId || file.DeletedAt is not null)
        {
            throw new NotFoundException("Fayl tapılmadı.");
        }

        return file;
    }
}
