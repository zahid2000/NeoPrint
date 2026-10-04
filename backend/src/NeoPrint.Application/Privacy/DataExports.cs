using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Notifications;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.Privacy;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Privacy;

/// <summary>Persistence for <see cref="DataExport"/> (<c>data_exports</c>).</summary>
public interface IDataExportRepository
{
    Task<DataExport?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DataExport?> FindLatestAsync(Guid userId, CancellationToken cancellationToken = default);

    Task InsertAsync(DataExport export, CancellationToken cancellationToken = default);

    Task ReplaceAsync(DataExport export, CancellationToken cancellationToken = default);

    /// <summary>Ready archives past their time, oldest first.</summary>
    Task<IReadOnlyList<DataExport>> ListExpiredAsync(DateTime now, int take, CancellationToken cancellationToken = default);

    /// <summary>Every export of a user, for the account deletion.</summary>
    Task<IReadOnlyList<DataExport>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>What went into an archive.</summary>
/// <param name="IncludesFiles">False when the files were over the limit and only listed.</param>
public sealed record DataExportContents(bool IncludesFiles);

/// <summary>
/// Writes a user's archive as a ZIP (plan section 2.13): profile, payments,
/// refunds, balance, orders, receipts as PDF, files - originals and versions,
/// or only their list when they are over <paramref name="maxFileBytes"/>.
/// </summary>
public interface IDataExportBuilder
{
    Task<DataExportContents> BuildAsync(
        Guid userId, string language, long maxFileBytes, Stream destination, CancellationToken cancellationToken);
}

/// <summary>
/// The download link of a ready archive: signed, good until the archive
/// expires, so the system browser can fetch it without the app's session.
/// </summary>
public interface IExportLinkSigner
{
    string Sign(Guid exportId, DateTime expiresAt);

    /// <summary>The export the token is for, or null when it is forged or past its time.</summary>
    Guid? Verify(string token, DateTime now);
}

/// <summary><c>POST users/me/export</c>: an archive is made; a current one is returned instead of a second.</summary>
public sealed record RequestDataExportCommand(Guid UserId) : IRequest<DataExportView>;

/// <summary><c>GET users/me/export</c>: the latest archive, with its link when ready.</summary>
public sealed record GetDataExportQuery(Guid UserId) : IRequest<DataExportView?>;

/// <summary>The archive is made (the bus, after <see cref="RequestDataExportCommand"/>).</summary>
public sealed record PrepareDataExportCommand(Guid ExportId) : IRequest;

/// <summary><c>GET exports/download?token=…</c>: the ready archive the token is for, or null.</summary>
public sealed record ResolveExportDownloadQuery(string Token) : IRequest<DataExport?>;

/// <summary>Every hour: archives past their 24 hours are deleted.</summary>
public sealed record ExpireDataExportsCommand : IRequest<int>;

/// <param name="DownloadUrl">Relative to the API (<c>api/exports/download?token=…</c>); only while ready.</param>
/// <param name="FailureReason">In the request's language.</param>
public sealed record DataExportView(
    Guid Id,
    string Status,
    DateTime RequestedAt,
    DateTime? ReadyAt,
    DateTime? ExpiresAt,
    long SizeBytes,
    bool IncludesFiles,
    string? DownloadUrl,
    string? FailureReason);

public sealed class DataExportHandlers :
    IRequestHandler<RequestDataExportCommand, DataExportView>,
    IRequestHandler<GetDataExportQuery, DataExportView?>,
    IRequestHandler<PrepareDataExportCommand>,
    IRequestHandler<ResolveExportDownloadQuery, DataExport?>,
    IRequestHandler<ExpireDataExportsCommand, int>
{
    private const int Batch = 50;

    private readonly IDataExportRepository _exports;
    private readonly IUserRepository _users;
    private readonly IDataExportBuilder _builder;
    private readonly IExportLinkSigner _links;
    private readonly IFileStorage _storage;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly ISender _sender;
    private readonly IClock _clock;
    private readonly ILogger<DataExportHandlers> _logger;

    public DataExportHandlers(
        IDataExportRepository exports,
        IUserRepository users,
        IDataExportBuilder builder,
        IExportLinkSigner links,
        IFileStorage storage,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        ISender sender,
        IClock clock,
        ILogger<DataExportHandlers> logger)
    {
        _exports = exports;
        _users = users;
        _builder = builder;
        _links = links;
        _storage = storage;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DataExportView> Handle(RequestDataExportCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        if (await _exports.FindLatestAsync(request.UserId, cancellationToken).ConfigureAwait(false) is { } latest && latest.IsCurrent(now))
        {
            return View(latest);
        }

        var export = DataExport.Request(request.UserId, now);
        await _exports.InsertAsync(export, cancellationToken).ConfigureAwait(false);
        await _events
            .PublishAsync(new Contracts.DataExportRequested(export.Id, export.UserId, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Data export {ExportId} requested by user {UserId}.", export.Id, export.UserId);
        return View(export);
    }

    public async Task<DataExportView?> Handle(GetDataExportQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var latest = await _exports.FindLatestAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        return latest is null ? null : View(latest);
    }

    public async Task Handle(PrepareDataExportCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var export = await _exports.FindByIdAsync(request.ExportId, cancellationToken).ConfigureAwait(false);
        if (export is not { Status: DataExportStatus.Preparing })
        {
            return;
        }

        var user = await _users.FindByIdAsync(export.UserId, cancellationToken).ConfigureAwait(false);
        var language = Messages.Supported(user?.Language) ?? Messages.DefaultLanguage;
        var maxMegabytes = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.PrivacyExportMaxMegabytes, 500, cancellationToken).ConfigureAwait(false), 1, 10_000);

        var path = Path.Combine(Path.GetTempPath(), $"neoprint-export-{export.Id:N}.zip");
        try
        {
            DataExportContents contents;
            await using (var archive = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                contents = await _builder
                    .BuildAsync(export.UserId, language, maxMegabytes * 1024L * 1024L, archive, cancellationToken)
                    .ConfigureAwait(false);
            }

            StoredObject stored;
            await using (var archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                stored = await _storage
                    .WriteAsync(StorageBucket.Temp, $"exports/{export.UserId:N}/{export.Id:N}.zip", archive, "application/zip", cancellationToken)
                    .ConfigureAwait(false);
            }

            export.MarkReady(
                new FileObject(stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
                stored.PlaintextLength,
                contents.IncludesFiles,
                _clock.UtcNow);
            await _exports.ReplaceAsync(export, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Data export {ExportId} ready: {Bytes} bytes, files included: {IncludesFiles}.", export.Id, stored.PlaintextLength, contents.IncludesFiles);

            await _sender
                .Send(
                    new NotifyUserCommand(
                        export.UserId,
                        NotificationType.ExportReady,
                        UserMessage.Of("notifications.exportReady.title"),
                        UserMessage.Of("notifications.exportReady.body"),
                        NotificationLinks.Export,
                        $"ExportReady:{export.Id}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Said to the user, who asks again; not retried by the bus into a second archive.
            _logger.LogError(exception, "Data export {ExportId} could not be made.", export.Id);
            export.MarkFailed(UserMessage.Of("privacy.exportFailed"), _clock.UtcNow);
            await _exports.ReplaceAsync(export, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public async Task<DataExport?> Handle(ResolveExportDownloadQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        if (_links.Verify(request.Token ?? string.Empty, now) is not { } id)
        {
            return null;
        }

        var export = await _exports.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return export is { Status: DataExportStatus.Ready, Archive: not null } && now < export.ExpiresAt ? export : null;
    }

    public async Task<int> Handle(ExpireDataExportsCommand request, CancellationToken cancellationToken)
    {
        var expired = 0;
        var now = _clock.UtcNow;

        foreach (var export in await _exports.ListExpiredAsync(now, Batch, cancellationToken).ConfigureAwait(false))
        {
            if (export.Archive is { } archive)
            {
                await _storage.DeleteAsync(Stored(archive), StorageBucket.Temp, cancellationToken).ConfigureAwait(false);
            }

            export.Expire(now);
            await _exports.ReplaceAsync(export, cancellationToken).ConfigureAwait(false);
            expired++;
        }

        if (expired > 0)
        {
            _logger.LogInformation("{Count} data export archive(s) past their 24 hours were deleted.", expired);
        }

        return expired;
    }

    /// <summary>The storage address of an archive.</summary>
    public static StoredObject Stored(FileObject archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        return new StoredObject(archive.ProviderId, archive.Key, archive.Sha256, archive.EncryptedLength, archive.PlaintextLength, archive.KekId);
    }

    private DataExportView View(DataExport export) => new(
        export.Id,
        export.Status.ToString(),
        export.CreatedAt,
        export.ReadyAt,
        export.ExpiresAt,
        export.SizeBytes,
        export.IncludesFiles,
        export.Status == DataExportStatus.Ready && export.ExpiresAt is { } until && _clock.UtcNow < until
            ? $"api/exports/download?token={Uri.EscapeDataString(_links.Sign(export.Id, until))}"
            : null,
        Messages.WriteStored(export.FailureReason));
}
