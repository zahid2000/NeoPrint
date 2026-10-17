using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Operations;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Operations;

/// <summary>An object as the store lists it.</summary>
public sealed record ListedObject(Guid ProviderId, string Key, long Size, DateTime LastModified);

/// <summary>Lists what the object stores hold - every provider - for the file cleanup.</summary>
public interface IStoredObjectLister
{
    /// <summary>The objects under <paramref name="prefix"/> in <paramref name="bucket"/>, provider by provider.</summary>
    IAsyncEnumerable<ListedObject> ListAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the documents say about a set of files: which objects are in use
/// and which may not be touched whatever happens.
/// </summary>
/// <param name="LiveKeys">Every object a file not deleted points at: original, PDF, versions, layout assets.</param>
/// <param name="ProtectedFiles">Files an open order, a refund, an active planned print or a "Kiosk üçün hazırla" holds.</param>
/// <param name="ProtectedKeys">Objects such an order prints from - an edit may have left the file pointing elsewhere.</param>
public sealed record FileReferences(IReadOnlySet<string> LiveKeys, IReadOnlySet<Guid> ProtectedFiles, IReadOnlySet<string> ProtectedKeys);

public interface IFileReferenceReader
{
    /// <param name="files">The files the listed objects belong to.</param>
    /// <param name="keys">The listed objects' keys.</param>
    Task<FileReferences> ReadAsync(IReadOnlyCollection<Guid> files, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);
}

/// <summary>What the cleanup does with one object.</summary>
public enum CleanupDecision
{
    /// <summary>A file not deleted points at it.</summary>
    Live = 0,

    /// <summary>Unused, but an order, a refund, a planned print or a prepared order holds its file: never deleted.</summary>
    Protected = 1,

    /// <summary>Unused, but younger than the grace period - a write may be on its way to the document.</summary>
    Recent = 2,

    /// <summary>Unused and old enough.</summary>
    Delete = 3,

    /// <summary>Not a user file's object (<c>files/{user}/{file}/...</c>): not this job's.</summary>
    Foreign = 4,
}

/// <summary>
/// <c>FileCleanupJob</c> (plan section 6, hourly; docs/DATA_RETENTION.md):
/// objects left behind under <c>files/</c> - a replaced PDF or a dropped
/// version whose immediate deletion failed, what a deleted file still had in
/// the store, an object whose document is gone.
/// </summary>
/// <param name="DryRun">Null: the <c>Files.CleanupDryRun</c> setting.</param>
/// <param name="GraceHours">Null: the <c>Files.CleanupGraceHours</c> setting.</param>
public sealed record RunFileCleanupCommand(bool? DryRun = null, int? GraceHours = null) : IRequest<JobRun>;

/// <summary>The decision for one object, alone: no store, no clock, no settings (T-215).</summary>
public static class FileCleanupPlanner
{
    /// <summary>The file a key belongs to: <c>files/{user}/{file}/...</c>; null for any other key.</summary>
    public static Guid? FileOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var parts = key.Split('/');

        return parts.Length >= 4 && parts[0] == "files" && Guid.TryParseExact(parts[1], "N", out _) && Guid.TryParseExact(parts[2], "N", out var file)
            ? file
            : null;
    }

    /// <remarks>
    /// In this order: what is in use stays; what an open order, a refund, a
    /// planned print or a prepared order holds stays, used or not; what was
    /// written within <paramref name="grace"/> stays (the document that will
    /// point at it may not be saved yet); the rest goes. An object exactly
    /// <paramref name="grace"/> old goes.
    /// </remarks>
    public static CleanupDecision Decide(ListedObject listed, FileReferences references, DateTime now, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(references);

        if (FileOf(listed.Key) is not { } file)
        {
            return CleanupDecision.Foreign;
        }

        if (references.LiveKeys.Contains(listed.Key))
        {
            return CleanupDecision.Live;
        }

        if (references.ProtectedFiles.Contains(file) || references.ProtectedKeys.Contains(listed.Key))
        {
            return CleanupDecision.Protected;
        }

        return listed.LastModified > now - grace ? CleanupDecision.Recent : CleanupDecision.Delete;
    }
}

/// <inheritdoc cref="RunFileCleanupCommand" />
/// <remarks>
/// Idempotent: a deleted object is no longer listed, so a run again finds
/// nothing more to do. Every deletion is in the audit log; the counts go to
/// the run record and the <c>NeoPrint</c> meter. A dry run deletes nothing
/// and writes what it would have deleted.
/// </remarks>
public sealed class RunFileCleanupHandler : IRequestHandler<RunFileCleanupCommand, JobRun>
{
    /// <summary>Objects looked at together: one read of the documents per page.</summary>
    public const int Page = 500;

    private readonly IStoredObjectLister _lister;
    private readonly IFileReferenceReader _references;
    private readonly IFileStorage _storage;
    private readonly IJobRunStore _runs;
    private readonly IAuditWriter _audit;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<RunFileCleanupHandler> _logger;

    public RunFileCleanupHandler(
        IStoredObjectLister lister,
        IFileReferenceReader references,
        IFileStorage storage,
        IJobRunStore runs,
        IAuditWriter audit,
        ISettingsReader settings,
        IClock clock,
        ILogger<RunFileCleanupHandler> logger)
    {
        _lister = lister;
        _references = references;
        _storage = storage;
        _runs = runs;
        _audit = audit;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<JobRun> Handle(RunFileCleanupCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var dryRun = request.DryRun ?? await _settings.GetBoolAsync(SettingKeys.FilesCleanupDryRun, true, cancellationToken).ConfigureAwait(false);
        var grace = TimeSpan.FromHours(Math.Max(0, request.GraceHours ?? await _settings.GetIntAsync(SettingKeys.FilesCleanupGraceHours, 24, cancellationToken).ConfigureAwait(false)));
        var run = new JobRun { Job = JobNames.FileCleanup, StartedAt = now, DryRun = dryRun };

        foreach (var name in new[] { "examined", "deleted", "deletedBytes", "protected", "recent", "failed" })
        {
            run.Counts[name] = 0;
        }

        try
        {
            var page = new List<ListedObject>(Page);

            await foreach (var listed in _lister.ListAsync(StorageBucket.Files, "files/", cancellationToken).ConfigureAwait(false))
            {
                page.Add(listed);

                if (page.Count == Page)
                {
                    await SweepAsync(page, run, now, grace, cancellationToken).ConfigureAwait(false);
                    page.Clear();
                }
            }

            await SweepAsync(page, run, now, grace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Succeeded = false;
            run.Error = exception.GetType().Name;
            _logger.LogError(exception, "The file cleanup stopped.");
        }

        run.FinishedAt = _clock.UtcNow;
        await _runs.AddAsync(run, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "File cleanup ({Mode}): {Examined} objects, {Deleted} {Verb} ({Bytes} bytes), {Protected} protected, {Recent} too recent, {Failed} failed.",
            dryRun ? "dry run" : "for real",
            run.Counts["examined"],
            run.Counts["deleted"],
            dryRun ? "would go" : "deleted",
            run.Counts["deletedBytes"],
            run.Counts["protected"],
            run.Counts["recent"],
            run.Counts["failed"]);

        return run;
    }

    private async Task SweepAsync(List<ListedObject> page, JobRun run, DateTime now, TimeSpan grace, CancellationToken cancellationToken)
    {
        if (page.Count == 0)
        {
            return;
        }

        var files = page.Select(listed => FileCleanupPlanner.FileOf(listed.Key)).OfType<Guid>().Distinct().ToList();
        var references = await _references.ReadAsync(files, [.. page.Select(listed => listed.Key)], cancellationToken).ConfigureAwait(false);

        foreach (var listed in page)
        {
            var decision = FileCleanupPlanner.Decide(listed, references, now, grace);

            if (decision == CleanupDecision.Foreign)
            {
                continue;
            }

            run.Count("examined");

            switch (decision)
            {
                case CleanupDecision.Protected:
                    run.Count("protected");
                    continue;
                case CleanupDecision.Recent:
                    run.Count("recent");
                    continue;
                case CleanupDecision.Live:
                    continue;
            }

            if (run.DryRun)
            {
                run.Count("deleted");
                run.Count("deletedBytes", listed.Size);
                run.Found(new JobFinding("wouldDelete", listed.Key, listed.Size));
                continue;
            }

            try
            {
                // Deletes from every provider that could hold it, as a user's own deletion does.
                await _storage
                    .DeleteAsync(new StoredObject(listed.ProviderId, listed.Key, string.Empty, listed.Size, listed.Size, string.Empty), StorageBucket.Files, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The next run tries again: the object is still listed.
                run.Count("failed");
                _logger.LogWarning(exception, "The file cleanup could not delete {Key}.", listed.Key);
                continue;
            }

            run.Count("deleted");
            run.Count("deletedBytes", listed.Size);
            run.Found(new JobFinding("deleted", listed.Key, listed.Size));
            NeoPrintMetrics.FilesCleanedUp.Add(1);
            NeoPrintMetrics.FilesCleanedUpBytes.Add(listed.Size);

            await _audit
                .WriteAsync(
                    new AuditEntry(null, "files.cleanup.deleted", "StoredFile", FileCleanupPlanner.FileOf(listed.Key)?.ToString(), listed.Key, null, null, Messages.Write("files.cleanup.unused"), ActorType.System),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
