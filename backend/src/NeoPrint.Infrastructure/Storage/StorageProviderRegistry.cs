using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// The configured storage providers, cached in memory.
/// </summary>
/// <remarks>
/// <para>
/// Every read and write asks this which provider to use, so going to Mongo
/// each time would put a query in front of every object operation. The set
/// changes only when an administrator changes it, and
/// <see cref="Invalidate"/> is called when they do - directly on the instance
/// that made the change, and through the <c>StorageConfigChanged</c> event on
/// every other API and worker process, so no restart is needed
/// (plan section 2.10).
/// </para>
/// <para>
/// The cache holds the documents, not the S3 clients: building a client needs
/// the decrypted credentials, and those are not kept in memory longer than
/// the call that uses them.
/// </para>
/// </remarks>
public sealed class StorageProviderRegistry
{
    private readonly MongoContext _context;
    private readonly ILogger<StorageProviderRegistry> _logger;

    // System.Threading.Lock is .NET 9; this targets .NET 8.
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loading = new(1, 1);

    private IReadOnlyList<StorageProvider>? _providers;

    /// <summary>The active migrations, cached with the providers and dropped with them.</summary>
    private IReadOnlyList<StorageSyncJob>? _migrations;

    public StorageProviderRegistry(MongoContext context, ILogger<StorageProviderRegistry> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>Every configured provider, whatever its role.</summary>
    public async Task<IReadOnlyList<StorageProvider>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_providers is not null)
            {
                return _providers;
            }
        }

        // One loader at a time: a cold cache under load would otherwise send
        // the same query once per concurrent request.
        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_gate)
            {
                if (_providers is not null)
                {
                    return _providers;
                }
            }

            var loaded = await _context.StorageProviders
                .Find(FilterDefinition<StorageProvider>.Empty)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _providers = loaded;
                return _providers;
            }
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>
    /// The provider new objects are written to.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// None is configured. Deliberately an exception rather than a silent
    /// fallback: writing to an arbitrary provider would scatter objects across
    /// stores that nothing then knows to read from.
    /// </exception>
    public async Task<StorageProvider> GetPrimaryAsync(CancellationToken cancellationToken = default)
    {
        var providers = await GetAllAsync(cancellationToken).ConfigureAwait(false);

        return providers.FirstOrDefault(p => p.Role == StorageProviderRole.Primary)
            ?? throw new InvalidOperationException(
                "No storage provider is marked Primary, so there is nowhere to write. "
                + "Set one from the admin panel (plan section 2.10).");
    }

    /// <summary>The mirror target, or null when mirroring is off.</summary>
    public async Task<StorageProvider?> GetSecondaryAsync(CancellationToken cancellationToken = default)
    {
        var providers = await GetAllAsync(cancellationToken).ConfigureAwait(false);

        return providers.FirstOrDefault(p => p.Role == StorageProviderRole.Secondary);
    }

    /// <summary>
    /// A provider by id, including inactive ones.
    /// </summary>
    /// <remarks>
    /// Reads go to the provider the object was written to, and that provider
    /// may since have been deactivated. Excluding inactive providers here
    /// would make every older file unreadable the moment the primary changed.
    /// </remarks>
    public async Task<StorageProvider> GetByIdAsync(
        Guid providerId, CancellationToken cancellationToken = default)
    {
        var providers = await GetAllAsync(cancellationToken).ConfigureAwait(false);

        return providers.FirstOrDefault(p => p.Id == providerId)
            ?? throw new InvalidOperationException(
                $"Storage provider {providerId} is no longer configured, but objects still point at it. "
                + "Restore it, or migrate those objects before removing it.");
    }

    /// <summary>
    /// The provider a migration is copying the given primary to, or null.
    /// </summary>
    /// <remarks>
    /// While a migration is active - running, paused or verifying - new
    /// writes go to the target as well, so the copy cannot fall behind what is
    /// being uploaded (plan section 2.10). Paused counts: the uploads do not
    /// pause with it.
    /// </remarks>
    public async Task<StorageProvider?> GetMigrationTargetAsync(
        Guid primaryId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StorageSyncJob>? migrations;

        lock (_gate)
        {
            migrations = _migrations;
        }

        if (migrations is null)
        {
            var loaded = await _context.StorageSyncJobs
                .Find(job => job.Mode == StorageSyncMode.Migration
                             && (job.Status == StorageSyncStatus.Running
                                 || job.Status == StorageSyncStatus.Paused
                                 || job.Status == StorageSyncStatus.Verifying
                                 || job.Status == StorageSyncStatus.Pending))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _migrations = loaded;
                migrations = loaded;
            }
        }

        var active = migrations.FirstOrDefault(job => job.SourceId == primaryId);

        if (active is null)
        {
            return null;
        }

        var providers = await GetAllAsync(cancellationToken).ConfigureAwait(false);

        return providers.FirstOrDefault(provider => provider.Id == active.TargetId);
    }

    /// <summary>Drops the cache so the next call re-reads the collection.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _providers = null;
            _migrations = null;
        }

        _logger.LogInformation("The storage provider cache was invalidated.");
    }
}
