using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

using MediatR;

using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Storage;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary>
/// The camera archive on a storage provider (T-485): the provider's Files
/// bucket, under <c>camera/</c>.
/// </summary>
/// <remarks>
/// The bytes go in as the kiosk sealed them - for the backend's camera key -
/// and are not wrapped once more with the key encryption key: they are
/// already ciphertext nothing but the backend opens. Nothing else in the
/// Files bucket lives under <c>camera/</c>, so the orphan cleanup (which
/// walks <c>files/</c>) never meets them.
/// </remarks>
public sealed class CameraArchiveStore : ICameraArchiveStore
{
    private readonly StorageProviderRegistry _registry;
    private readonly S3ClientFactory _clients;

    public CameraArchiveStore(StorageProviderRegistry registry, S3ClientFactory clients)
    {
        _registry = registry;
        _clients = clients;
    }

    public async Task<bool> HasProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false)).Any(provider => provider.Id == providerId);

    public async Task<Guid> PrimaryProviderIdAsync(CancellationToken cancellationToken = default)
        => (await _registry.GetPrimaryAsync(cancellationToken).ConfigureAwait(false)).Id;

    public async Task<(string Sha256, long Length)> WriteAsync(
        Guid providerId, string key, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var provider = await _registry.GetByIdAsync(providerId, cancellationToken).ConfigureAwait(false);

        var start = content.Position;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        var length = content.Position - start;
        content.Position = start;

        await _clients.For(provider).PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = provider.Buckets.Files,
                    Key = key,
                    InputStream = content,
                    ContentType = "application/octet-stream",
                    AutoCloseStream = false,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return (hash, length);
    }

    public async Task<Stream> OpenReadAsync(Guid providerId, string key, CancellationToken cancellationToken = default)
    {
        var provider = await _registry.GetByIdAsync(providerId, cancellationToken).ConfigureAwait(false);
        var response = await _clients.For(provider)
            .GetObjectAsync(provider.Buckets.Files, key, cancellationToken)
            .ConfigureAwait(false);

        return response.ResponseStream;
    }

    public async Task DeleteAsync(Guid providerId, string key, CancellationToken cancellationToken = default)
    {
        var provider = await _registry.GetByIdAsync(providerId, cancellationToken).ConfigureAwait(false);

        try
        {
            await _clients.For(provider)
                .DeleteObjectAsync(new DeleteObjectRequest { BucketName = provider.Buckets.Files, Key = key }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }
}

/// <summary><c>camera_segments</c>: the archived camera minutes (T-485).</summary>
public sealed class CameraSegmentRepository : ICameraSegmentRepository
{
    private readonly MongoContext _context;

    public CameraSegmentRepository(MongoContext context) => _context = context;

    public async Task<bool> TryAddAsync(CameraSegment segment, CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.CameraSegments.InsertOneAsync(segment, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public Task<CameraSegment?> FindAsync(Guid kioskId, DateTime start, CancellationToken cancellationToken = default)
        => _context.CameraSegments.Find(segment => segment.KioskId == kioskId && segment.Start == start).FirstOrDefaultAsync(cancellationToken)!;

    public Task<CameraSegment?> FindByIdAsync(Guid kioskId, Guid id, CancellationToken cancellationToken = default)
        => _context.CameraSegments.Find(segment => segment.KioskId == kioskId && segment.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<CameraSegment>> ListAsync(Guid kioskId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => await _context.CameraSegments.Find(segment => segment.KioskId == kioskId && segment.Start >= from && segment.Start < to)
            .SortBy(segment => segment.Start)
            .Limit(2000)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<CameraSegment>> ListExpiredAsync(DateTime now, int limit, CancellationToken cancellationToken = default)
        => await _context.CameraSegments.Find(segment => segment.ExpiresAt < now)
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.CameraSegments.DeleteOneAsync(segment => segment.Id == id, cancellationToken);
}

/// <summary><c>camera_requests</c>: what the panel asked kiosks for (T-483).</summary>
public sealed class CameraRequestRepository : ICameraRequestRepository
{
    private readonly MongoContext _context;

    public CameraRequestRepository(MongoContext context) => _context = context;

    public Task AddAsync(CameraRequest request, CancellationToken cancellationToken = default)
        => _context.CameraRequests.InsertOneAsync(request, cancellationToken);

    public Task<CameraRequest?> FindAsync(Guid kioskId, Guid id, CancellationToken cancellationToken = default)
        => _context.CameraRequests.Find(request => request.KioskId == kioskId && request.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<CameraRequest>> ListPendingAsync(Guid kioskId, DateTime now, CancellationToken cancellationToken = default)
        => await _context.CameraRequests
            .Find(request => request.KioskId == kioskId && request.Status == CameraRequestStatus.Pending && request.ExpiresAt > now)
            .SortBy(request => request.CreatedAt)
            .Limit(20)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<CameraRequest?> LatestAsync(Guid kioskId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => _context.CameraRequests
            .Find(request => request.KioskId == kioskId && request.From < to && request.To > from)
            .SortByDescending(request => request.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(CameraRequest request, CancellationToken cancellationToken = default)
        => _context.CameraRequests.ReplaceOneAsync(stored => stored.Id == request.Id, request, cancellationToken: cancellationToken);

    public async Task<int> DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken = default)
        => (int)(await _context.CameraRequests.DeleteManyAsync(request => request.ExpiresAt < now, cancellationToken).ConfigureAwait(false))
            .DeletedCount;
}

/// <summary>
/// The live picture in Redis (T-483): who watches, and the latest frame -
/// both expire on their own, so a closed tab or a gone kiosk leaves nothing.
/// </summary>
public sealed class RedisCameraLiveStore : ICameraLiveStore
{
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;

    public RedisCameraLiveStore(StackExchange.Redis.IConnectionMultiplexer redis) => _redis = redis;

    public Task WatchAsync(Guid kioskId, TimeSpan lifetime, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().StringSetAsync(WatchKey(kioskId), "1", lifetime);

    public Task<bool> IsWatchedAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().KeyExistsAsync(WatchKey(kioskId));

    public Task StopAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().KeyDeleteAsync([WatchKey(kioskId), FrameKey(kioskId)]);

    public Task PutFrameAsync(Guid kioskId, CameraLiveFrame frame, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Eight bytes of time, then the JPEG.
        var value = new byte[8 + frame.Jpeg.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(value, frame.At.Ticks);
        frame.Jpeg.CopyTo(value, 8);

        return _redis.GetDatabase().StringSetAsync(FrameKey(kioskId), value, lifetime);
    }

    public async Task<CameraLiveFrame?> GetFrameAsync(Guid kioskId, CancellationToken cancellationToken = default)
    {
        byte[]? value = await _redis.GetDatabase().StringGetAsync(FrameKey(kioskId)).ConfigureAwait(false);
        if (value is null || value.Length <= 8)
        {
            return null;
        }

        var at = new DateTime(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(value), DateTimeKind.Utc);
        return new CameraLiveFrame(value[8..], at);
    }

    private static StackExchange.Redis.RedisKey WatchKey(Guid kioskId) => $"camera:live:watch:{kioskId}";

    private static StackExchange.Redis.RedisKey FrameKey(Guid kioskId) => $"camera:live:frame:{kioskId}";
}

/// <summary>The archive's daily clean-up, as a Hangfire job.</summary>
public sealed class CameraArchiveJobs
{
    public const string PurgeJobId = "camera-archive-purge";

    /// <summary>Hourly: a minute is gone within the hour its time ran out.</summary>
    public const string Hourly = "17 * * * *";

    private readonly ISender _sender;

    public CameraArchiveJobs(ISender sender) => _sender = sender;

    public Task<int> PurgeAsync(CancellationToken cancellationToken)
        => _sender.Send(new PurgeExpiredCameraSegmentsCommand(), cancellationToken);
}
