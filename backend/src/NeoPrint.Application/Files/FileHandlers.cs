using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Contracts;
using NeoPrint.Documents;
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
    private readonly FileIntake _intake;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<UploadFileHandler> _logger;

    public UploadFileHandler(
        IFileRepository files,
        IFileStorage storage,
        FileIntake intake,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        ILogger<UploadFileHandler> logger)
    {
        _files = files;
        _storage = storage;
        _intake = intake;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task<UserFileResult> Handle(UploadFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A scan, a layout and a shared copy each have their own way in, with
        // their own checks (the scan notice, the layout's pictures, the
        // sender); an upload that merely claims to be one is refused.
        if (request.Source is not (FileSource.Uploaded or FileSource.Usb))
        {
            throw FileIntake.Invalid("source", "files.sourceNotAllowed");
        }

        var maxBytes = await _intake.MaxBytesAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        FileIntake.EnsureWithin(request.DeclaredLength, maxBytes);

        await _intake.EnsureQuotaAsync(request.UserId, request.DeclaredLength, cancellationToken)
            .ConfigureAwait(false);

        // The first block decides what the file is. Buffered here rather than
        // seeking, because the request body is not seekable.
        var header = new byte[ContentSignature.HeaderLength];
        var headerLength = await ReadAtMostAsync(request.Content, header, cancellationToken)
            .ConfigureAwait(false);

        var signature = ContentSignature.Identify(header.AsSpan(0, headerLength), request.FileName);

        if (!signature.Recognised)
        {
            throw FileIntake.Invalid("file", signature.Reason ?? "files.signature.unknown");
        }

        var format = signature.Format!;

        await EnsureFormatEnabledAsync(format, cancellationToken).ConfigureAwait(false);

        var file = new StoredFile
        {
            UserId = request.UserId,
            FileName = FileIntake.SafeName(request.FileName, format.Extension),
            Extension = format.Extension,
            MediaType = format.MediaType,
            Source = request.Source,
            IsSensitive = request.IsSensitive,
            AutoDelete = await _intake.DefaultAutoDeleteAsync(request.IsSensitive, cancellationToken)
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

            FileIntake.EnsureWithin(stored.PlaintextLength, maxBytes);
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
            throw FileIntake.Invalid("file", "files.formatDisabled", ("format", format.Extension));
        }
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
        Messages.WriteStored(file.FailureReason),
        file.Original.PlaintextLength,
        file.PageCount,
        file.IsSensitive,
        file.AutoDelete.ToString(),
        file.Versions.Count,
        file.CreatedAt,
        file.EditLocked,
        file.LayoutProjectJson is not null,
        file.DocumentType,
        file.PhysicalWidthMm,
        file.PhysicalHeightMm,
        file.RealSizePaper);
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
    private readonly PrintJobs.IPrintJobRepository _jobs;
    private readonly Schedules.IScheduledPrintRepository _schedules;
    private readonly IFileRepository _files;
    private readonly FileEraser _eraser;
    private readonly ILogger<DeleteFileHandler> _logger;

    public DeleteFileHandler(
        IFileRepository files,
        FileEraser eraser,
        ILogger<DeleteFileHandler> logger,
        PrintJobs.IPrintJobRepository jobs,
        Schedules.IScheduledPrintRepository schedules)
    {
        _jobs = jobs;
        _schedules = schedules;
        _files = files;
        _eraser = eraser;
        _logger = logger;
    }

    public async Task Handle(DeleteFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await FileAccess
            .LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        // A scheduled print is told apart: it is cancelled first, in its own list (docs/DECISIONS.md T-145).
        if (await _schedules.HasActiveForFileAsync(file.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("files.inUseBySchedule");
        }

        // The kiosk still has to fetch it for an order that may print.
        if (await _jobs.HasOpenOrderForFileAsync(file.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("files.inUseByOrder");
        }

        // Every object the file owns, not only the original: the converted
        // PDF and the saved versions are the same bytes to somebody who finds
        // them (plan section 2.13).
        await _eraser.EraseAsync(file, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("File {FileId} was deleted by its owner.", file.Id);
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
            throw new NotFoundException("file");
        }

        return file;
    }
}
