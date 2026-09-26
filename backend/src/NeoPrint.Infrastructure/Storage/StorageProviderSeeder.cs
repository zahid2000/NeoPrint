using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Creates the first storage provider from the secret store, once.
/// </summary>
/// <remarks>
/// <para>
/// Plan section 2.10: the installation starts with MinIO, configured from
/// <c>NEOPRINT_STORAGE_SEED_*</c> in OpenBao, and every later change is made
/// from the admin panel. So this runs only when the collection is empty - it
/// does not reconcile, because doing so would silently undo an administrator
/// who moved the endpoint or rotated the keys.
/// </para>
/// <para>
/// The seeded provider is made primary without a connection test. It is the
/// only way the installation has to store anything at all, and the health
/// check will mark it unhealthy soon enough if it is wrong; refusing to seed
/// would leave an installation that cannot accept a single upload.
/// </para>
/// </remarks>
public sealed class StorageProviderSeeder
{
    private readonly MongoContext _context;
    private readonly IConfiguration _configuration;
    private readonly StorageSecretProtector _secrets;
    private readonly ILogger<StorageProviderSeeder> _logger;

    public StorageProviderSeeder(
        MongoContext context,
        IConfiguration configuration,
        StorageSecretProtector secrets,
        ILogger<StorageProviderSeeder> logger)
    {
        _context = context;
        _configuration = configuration;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.StorageProviders
            .CountDocumentsAsync(FilterDefinition<StorageProvider>.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (existing > 0)
        {
            return;
        }

        var accessKey = _configuration["NEOPRINT_STORAGE_SEED_ACCESS_KEY"];
        var secretKey = _configuration["NEOPRINT_STORAGE_SEED_SECRET_KEY"];
        var endpoint = _configuration["NEOPRINT_STORAGE_SEED_ENDPOINT"];

        if (string.IsNullOrWhiteSpace(accessKey)
            || string.IsNullOrWhiteSpace(secretKey)
            || string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning(
                "No storage provider is configured and NEOPRINT_STORAGE_SEED_* is incomplete, "
                + "so none was created. Add one from the admin panel before uploading a file.");
            return;
        }

        // The id is generated first: it is bound into the sealed credentials,
        // so it has to exist before they are protected.
        var provider = new StorageProvider
        {
            Id = Guid.NewGuid(),
            Name = _configuration["NEOPRINT_STORAGE_SEED_NAME"] ?? "Local MinIO",
            Type = Enum.TryParse<StorageProviderType>(
                _configuration["NEOPRINT_STORAGE_SEED_TYPE"], ignoreCase: true, out var type)
                ? type
                : StorageProviderType.MinIO,
            Endpoint = endpoint,
            Region = _configuration["NEOPRINT_STORAGE_SEED_REGION"] ?? "us-east-1",
            UseSsl = bool.TryParse(_configuration["NEOPRINT_STORAGE_SEED_USE_SSL"], out var ssl) && ssl,
            Buckets = new StorageBuckets
            {
                Files = _configuration["NEOPRINT_STORAGE_SEED_BUCKET_FILES"] ?? "neoprint-files",
                Receipts = _configuration["NEOPRINT_STORAGE_SEED_BUCKET_RECEIPTS"] ?? "neoprint-receipts",
                Temp = _configuration["NEOPRINT_STORAGE_SEED_BUCKET_TEMP"] ?? "neoprint-temp",
            },
        };

        provider.AccessKeyCiphertext = _secrets.Protect(accessKey, provider.Id);
        provider.SecretKeyCiphertext = _secrets.Protect(secretKey, provider.Id);

        // Seeding is the installation's own act, not an administrator's.
        var actor = ActorContext.System("Storage provider seeded from the secret store.");

        provider.RecordHealth(true, null, DateTime.UtcNow);
        provider.MakePrimary(actor, DateTime.UtcNow);

        await _context.StorageProviders.InsertOneAsync(provider, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Seeded storage provider '{Name}' ({Type}) as Primary.", provider.Name, provider.Type);
    }
}
