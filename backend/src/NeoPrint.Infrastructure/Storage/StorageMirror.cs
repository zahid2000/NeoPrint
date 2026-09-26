using Amazon.S3;
using Amazon.S3.Model;

using MassTransit;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Contracts;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>What one reconciliation pass did.</summary>
/// <param name="Copied">Objects the secondary was missing, now copied.</param>
/// <param name="Removed">Objects left on the secondary after the primary deleted them, now removed.</param>
/// <param name="Failed">Objects that could not be fixed this time.</param>
public sealed record ReconciliationSummary(int Copied, int Removed, int Failed);

/// <summary>
/// Mirror mode: keeping the secondary a copy of the primary
/// (plan section 2.10).
/// </summary>
/// <remarks>
/// <para>
/// Three parts, each covering what the one before can miss. Every write is
/// repeated on the secondary as it happens (<see cref="MirrorAsync"/>, driven
/// by the <see cref="ObjectStored"/> event). A copy that failed despite the
/// bus's retries is caught by the nightly pass (<see cref="ReconcileAsync"/>),
/// which compares the two sides and fixes the differences. And
/// <see cref="CheckHealthAsync"/> records which provider answers, for the
/// panel and for deciding where reads should go.
/// </para>
/// <para>
/// Deletes are not mirrored by event: <see cref="S3FileStorage.DeleteAsync"/>
/// already deletes from every provider synchronously, because a file the user
/// deleted must not wait for a queue to disappear.
/// </para>
/// </remarks>
public sealed class StorageMirror
{
    private readonly MongoContext _context;
    private readonly StorageProviderRegistry _registry;
    private readonly S3ClientFactory _clients;
    private readonly IHttpClientFactory _http;
    private readonly IClock _clock;
    private readonly ILogger<StorageMirror> _logger;

    public StorageMirror(
        MongoContext context,
        StorageProviderRegistry registry,
        S3ClientFactory clients,
        IHttpClientFactory http,
        IClock clock,
        ILogger<StorageMirror> logger)
    {
        _context = context;
        _registry = registry;
        _clients = clients;
        _http = http;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Repeats one object on the secondary.
    /// </summary>
    /// <returns>False when there is no secondary, or the object was not written to the primary.</returns>
    /// <exception cref="Exception">
    /// The copy failed. Thrown rather than swallowed, so the bus retries it.
    /// </exception>
    public async Task<bool> MirrorAsync(
        Guid writtenTo, string bucketRole, string key, CancellationToken cancellationToken = default)
    {
        var primary = await _registry.GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        var secondary = await _registry.GetSecondaryAsync(cancellationToken).ConfigureAwait(false);

        // Only the primary is mirrored. An object written elsewhere - to a
        // migration target, or to a provider that has since stepped down - is
        // not this mirror's to copy.
        if (secondary is null || writtenTo != primary.Id || secondary.Id == primary.Id)
        {
            return false;
        }

        await S3ObjectCopier.CopyAsync(
                _clients.For(primary),
                BucketName(primary, bucketRole),
                _clients.For(secondary),
                BucketName(secondary, bucketRole),
                key,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Makes the secondary match the primary: copies what it is missing,
    /// removes what the primary no longer has.
    /// </summary>
    /// <remarks>
    /// The secondary is listed before the primary, and that order matters. An
    /// object uploaded between the two listings shows up on the primary side
    /// and is copied; one deleted between them shows up only on the secondary
    /// side and is removed. Listing the other way round would remove an object
    /// that had just been mirrored.
    /// </remarks>
    public async Task<ReconciliationSummary> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var primary = await _registry.GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        var secondary = await _registry.GetSecondaryAsync(cancellationToken).ConfigureAwait(false);

        if (secondary is null || secondary.Id == primary.Id)
        {
            return new ReconciliationSummary(0, 0, 0);
        }

        // A migration writes to its target too; reconciling at the same time
        // would fight it over which side is right.
        if (await _registry.GetMigrationTargetAsync(primary.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            _logger.LogInformation("Reconciliation skipped: a migration is running.");
            return new ReconciliationSummary(0, 0, 0);
        }

        var primaryClient = _clients.For(primary);
        var secondaryClient = _clients.For(secondary);

        int copied = 0, removed = 0, failed = 0;

        foreach (var role in StorageMigrationWorker.BucketOrder)
        {
            var secondaryBucket = BucketName(secondary, role);
            var primaryBucket = BucketName(primary, role);

            var onSecondary = await ListAsync(secondaryClient, secondaryBucket, cancellationToken).ConfigureAwait(false);
            var onPrimary = await ListAsync(primaryClient, primaryBucket, cancellationToken).ConfigureAwait(false);

            foreach (var (key, size) in onPrimary)
            {
                if (onSecondary.TryGetValue(key, out var mirrored) && mirrored == size)
                {
                    continue;
                }

                try
                {
                    await S3ObjectCopier.CopyAsync(
                            primaryClient, primaryBucket, secondaryClient, secondaryBucket, key, cancellationToken)
                        .ConfigureAwait(false);
                    copied++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Reconciliation could not copy {Key} to the secondary.", key);
                    failed++;
                }
            }

            foreach (var key in onSecondary.Keys.Where(key => !onPrimary.ContainsKey(key)))
            {
                try
                {
                    await secondaryClient.DeleteObjectAsync(secondaryBucket, key, cancellationToken).ConfigureAwait(false);
                    removed++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Reconciliation could not remove {Key} from the secondary.", key);
                    failed++;
                }
            }
        }

        _logger.LogInformation(
            "Mirror reconciliation: {Copied} copied, {Removed} removed, {Failed} failed.",
            copied,
            removed,
            failed);

        return new ReconciliationSummary(copied, removed, failed);
    }

    /// <summary>
    /// Records whether each provider answers (plan section 2.10, every minute).
    /// </summary>
    /// <remarks>
    /// A targeted update rather than a save of the whole document: the check
    /// runs every minute and must not collide with an administrator editing
    /// the same provider, which a row-version conflict would turn into a
    /// failed edit.
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, bool>> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var providers = await _context.StorageProviders
            .Find(FilterDefinition<StorageProvider>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var results = new Dictionary<Guid, bool>();
        var changed = false;

        foreach (var provider in providers)
        {
            string? problem = null;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                problem = await CheckReadinessAsync(provider, timeout.Token).ConfigureAwait(false);

                if (problem is null)
                {
                    // Signed, so it also proves the stored credentials and the
                    // bucket - which no product's health endpoint does.
                    await _clients.For(provider).GetBucketLocationAsync(provider.Buckets.Files, timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (AmazonS3Exception exception)
            {
                problem = $"Provayder xəta qaytardı ({exception.ErrorCode ?? exception.StatusCode.ToString()}).";
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                problem = "Provayderə qoşulmaq mümkün olmadı.";
            }

            var healthy = problem is null;
            results[provider.Id] = healthy;

            var status = healthy ? StorageHealthStatus.Healthy : StorageHealthStatus.Unhealthy;
            changed |= provider.HealthStatus != status;

            var update = Builders<StorageProvider>.Update
                .Set(p => p.HealthStatus, status)
                .Set(p => p.HealthMessage, problem)
                .Set(p => p.LastCheckedAt, _clock.UtcNow);

            await _context.StorageProviders.UpdateOneAsync(p => p.Id == provider.Id, update, cancellationToken)
                .ConfigureAwait(false);

            if (!healthy && provider.Role == StorageProviderRole.Primary)
            {
                // The incident (Critical) is raised here once incidents exist,
                // in step 7. Until then the log is where this is seen; reads
                // already fall back to the secondary on their own.
                _logger.LogCritical(
                    "The primary storage provider {Name} is not answering: {Problem}",
                    provider.Name,
                    problem);
            }
        }

        if (changed)
        {
            _registry.Invalidate();
        }

        return results;
    }

    /// <summary>
    /// Asks the product's own readiness endpoint, where it has one
    /// (<see cref="StorageProviderProfile"/>).
    /// </summary>
    /// <returns>What is wrong, or null when ready or when there is nothing to ask.</returns>
    /// <remarks>
    /// Unauthenticated, so nothing secret is sent. A connection failure is
    /// left to throw: it is the same "cannot reach" the S3 check would report.
    /// </remarks>
    private async Task<string?> CheckReadinessAsync(StorageProvider provider, CancellationToken cancellationToken)
    {
        var uri = StorageProviderProfile.ReadinessUri(provider);

        if (uri is null)
        {
            return null;
        }

        using var response = await _http.CreateClient(nameof(StorageMirror))
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? null
            : $"Provayder hazır olmadığını bildirir (readiness: {(int)response.StatusCode}).";
    }

    private static async Task<Dictionary<string, long>> ListAsync(
        IAmazonS3 client, string bucket, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<string, long>(StringComparer.Ordinal);
        string? continuation = null;

        try
        {
            do
            {
                var page = await client.ListObjectsV2Async(
                        new ListObjectsV2Request { BucketName = bucket, ContinuationToken = continuation },
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var entry in page.S3Objects ?? [])
                {
                    keys[entry.Key] = entry.Size ?? 0;
                }

                continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (continuation is not null);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchBucket")
        {
            // Empty, then: every object will be reported as missing.
        }

        return keys;
    }

    private static string BucketName(StorageProvider provider, string role) => role switch
    {
        "Files" => provider.Buckets.Files,
        "Receipts" => provider.Buckets.Receipts,
        "Temp" => provider.Buckets.Temp,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown bucket role."),
    };
}

/// <summary>Repeats each write on the secondary, off the request thread.</summary>
public sealed class StorageMirrorConsumer : IConsumer<ObjectStored>
{
    private readonly StorageMirror _mirror;

    public StorageMirrorConsumer(StorageMirror mirror) => _mirror = mirror;

    public Task Consume(ConsumeContext<ObjectStored> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A failure throws, and the bus retries it; what still fails is picked
        // up by the nightly reconciliation.
        return _mirror.MirrorAsync(
            context.Message.ProviderId, context.Message.Bucket, context.Message.Key, context.CancellationToken);
    }
}

/// <summary>The recurring jobs mirror mode runs on (plan section 2.10).</summary>
public sealed class StorageMirrorJobs
{
    public const string HealthJobId = "storage-health-check";
    public const string ReconciliationJobId = "storage-reconciliation";

    private readonly StorageMirror _mirror;

    public StorageMirrorJobs(StorageMirror mirror) => _mirror = mirror;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task CheckHealthAsync(CancellationToken cancellationToken) => _mirror.CheckHealthAsync(cancellationToken);

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public Task ReconcileAsync(CancellationToken cancellationToken) => _mirror.ReconcileAsync(cancellationToken);
}
