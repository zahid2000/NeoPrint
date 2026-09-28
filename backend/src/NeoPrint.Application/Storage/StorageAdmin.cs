using System.Text.Json;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;

namespace NeoPrint.Application.Storage;

// ---------------------------------------------------------------------------
// What the panel sees
// ---------------------------------------------------------------------------

/// <summary>The three bucket names of a provider.</summary>
public sealed record StorageBucketsDto(string Files, string Receipts, string Temp);

/// <summary>
/// A provider as the panel shows it (plan section 2.10).
/// </summary>
/// <remarks>
/// The secret key never leaves the server, not even encrypted. The access key
/// is shown as its last four characters, which is enough to tell two sets of
/// credentials apart and not enough to use either.
/// </remarks>
public sealed record StorageProviderResponse(
    Guid Id,
    string Name,
    string Type,
    string Endpoint,
    string Region,
    bool UseSsl,
    string AccessKeyHint,
    StorageBucketsDto Buckets,
    string Role,
    string HealthStatus,
    string? HealthMessage,
    DateTime? LastCheckedAt,
    bool IsVerified);

/// <summary>What a connection test found, bucket by bucket.</summary>
public sealed record StorageTestResponse(
    bool Succeeded,
    string Message,
    IReadOnlyList<StorageBucketTestDto> Buckets,
    long ElapsedMilliseconds);

public sealed record StorageBucketTestDto(string Bucket, bool Succeeded, string? Problem);

/// <summary>A sync job, for the list and the wizard's progress view.</summary>
/// <param name="SourcePurgeAllowedFrom">
/// When the source of a completed migration may be emptied
/// (<c>Storage.SourceRetentionDays</c> after completion); null otherwise.
/// </param>
/// <param name="SourcePurgeRequestedAt">When an administrator approved emptying the source.</param>
/// <param name="SourcePurgedAt">When emptying the source finished.</param>
/// <param name="SourcePurgedObjects">Objects deleted from the source.</param>
/// <param name="SourceKeptObjects">Objects kept on the source because they were still needed.</param>
public sealed record StorageSyncJobResponse(
    Guid Id,
    string Mode,
    Guid SourceId,
    string SourceName,
    Guid TargetId,
    string TargetName,
    string Status,
    long TotalObjects,
    long CopiedObjects,
    long FailedObjects,
    long TotalBytes,
    long CopiedBytes,
    string? CheckpointBucket,
    bool SwitchPrimaryOnComplete,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? FailureReason,
    DateTime? SourcePurgeAllowedFrom,
    DateTime? SourcePurgeRequestedAt,
    DateTime? SourcePurgedAt,
    long SourcePurgedObjects,
    long SourceKeptObjects);

/// <summary>One object a job could not copy.</summary>
public sealed record StorageSyncFailureResponse(
    string Bucket, string Key, string Error, int Attempts, DateTime LastAttemptAt, DateTime? ResolvedAt);

/// <summary>A provider's settings, as entered on the form.</summary>
/// <param name="SecretKey">
/// Required when adding. When editing, empty means "keep the one stored" -
/// the form never receives the stored value to send back.
/// </param>
public sealed record StorageProviderInput(
    string Name,
    string Type,
    string Endpoint,
    string? Region,
    bool UseSsl,
    string AccessKey,
    string? SecretKey,
    StorageBucketsDto Buckets);

// ---------------------------------------------------------------------------
// The operations
// ---------------------------------------------------------------------------

/// <summary>
/// Managing storage providers and sync jobs (plan section 2.10,
/// <c>storage.manage</c>).
/// </summary>
/// <remarks>
/// Implemented in Infrastructure, where the S3 clients, the secret protector
/// and the background scheduler live; the handlers below add auditing and the
/// transaction around it.
/// </remarks>
public interface IStorageAdministration
{
    Task<IReadOnlyList<StorageProviderResponse>> ListProvidersAsync(CancellationToken cancellationToken);

    /// <summary>Tests credentials before anything is saved.</summary>
    Task<StorageTestResponse> TestAsync(StorageProviderInput input, CancellationToken cancellationToken);

    /// <summary>Tests a saved provider and records the result.</summary>
    Task<StorageTestResponse> TestAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a provider. Refused unless its connection test passes: an
    /// untested provider cannot be given a role, and one nobody can use is
    /// only clutter.
    /// </summary>
    Task<StorageProviderResponse> AddProviderAsync(StorageProviderInput input, CancellationToken cancellationToken);

    Task<StorageProviderResponse> UpdateProviderAsync(
        Guid providerId, StorageProviderInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a provider primary, secondary or inactive, moving whoever held
    /// that role out of it so there is only ever one of each.
    /// </summary>
    Task<StorageProviderResponse> SetRoleAsync(Guid providerId, string role, CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageSyncJobResponse>> ListJobsAsync(CancellationToken cancellationToken);

    Task<StorageSyncJobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a migration or a mirror. One active job at a time
    /// (plan section 2.10).
    /// </summary>
    Task<StorageSyncJobResponse> StartJobAsync(
        string mode, Guid targetId, bool switchPrimaryOnComplete, CancellationToken cancellationToken);

    Task<StorageSyncJobResponse> PauseJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task<StorageSyncJobResponse> ResumeJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task<StorageSyncJobResponse> CancelJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Sends a failed job back through verification, which copies what is still missing.</summary>
    Task<StorageSyncJobResponse> RetryFailuresAsync(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageSyncFailureResponse>> ListFailuresAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Approves deleting the migrated objects from the source of a completed
    /// migration, once <c>Storage.SourceRetentionDays</c> have passed
    /// (plan section 2.10). Runs in the background.
    /// </summary>
    Task<StorageSyncJobResponse> RequestSourcePurgeAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an inactive provider no file and no running job refers to.
    /// The objects in it are not touched.
    /// </summary>
    Task DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a key rewrap over every provider and bucket, after a key
    /// rotation (plan section 2.13).
    /// </summary>
    Task StartRewrapAsync(CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

public sealed record ListStorageProvidersQuery : IRequest<IReadOnlyList<StorageProviderResponse>>;

public sealed record TestStorageCredentialsCommand(StorageProviderInput Input) : IRequest<StorageTestResponse>;

public sealed record TestStorageProviderCommand(Guid ProviderId, Guid? AdminUserId, string? Ip)
    : IRequest<StorageTestResponse>;

public sealed record AddStorageProviderCommand(StorageProviderInput Input, Guid? AdminUserId, string? Ip)
    : IRequest<StorageProviderResponse>, ITransactionalRequest;

public sealed record UpdateStorageProviderCommand(
    Guid ProviderId, StorageProviderInput Input, Guid? AdminUserId, string? Ip)
    : IRequest<StorageProviderResponse>, ITransactionalRequest;

public sealed record SetStorageProviderRoleCommand(Guid ProviderId, string Role, Guid? AdminUserId, string? Ip)
    : IRequest<StorageProviderResponse>, ITransactionalRequest;

public sealed record DeleteStorageProviderCommand(Guid ProviderId, Guid? AdminUserId, string? Ip)
    : IRequest, ITransactionalRequest;

public sealed record ListStorageSyncJobsQuery : IRequest<IReadOnlyList<StorageSyncJobResponse>>;

public sealed record GetStorageSyncJobQuery(Guid JobId) : IRequest<StorageSyncJobResponse>;

public sealed record StartStorageSyncCommand(
    string Mode, Guid TargetId, bool SwitchPrimaryOnComplete, Guid? AdminUserId, string? Ip)
    : IRequest<StorageSyncJobResponse>, ITransactionalRequest;

/// <param name="Action">pause, resume, cancel or retry-failed.</param>
public sealed record ControlStorageSyncCommand(Guid JobId, string Action, Guid? AdminUserId, string? Ip)
    : IRequest<StorageSyncJobResponse>, ITransactionalRequest;

public sealed record ListStorageSyncFailuresQuery(Guid JobId) : IRequest<IReadOnlyList<StorageSyncFailureResponse>>;

public sealed record PurgeStorageSourceCommand(Guid JobId, Guid? AdminUserId, string? Ip)
    : IRequest<StorageSyncJobResponse>, ITransactionalRequest;

public sealed record StartKeyRewrapCommand(Guid? AdminUserId, string? Ip) : IRequest;

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

/// <summary>
/// Storage administration, with an audit entry for everything that changes
/// something (plan section 8A.1).
/// </summary>
public sealed class StorageAdminHandlers :
    IRequestHandler<ListStorageProvidersQuery, IReadOnlyList<StorageProviderResponse>>,
    IRequestHandler<TestStorageCredentialsCommand, StorageTestResponse>,
    IRequestHandler<TestStorageProviderCommand, StorageTestResponse>,
    IRequestHandler<AddStorageProviderCommand, StorageProviderResponse>,
    IRequestHandler<UpdateStorageProviderCommand, StorageProviderResponse>,
    IRequestHandler<SetStorageProviderRoleCommand, StorageProviderResponse>,
    IRequestHandler<DeleteStorageProviderCommand>,
    IRequestHandler<ListStorageSyncJobsQuery, IReadOnlyList<StorageSyncJobResponse>>,
    IRequestHandler<GetStorageSyncJobQuery, StorageSyncJobResponse>,
    IRequestHandler<StartStorageSyncCommand, StorageSyncJobResponse>,
    IRequestHandler<ControlStorageSyncCommand, StorageSyncJobResponse>,
    IRequestHandler<ListStorageSyncFailuresQuery, IReadOnlyList<StorageSyncFailureResponse>>,
    IRequestHandler<PurgeStorageSourceCommand, StorageSyncJobResponse>,
    IRequestHandler<StartKeyRewrapCommand>
{
    private readonly IStorageAdministration _storage;
    private readonly IAuditWriter _audit;

    public StorageAdminHandlers(IStorageAdministration storage, IAuditWriter audit)
    {
        _storage = storage;
        _audit = audit;
    }

    public Task<IReadOnlyList<StorageProviderResponse>> Handle(
        ListStorageProvidersQuery request, CancellationToken cancellationToken)
        => _storage.ListProvidersAsync(cancellationToken);

    public Task<StorageTestResponse> Handle(TestStorageCredentialsCommand request, CancellationToken cancellationToken)
        => _storage.TestAsync(request.Input, cancellationToken);

    public async Task<StorageTestResponse> Handle(TestStorageProviderCommand request, CancellationToken cancellationToken)
    {
        var result = await _storage.TestAsync(request.ProviderId, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.provider.test", "StorageProvider", request.ProviderId.ToString(),
                null, new { result.Succeeded, result.Message }, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    public async Task<StorageProviderResponse> Handle(AddStorageProviderCommand request, CancellationToken cancellationToken)
    {
        var added = await _storage.AddProviderAsync(request.Input, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.provider.add", "StorageProvider", added.Id.ToString(),
                null, added, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return added;
    }

    public async Task<StorageProviderResponse> Handle(UpdateStorageProviderCommand request, CancellationToken cancellationToken)
    {
        var before = (await _storage.ListProvidersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id == request.ProviderId);

        var updated = await _storage.UpdateProviderAsync(request.ProviderId, request.Input, cancellationToken)
            .ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.provider.update", "StorageProvider", updated.Id.ToString(),
                before, updated, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return updated;
    }

    public async Task<StorageProviderResponse> Handle(SetStorageProviderRoleCommand request, CancellationToken cancellationToken)
    {
        var before = (await _storage.ListProvidersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id == request.ProviderId);

        var updated = await _storage.SetRoleAsync(request.ProviderId, request.Role, cancellationToken)
            .ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.provider.role", "StorageProvider", updated.Id.ToString(),
                new { before?.Role }, new { updated.Role }, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return updated;
    }

    public async Task Handle(DeleteStorageProviderCommand request, CancellationToken cancellationToken)
    {
        var before = (await _storage.ListProvidersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id == request.ProviderId);

        await _storage.DeleteProviderAsync(request.ProviderId, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.provider.delete", "StorageProvider", request.ProviderId.ToString(),
                before, null, request.Ip, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StorageSyncJobResponse> Handle(PurgeStorageSourceCommand request, CancellationToken cancellationToken)
    {
        var job = await _storage.RequestSourcePurgeAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.sync.purge-source", "StorageSyncJob", job.Id.ToString(),
                null, new { job.SourceId, job.SourceName, job.SourcePurgeRequestedAt }, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return job;
    }

    public Task<IReadOnlyList<StorageSyncJobResponse>> Handle(ListStorageSyncJobsQuery request, CancellationToken cancellationToken)
        => _storage.ListJobsAsync(cancellationToken);

    public Task<StorageSyncJobResponse> Handle(GetStorageSyncJobQuery request, CancellationToken cancellationToken)
        => _storage.GetJobAsync(request.JobId, cancellationToken);

    public async Task<StorageSyncJobResponse> Handle(StartStorageSyncCommand request, CancellationToken cancellationToken)
    {
        var job = await _storage
            .StartJobAsync(request.Mode, request.TargetId, request.SwitchPrimaryOnComplete, cancellationToken)
            .ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.sync.start", "StorageSyncJob", job.Id.ToString(),
                null, job, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return job;
    }

    public async Task<StorageSyncJobResponse> Handle(ControlStorageSyncCommand request, CancellationToken cancellationToken)
    {
        var job = request.Action switch
        {
            "pause" => await _storage.PauseJobAsync(request.JobId, cancellationToken).ConfigureAwait(false),
            "resume" => await _storage.ResumeJobAsync(request.JobId, cancellationToken).ConfigureAwait(false),
            "cancel" => await _storage.CancelJobAsync(request.JobId, cancellationToken).ConfigureAwait(false),
            "retry-failed" => await _storage.RetryFailuresAsync(request.JobId, cancellationToken).ConfigureAwait(false),
            _ => throw new Common.ValidationException(
                [new Common.ValidationError("action", "storage.actionUnknown")]),
        };

        await AuditAsync(
                request.AdminUserId, $"storage.sync.{request.Action}", "StorageSyncJob", job.Id.ToString(),
                null, new { job.Status }, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return job;
    }

    public Task<IReadOnlyList<StorageSyncFailureResponse>> Handle(
        ListStorageSyncFailuresQuery request, CancellationToken cancellationToken)
        => _storage.ListFailuresAsync(request.JobId, cancellationToken);

    public async Task Handle(StartKeyRewrapCommand request, CancellationToken cancellationToken)
    {
        await _storage.StartRewrapAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
                request.AdminUserId, "storage.rewrap.start", "Storage", null,
                null, null, request.Ip, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task AuditAsync(
        Guid? adminUserId,
        string action,
        string entityType,
        string? entityId,
        object? before,
        object? after,
        string? ip,
        CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(
                adminUserId,
                action,
                entityType,
                entityId,
                before is null ? null : JsonSerializer.Serialize(before),
                after is null ? null : JsonSerializer.Serialize(after),
                ip,
                Reason: null),
            cancellationToken);
}
