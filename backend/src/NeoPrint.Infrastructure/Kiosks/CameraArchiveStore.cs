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

    public async Task<IReadOnlyList<CameraSegment>> ListExpiredAsync(DateTime now, int limit, CancellationToken cancellationToken = default)
        => await _context.CameraSegments.Find(segment => segment.ExpiresAt < now)
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.CameraSegments.DeleteOneAsync(segment => segment.Id == id, cancellationToken);
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
