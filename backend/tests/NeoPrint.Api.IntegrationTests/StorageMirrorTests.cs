using System.Text;

using Amazon.S3.Model;

using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Crypto;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Storage;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Mirror mode (plan section 2.10): the secondary keeps a copy of the primary.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StorageMirrorTests : MirrorTestBase, IClassFixture<StoragePair>
{
    private readonly StoragePair _pair;

    public StorageMirrorTests(NeoPrintApiFixture fixture, StoragePair pair)
        : base(fixture) => _pair = pair;

    protected override Task<(IContainer Primary, IContainer Secondary, bool Owned)> StoresAsync()
        => Task.FromResult((_pair.Source, _pair.Target, false));

    [DockerFact]
    public async Task A_write_to_the_primary_is_repeated_on_the_secondary()
    {
        var (primary, _) = await AddProvidersAsync();
        var stored = await WriteAsync("mirror/document.txt", "yalnız bir nüsxə kifayət deyil");

        var mirrored = await Mirror().MirrorAsync(primary.Id, "Files", stored.Key);

        mirrored.Should().BeTrue();
        (await KeysAsync(SecondaryStore)).Should().Contain(stored.Key);
    }

    [DockerFact]
    public async Task A_missing_object_is_not_fetched_from_the_secondary_instead()
    {
        await AddProvidersAsync();

        // Only on the secondary. "Not found" is an answer from a healthy
        // primary; serving something else in its place would be wrong.
        await PutRawAsync(SecondaryStore, "mirror/only-on-secondary.bin", "stray");

        var ghost = new StoredObject(
            (await Registry.GetPrimaryAsync()).Id, "mirror/only-on-secondary.bin", "00", 5, 5, "k1");

        using var destination = new MemoryStream();
        var read = async () => await Storage().ReadAsync(ghost, StorageBucket.Files, destination);

        await read.Should().ThrowAsync<StorageObjectNotFoundException>();
    }

    [DockerFact]
    public async Task Reconciliation_copies_what_the_secondary_missed_and_removes_what_the_primary_deleted()
    {
        await AddProvidersAsync();

        // Missed by the mirror: on the primary only.
        var missed = await WriteAsync("mirror/missed.txt", "mirror bunu buraxdı");

        // Deleted on the primary, still on the secondary.
        await PutRawAsync(SecondaryStore, "mirror/deleted-on-primary.bin", "orphan");

        var summary = await Mirror().ReconcileAsync();

        summary.Copied.Should().Be(1);
        summary.Removed.Should().Be(1);
        summary.Failed.Should().Be(0);

        var onSecondary = await KeysAsync(SecondaryStore);
        onSecondary.Should().Contain(missed.Key);
        onSecondary.Should().NotContain("mirror/deleted-on-primary.bin");

        // A second pass has nothing left to do.
        (await Mirror().ReconcileAsync()).Should().Be(new ReconciliationSummary(0, 0, 0));
    }

    [DockerFact]
    public async Task Nothing_is_mirrored_or_reconciled_without_a_secondary()
    {
        var (primary, secondary) = await AddProvidersAsync();

        var stored = await Context.StorageProviders.Find(p => p.Id == secondary.Id).FirstAsync();
        stored.Deactivate(ActorContext.System("test"), DateTime.UtcNow);
        await AggregateWriter.ReplaceAsync(Context, Context.StorageProviders, stored, CancellationToken.None);
        Registry.Invalidate();

        var written = await WriteAsync("mirror/no-secondary.txt", "tək");

        (await Mirror().MirrorAsync(primary.Id, "Files", written.Key)).Should().BeFalse();
        (await Mirror().ReconcileAsync()).Should().Be(new ReconciliationSummary(0, 0, 0));
        (await KeysAsync(SecondaryStore)).Should().NotContain(written.Key);
    }
}

/// <summary>
/// The primary stops for good, so this test has stores of its own: a stopped
/// container comes back on a different port, and the shared pair must not.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StorageFailoverTests : MirrorTestBase
{
    public StorageFailoverTests(NeoPrintApiFixture fixture)
        : base(fixture)
    {
    }

    protected override async Task<(IContainer Primary, IContainer Secondary, bool Owned)> StoresAsync()
    {
        var primary = NeoPrintApiFixture.CloudServer();
        var secondary = NeoPrintApiFixture.CloudServer();
        await Task.WhenAll(primary.StartAsync(), secondary.StartAsync());

        return (primary, secondary, true);
    }

    [DockerFact]
    public async Task Reads_carry_on_from_the_secondary_when_the_primary_stops()
    {
        var (primary, _) = await AddProvidersAsync();

        const string content = "Primary söndürülüb, oxuma davam edir.";
        var stored = await WriteAsync("mirror/survivor.txt", content);
        await Mirror().MirrorAsync(primary.Id, "Files", stored.Key);

        // The primary goes away - not paused, stopped.
        await PrimaryStore.StopAsync();

        using var readBack = new MemoryStream();
        await Storage().ReadAsync(stored, StorageBucket.Files, readBack);

        // Plan section 12, step 2's definition of done.
        Encoding.UTF8.GetString(readBack.ToArray()).Should().Be(content);

        // And the health check notices, which is what the panel shows.
        var health = await Mirror().CheckHealthAsync();
        health[primary.Id].Should().BeFalse();
        health.Values.Should().Contain(true, "the secondary is still answering");

        var recorded = await Context.StorageProviders.Find(p => p.Id == primary.Id).FirstAsync();
        recorded.HealthStatus.Should().Be(StorageHealthStatus.Unhealthy);
        recorded.HealthMessage.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>The stores, database and services the mirror tests share.</summary>
public abstract class MirrorTestBase : IAsyncLifetime
{
    private readonly NeoPrintApiFixture _fixture;

    private IContainer? _primaryStore;
    private IContainer? _secondaryStore;
    private MongoContext? _context;
    private StorageProviderRegistry? _registry;
    private S3ClientFactory? _clients;
    private string? _databaseName;
    private bool _ownsStores;

    protected MirrorTestBase(NeoPrintApiFixture fixture) => _fixture = fixture;

    protected IContainer PrimaryStore => _primaryStore!;

    protected IContainer SecondaryStore => _secondaryStore!;

    protected MongoContext Context => _context!;

    protected StorageProviderRegistry Registry => _registry!;

    /// <summary>The two stores, and whether this test owns (and disposes) them.</summary>
    protected abstract Task<(IContainer Primary, IContainer Secondary, bool Owned)> StoresAsync();

    public async Task InitializeAsync()
    {
        if (new DockerFactAttribute().Skip is not null)
        {
            return;
        }

        (_primaryStore, _secondaryStore, _ownsStores) = await StoresAsync();

        await StoragePair.ResetAsync(_primaryStore);
        await StoragePair.ResetAsync(_secondaryStore);

        _databaseName = $"neoprint-mirror-{Guid.NewGuid():N}";
        _context = new MongoContext(new MongoClient(_fixture.MongoConnectionString), _databaseName);
        await _context.EnsureIndexesAsync();

        _registry = new StorageProviderRegistry(_context, NullLogger<StorageProviderRegistry>.Instance);
        _clients = new S3ClientFactory(_fixture.Services.GetRequiredService<StorageSecretProtector>());
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is not null)
        {
            await new MongoClient(_fixture.MongoConnectionString).DropDatabaseAsync(_databaseName);
        }

        if (_ownsStores)
        {
            await Task.WhenAll(
                _primaryStore?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _secondaryStore?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        }
    }

    protected StorageMirror Mirror()
        => new(Context, Registry, _clients!, new SystemClock(), NullLogger<StorageMirror>.Instance);

    protected S3FileStorage Storage()
        => new(
            Registry,
            _clients!,
            _fixture.Services.GetRequiredService<EnvelopeEncryptor>(),
            _fixture.Services.GetRequiredService<IKeyEncryptionKeyProvider>(),
            NullLogger<S3FileStorage>.Instance);

    protected Task<StoredObject> WriteAsync(string key, string content)
        => Storage().WriteAsync(
            StorageBucket.Files, key, new MemoryStream(Encoding.UTF8.GetBytes(content)), "text/plain");

    protected async Task<(StorageProvider Primary, StorageProvider Secondary)> AddProvidersAsync()
    {
        var protector = _fixture.Services.GetRequiredService<StorageSecretProtector>();
        var actor = ActorContext.System("test");

        StorageProvider Build(string name, IContainer store)
        {
            var provider = new StorageProvider
            {
                Name = name,
                Type = StorageProviderType.Scality,
                Endpoint = StorageMigrationTests.Endpoint(store),
                Buckets = new StorageBuckets
                {
                    Files = NeoPrintApiFixture.FilesBucket,
                    Receipts = NeoPrintApiFixture.ReceiptsBucket,
                    Temp = NeoPrintApiFixture.TempBucket,
                },
            };

            provider.AccessKeyCiphertext = protector.Protect(NeoPrintApiFixture.StorageAccessKey, provider.Id);
            provider.SecretKeyCiphertext = protector.Protect(NeoPrintApiFixture.StorageSecretKey, provider.Id);
            provider.RecordHealth(true, null, DateTime.UtcNow);

            return provider;
        }

        var primary = Build("Primary", PrimaryStore);
        primary.MakePrimary(actor, DateTime.UtcNow);

        var secondary = Build("Secondary", SecondaryStore);
        secondary.MakeSecondary(actor, DateTime.UtcNow);

        await Context.StorageProviders.InsertManyAsync([primary, secondary]);
        Registry.Invalidate();

        return (primary, secondary);
    }

    protected static async Task PutRawAsync(IContainer store, string key, string content)
    {
        using var client = StorageMigrationTests.Direct(store);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Key = key,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        });
    }

    protected static async Task<List<string>> KeysAsync(IContainer store)
    {
        using var client = StorageMigrationTests.Direct(store);
        var listing = await client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = NeoPrintApiFixture.FilesBucket });

        return [.. (listing.S3Objects ?? []).Select(o => o.Key)];
    }
}
