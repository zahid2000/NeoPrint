using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Notifications;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Notifications;

namespace NeoPrint.Application.Files;

/// <summary>
/// Removes a file for good: every object it owns - the original, the converted
/// PDF, the saved versions - from every storage provider, then marks it deleted
/// (plan section 2.13). The user's own delete, the auto-delete and the account
/// deletion all go through here.
/// </summary>
/// <remarks>
/// Retry-safe: a provider that no longer has an object is not an error, and the
/// file is marked deleted only after its objects are gone, so a run that fails
/// half-way is repeated in full.
/// </remarks>
public sealed class FileEraser
{
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly IClock _clock;

    public FileEraser(IFileRepository files, IFileStorage storage, IClock clock)
    {
        _files = files;
        _storage = storage;
        _clock = clock;
    }

    public async Task EraseAsync(StoredFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        foreach (var stored in file.AllObjects())
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

        file.DeletedAt ??= _clock.UtcNow;
        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
    }

}

/// <summary>An order printed (plan section 6, <c>PrintJobCompleted</c>): its file notes it and may delete itself.</summary>
public sealed record NoteFilePrintedCommand(Guid JobId) : IRequest;

/// <summary>Deletes one file whose own rule says so, unless something still needs it.</summary>
public sealed record AutoDeleteFileCommand(Guid FileId) : IRequest<bool>;

/// <summary>Every five minutes (plan section 6, <c>FileAutoDeleteJob</c>): the files whose time came.</summary>
public sealed record RunFileAutoDeleteCommand : IRequest<int>;

public sealed class FileAutoDeleteHandlers :
    IRequestHandler<NoteFilePrintedCommand>,
    IRequestHandler<AutoDeleteFileCommand, bool>,
    IRequestHandler<RunFileAutoDeleteCommand, int>
{
    private const int Batch = 100;

    private readonly IFileRepository _files;
    private readonly PrintJobs.IPrintJobRepository _jobs;
    private readonly Schedules.IScheduledPrintRepository _schedules;
    private readonly FileEraser _eraser;
    private readonly ISender _sender;
    private readonly IClock _clock;
    private readonly ILogger<FileAutoDeleteHandlers> _logger;

    public FileAutoDeleteHandlers(
        IFileRepository files,
        PrintJobs.IPrintJobRepository jobs,
        Schedules.IScheduledPrintRepository schedules,
        FileEraser eraser,
        ISender sender,
        IClock clock,
        ILogger<FileAutoDeleteHandlers> logger)
    {
        _files = files;
        _jobs = jobs;
        _schedules = schedules;
        _eraser = eraser;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    public async Task Handle(NoteFilePrintedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An anonymous kiosk order has no stored file (plan section 2.2).
        if (await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false) is not { FileId: { } fileId } job
            || await _files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false) is not { DeletedAt: null } file)
        {
            return;
        }

        file.MarkPrinted(_clock.UtcNow);
        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        await _sender.Send(new AutoDeleteFileCommand(file.Id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> Handle(AutoDeleteFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await _files.FindByIdAsync(request.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null || !file.AutoDeleteDue(_clock.UtcNow))
        {
            return false;
        }

        // Another order or a scheduled print still prints it: after the last one
        // (plan section 2.13). The five-minute run tries again.
        if (await _jobs.HasOpenOrderForFileAsync(file.Id, cancellationToken).ConfigureAwait(false)
            || await _schedules.HasActiveForFileAsync(file.Id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _eraser.EraseAsync(file, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("File {FileId} deleted by its own rule ({Rule}).", file.Id, file.AutoDelete);

        await _sender
            .Send(
                new NotifyUserCommand(
                    file.UserId,
                    NotificationType.FileAutoDeleted,
                    UserMessage.Of("notifications.fileAutoDeleted.title"),
                    UserMessage.Of(
                        "notifications.fileAutoDeleted.body",
                        ("name", file.FileName),
                        ("rule", file.AutoDelete.ToString())),
                    NotificationLinks.Files,
                    $"FileAutoDeleted:{file.Id}"),
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    public async Task<int> Handle(RunFileAutoDeleteCommand request, CancellationToken cancellationToken)
    {
        var deleted = 0;

        foreach (var id in await _files.ListAutoDeleteDueAsync(_clock.UtcNow, Batch, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await _sender.Send(new AutoDeleteFileCommand(id), cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One file a provider would not let go of is not every file's problem; it is tried next run.
                _logger.LogWarning(exception, "Auto-delete of file {FileId} failed; it is tried again.", id);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("{Count} file(s) deleted by their own rule.", deleted);
        }

        return deleted;
    }
}
