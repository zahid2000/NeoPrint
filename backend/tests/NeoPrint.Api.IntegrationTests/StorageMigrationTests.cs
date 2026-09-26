using System.Text;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Crypto;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Storage;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Migrating objects between two providers (plan sections 2.10, 9.8).
/// </summary>
/// <remarks>
/// <para>
/// Two object stores of their own and a database of their own, so the counts
/// are exact: the shared store holds whatever the other tests left behind, and
/// a migration copies a whole bucket.
/// </para>
/// <para>
/// The question the owner asked to have proven is the restart one: a worker
/// that dies half way must be resumed from where it stopped, by another
/// worker, without starting over and without two workers running at once.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StorageMigrationTests : IClassFixture<StoragePair>, IAsyncLifetime
{
    private readonly NeoPrintApiFixture _fixture;
    private readonly StoragePair _pair;

    private IContainer? _sourceStore;
    private IContainer? _targetStore;
    private MongoContext? _context;
    private string? _databaseName;

    private readonly TestClock _clock = new(new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc));

    public StorageMigrationTests(NeoPrintApiFixture fixture, StoragePair pair)
    {
        _fixture = fixture;
        _pair = pair;
    }

    public async Task InitializeAsync()
    {
        if (new DockerFactAttribute().Skip is not null)
        {
            return;
        }

        _sourceStore = _pair.Source;
        _targetStore = _pair.Target;

        // Same stores for every test, so each one starts them empty with all
        // three buckets: the counts below are exact, and one test deletes a
        // bucket on purpose.
        await StoragePair.ResetAsync(_sourceStore);
        await StoragePair.ResetAsync(_targetStore);

        _databaseName = $"neoprint-sync-{Guid.NewGuid():N}";
        _context = new MongoContext(new MongoClient(_fixture.MongoConnectionString), _databaseName);
        await _context.EnsureIndexesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is not null)
        {
            await new MongoClient(_fixture.MongoConnectionString).DropDatabaseAsync(_databaseName);
        }
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_migration_copies_everything_switches_the_references_and_the_primary()
    {
        var (source, target) = await AddProvidersAsync();
        var files = await UploadAsync(count: 10);
        await PutRawAsync(_sourceStore!, NeoPrintApiFixture.ReceiptsBucket, "receipts/r-1.pdf", "receipt one");
        await PutRawAsync(_sourceStore!, NeoPrintApiFixture.ReceiptsBucket, "receipts/r-2.pdf", "receipt two");

        var job = await StartJobAsync(source, target, switchPrimary: true);

        var finished = await Worker("worker-1").RunAsync(job.Id);

        finished!.Status.Should().Be(StorageSyncStatus.Completed, finished.FailureReason);
        finished.TotalObjects.Should().Be(12);
        finished.CopiedObjects.Should().Be(12);
        finished.FailedObjects.Should().Be(0);

        (await KeysAsync(_targetStore!, NeoPrintApiFixture.FilesBucket)).Should().HaveCount(10);
        (await KeysAsync(_targetStore!, NeoPrintApiFixture.ReceiptsBucket)).Should().HaveCount(2);

        // Every file now points at the target, and reads from there.
        var stored = await _context!.Files.Find(FilterDefinition<StoredFile>.Empty).ToListAsync();
        stored.Should().OnlyContain(file => file.Original.ProviderId == target.Id);

        var storage = Storage();
        foreach (var (file, content) in files)
        {
            var reloaded = stored.Single(f => f.Id == file.Id);

            using var readBack = new MemoryStream();
            await storage.ReadAsync(ToStored(reloaded.Original), StorageBucket.Files, readBack);

            Encoding.UTF8.GetString(readBack.ToArray()).Should().Be(content);
        }

        // "Bitəndə Primary et": the target is primary, the source steps out of
        // the write path but stays configured.
        var providers = await _context.StorageProviders.Find(FilterDefinition<StorageProvider>.Empty).ToListAsync();
        providers.Single(p => p.Id == target.Id).Role.Should().Be(StorageProviderRole.Primary);
        providers.Single(p => p.Id == source.Id).Role.Should().Be(StorageProviderRole.Inactive);
    }

    [DockerFact]
    public async Task A_worker_that_dies_half_way_is_resumed_from_its_checkpoint_by_another()
    {
        var (source, target) = await AddProvidersAsync();
        await UploadAsync(count: 12);

        var job = await StartJobAsync(source, target, switchPrimary: true);

        // The first worker "dies" after five objects: its process is stopped,
        // which is what cancellation stands for here. Nothing is cleaned up -
        // a killed process does not get to.
        using var death = new CancellationTokenSource();
        var firstObserver = new CountingObserver(onCopied: count =>
        {
            if (count == 5)
            {
                death.Cancel();
            }

            return Task.CompletedTask;
        });

        var first = Worker("worker-1", firstObserver);
        var run = async () => await first.RunAsync(job.Id, death.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();

        var interrupted = await LoadJobAsync(job.Id);

        interrupted.Status.Should().Be(StorageSyncStatus.Running);
        interrupted.CopiedObjects.Should().Be(5);
        interrupted.Checkpoint.Should().NotBeNull();
        interrupted.Checkpoint!.Bucket.Should().Be("Files");
        interrupted.LeaseOwner.Should().Be("worker-1");

        // While the dead worker's lease is still live, nobody else may take
        // the job: that is what stops two workers copying at once.
        var second = new CountingObserver();
        (await Worker("worker-2", second).RunAsync(job.Id)).Should().BeNull();
        second.Copied.Should().Be(0);

        // Time passes; the lease lapses.
        _clock.Advance(StorageMigrationWorker.LeaseDuration + TimeSpan.FromSeconds(1));

        var resumed = await Worker("worker-2", second).RunAsync(job.Id);

        resumed!.Status.Should().Be(StorageSyncStatus.Completed, resumed.FailureReason);

        // Resumed, not restarted: the second worker copied only what was left.
        second.Copied.Should().Be(12 - 5);
        resumed.CopiedObjects.Should().Be(12);

        (await KeysAsync(_targetStore!, NeoPrintApiFixture.FilesBucket)).Should().HaveCount(12);
        resumed.LeaseOwner.Should().BeNull("a finished job holds no lease");
    }

    [DockerFact]
    public async Task A_file_uploaded_during_the_migration_reaches_the_target()
    {
        var (source, target) = await AddProvidersAsync();
        await UploadAsync(count: 6, keyPrefix: "m");

        var job = await StartJobAsync(source, target, switchPrimary: true);

        // Pause after the first few objects, the way the panel does.
        var observer = new CountingObserver(onCopied: async count =>
        {
            if (count == 3)
            {
                await PauseAsync(job.Id);
            }
        });

        var paused = await Worker("worker-1", observer).RunAsync(job.Id);
        paused!.Status.Should().Be(StorageSyncStatus.Paused);
        paused.LeaseOwner.Should().BeNull("a paused job can be resumed at once");

        // Uploaded now, with a key that sorts before the checkpoint - the
        // listing has already passed where it belongs. Only the dual-write
        // puts it on the target.
        var (late, content) = await UploadOneAsync("a-late-upload");

        (await KeysAsync(_targetStore!, NeoPrintApiFixture.FilesBucket))
            .Should().Contain(late.Original.Key, "the upload is written to both while a migration is active");

        await ResumeAsync(job.Id);
        var finished = await Worker("worker-1").RunAsync(job.Id);

        finished!.Status.Should().Be(StorageSyncStatus.Completed, finished.FailureReason);

        var reloaded = await _context!.Files.Find(f => f.Id == late.Id).FirstAsync();
        reloaded.Original.ProviderId.Should().Be(target.Id);

        using var readBack = new MemoryStream();
        await Storage().ReadAsync(ToStored(reloaded.Original), StorageBucket.Files, readBack);
        Encoding.UTF8.GetString(readBack.ToArray()).Should().Be(content);
    }

    [DockerFact]
    public async Task An_object_the_copy_passed_over_is_repaired_before_the_switch()
    {
        var (source, target) = await AddProvidersAsync();
        await UploadAsync(count: 6, keyPrefix: "m");

        var job = await StartJobAsync(source, target, switchPrimary: false);

        var observer = new CountingObserver(onCopied: async count =>
        {
            if (count == 3)
            {
                await PauseAsync(job.Id);
            }
        });

        await Worker("worker-1", observer).RunAsync(job.Id);

        // Written straight to the source, as if the dual-write had failed,
        // with a key the listing has already passed.
        await PutRawAsync(_sourceStore!, NeoPrintApiFixture.FilesBucket, "a/missed-by-the-listing.bin", "missed");

        await ResumeAsync(job.Id);
        var finished = await Worker("worker-1").RunAsync(job.Id);

        finished!.Status.Should().Be(StorageSyncStatus.Completed, finished.FailureReason);

        (await KeysAsync(_targetStore!, NeoPrintApiFixture.FilesBucket))
            .Should().Contain("a/missed-by-the-listing.bin", "verification copies what the listing passed over");
    }

    [DockerFact]
    public async Task A_migration_that_cannot_finish_switches_nothing()
    {
        var (source, target) = await AddProvidersAsync();
        var files = await UploadAsync(count: 3);
        await PutRawAsync(_sourceStore!, NeoPrintApiFixture.ReceiptsBucket, "receipts/r-1.pdf", "receipt");

        // The target has no receipts bucket, so that object cannot land.
        using (var client = Direct(_targetStore!))
        {
            await client.DeleteBucketAsync(NeoPrintApiFixture.ReceiptsBucket);
        }

        var job = await StartJobAsync(source, target, switchPrimary: true);

        var finished = await Worker("worker-1").RunAsync(job.Id);

        finished!.Status.Should().Be(StorageSyncStatus.Failed);
        finished.FailureReason.Should().Contain("köçürülə bilmədi");

        // The source is still the only complete copy, so nothing may point
        // away from it and it must still be the primary.
        var stored = await _context!.Files.Find(FilterDefinition<StoredFile>.Empty).ToListAsync();
        stored.Should().OnlyContain(file => file.Original.ProviderId == source.Id);

        var providers = await _context.StorageProviders.Find(FilterDefinition<StorageProvider>.Empty).ToListAsync();
        providers.Single(p => p.Id == source.Id).Role.Should().Be(StorageProviderRole.Primary);

        var failures = await _context.StorageSyncFailures.Find(f => f.JobId == job.Id).ToListAsync();
        failures.Should().ContainSingle(f => f.Key == "receipts/r-1.pdf" && f.ResolvedAt == null);

        // And the files still read, from the source.
        using var readBack = new MemoryStream();
        await Storage().ReadAsync(ToStored(stored[0].Original), StorageBucket.Files, readBack);
        readBack.Length.Should().BeGreaterThan(0);
        files.Should().NotBeEmpty();
    }

    [DockerFact]
    public async Task Only_a_job_whose_worker_is_gone_is_resumed()
    {
        var now = _clock.UtcNow;

        StorageSyncJob Job(StorageSyncStatus status, DateTime? leaseUntil)
        {
            var job = new StorageSyncJob { Mode = StorageSyncMode.Migration, SourceId = Guid.NewGuid(), TargetId = Guid.NewGuid() };
            var actor = ActorContext.System("test");

            if (status != StorageSyncStatus.Pending)
            {
                job.Start(actor, now);
            }

            if (status == StorageSyncStatus.Paused)
            {
                job.Pause(actor, now);
            }

            if (status == StorageSyncStatus.Completed)
            {
                job.BeginVerification(actor, now);
                job.Complete(actor, now);
            }

            job.LeaseOwner = leaseUntil is null ? null : "somebody";
            job.LeaseExpiresAt = leaseUntil;
            return job;
        }

        var dead = Job(StorageSyncStatus.Running, now.AddMinutes(-1));
        var alive = Job(StorageSyncStatus.Running, now.AddMinutes(1));
        var neverStarted = Job(StorageSyncStatus.Pending, null);
        var paused = Job(StorageSyncStatus.Paused, null);
        var finished = Job(StorageSyncStatus.Completed, null);

        await _context!.StorageSyncJobs.InsertManyAsync([dead, alive, neverStarted, paused, finished]);

        var jobs = new RecordingJobClient();
        var scheduler = new StorageSyncScheduler(_context, jobs, _clock, NullLogger<StorageSyncScheduler>.Instance);

        var resumed = await scheduler.ResumeStaleAsync(CancellationToken.None);

        // The dead worker's job and the one nobody picked up. Not the one a
        // live worker holds - that would run it twice - and not the paused or
        // finished ones.
        resumed.Should().Be(2);
        jobs.EnqueuedJobIds.Should().BeEquivalentTo([dead.Id, neverStarted.Id]);
    }

    // -----------------------------------------------------------------------
    // Set-up
    // -----------------------------------------------------------------------

    private StorageProviderRegistry? _registry;
    private S3ClientFactory? _clients;

    private StorageProviderRegistry Registry()
        => _registry ??= new StorageProviderRegistry(_context!, NullLogger<StorageProviderRegistry>.Instance);

    private S3ClientFactory Clients()
        => _clients ??= new S3ClientFactory(_fixture.Services.GetRequiredService<StorageSecretProtector>());

    private StorageMigrationWorker Worker(string id, IStorageSyncObserver? observer = null)
        => new(
            _context!,
            Registry(),
            Clients(),
            _clock,
            observer ?? NullStorageSyncObserver.Instance,
            NullLogger<StorageMigrationWorker>.Instance,
            events: null,
            workerId: id);

    private S3FileStorage Storage()
        => new(
            Registry(),
            Clients(),
            _fixture.Services.GetRequiredService<EnvelopeEncryptor>(),
            _fixture.Services.GetRequiredService<IKeyEncryptionKeyProvider>(),
            NullLogger<S3FileStorage>.Instance);

    private async Task<(StorageProvider Source, StorageProvider Target)> AddProvidersAsync()
    {
        var protector = _fixture.Services.GetRequiredService<StorageSecretProtector>();
        var actor = ActorContext.System("test");

        StorageProvider Build(string name, IContainer store)
        {
            var provider = new StorageProvider
            {
                Name = name,
                Type = StorageProviderType.Scality,
                Endpoint = Endpoint(store),
                Buckets = new StorageBuckets
                {
                    Files = NeoPrintApiFixture.FilesBucket,
                    Receipts = NeoPrintApiFixture.ReceiptsBucket,
                    Temp = NeoPrintApiFixture.TempBucket,
                },
            };

            provider.AccessKeyCiphertext = protector.Protect(NeoPrintApiFixture.StorageAccessKey, provider.Id);
            provider.SecretKeyCiphertext = protector.Protect(NeoPrintApiFixture.StorageSecretKey, provider.Id);
            provider.RecordHealth(true, null, _clock.UtcNow);

            return provider;
        }

        var source = Build("Source", _sourceStore!);
        source.MakePrimary(actor, _clock.UtcNow);

        var target = Build("Target", _targetStore!);

        await _context!.StorageProviders.InsertManyAsync([source, target]);
        Registry().Invalidate();

        return (source, target);
    }

    private async Task<StorageSyncJob> StartJobAsync(StorageProvider source, StorageProvider target, bool switchPrimary)
    {
        var job = new StorageSyncJob
        {
            Mode = StorageSyncMode.Migration,
            SourceId = source.Id,
            TargetId = target.Id,
            SwitchPrimaryOnComplete = switchPrimary,
        };

        await _context!.StorageSyncJobs.InsertOneAsync(job);

        // Dual-write starts the moment the job exists, not when a worker
        // first picks it up.
        Registry().Invalidate();

        return job;
    }

    private async Task PauseAsync(Guid jobId)
    {
        var job = await LoadJobAsync(jobId);
        job.Pause(ActorContext.System("test: pause"), _clock.UtcNow);
        await AggregateWriter.ReplaceAsync(_context!, _context!.StorageSyncJobs, job, CancellationToken.None);
    }

    private async Task ResumeAsync(Guid jobId)
    {
        var job = await LoadJobAsync(jobId);
        job.Start(ActorContext.System("test: resume"), _clock.UtcNow);
        await AggregateWriter.ReplaceAsync(_context!, _context!.StorageSyncJobs, job, CancellationToken.None);
    }

    private Task<StorageSyncJob> LoadJobAsync(Guid jobId)
        => _context!.StorageSyncJobs.Find(job => job.Id == jobId).FirstAsync();

    private async Task<List<(StoredFile File, string Content)>> UploadAsync(int count, string keyPrefix = "f")
    {
        var uploaded = new List<(StoredFile, string)>();

        for (var i = 0; i < count; i++)
        {
            uploaded.Add(await UploadOneAsync($"{keyPrefix}-{i:D3}"));
        }

        return uploaded;
    }

    /// <summary>A file written the way the application writes one, with its document.</summary>
    private async Task<(StoredFile File, string Content)> UploadOneAsync(string name)
    {
        var content = $"{name}: {Guid.NewGuid():N}";
        var file = new StoredFile
        {
            UserId = Guid.NewGuid(),
            FileName = $"{name}.txt",
            Extension = "txt",
            MediaType = "text/plain",
        };

        var stored = await Storage().WriteAsync(
            StorageBucket.Files,
            $"{name}/{file.Id:N}.txt",
            new MemoryStream(Encoding.UTF8.GetBytes(content)),
            "text/plain");

        file.Original = new FileObject(
            stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId);

        await _context!.Files.InsertOneAsync(file);

        return (file, content);
    }

    private static StoredObject ToStored(FileObject o)
        => new(o.ProviderId, o.Key, o.Sha256, o.EncryptedLength, o.PlaintextLength, o.KekId);

    internal static string Endpoint(IContainer store) => $"http://{store.Hostname}:{store.GetMappedPublicPort(8000)}";

    internal static AmazonS3Client Direct(IContainer store) => new(
        new BasicAWSCredentials(NeoPrintApiFixture.StorageAccessKey, NeoPrintApiFixture.StorageSecretKey),
        new AmazonS3Config
        {
            ServiceURL = Endpoint(store),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    private static async Task PutRawAsync(IContainer store, string bucket, string key, string content)
    {
        using var client = Direct(store);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        });
    }

    private static async Task<List<string>> KeysAsync(IContainer store, string bucket)
    {
        using var client = Direct(store);
        var listing = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });

        return [.. (listing.S3Objects ?? []).Select(o => o.Key)];
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    /// <summary>Records what would have been handed to Hangfire.</summary>
    private sealed class RecordingJobClient : Hangfire.IBackgroundJobClient
    {
        public List<Guid> EnqueuedJobIds { get; } = [];

        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
        {
            EnqueuedJobIds.Add((Guid)job.Args[0]!);
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => true;
    }

    private sealed class TestClock : IClock
    {
        public TestClock(DateTime start) => UtcNow = start;

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>Counts the objects one worker copied, and can act after a given count.</summary>
    private sealed class CountingObserver : IStorageSyncObserver
    {
        private readonly Func<int, Task>? _onCopied;
        private long? _last;

        public CountingObserver(Func<int, Task>? onCopied = null) => _onCopied = onCopied;

        public int Copied { get; private set; }

        public async Task ProgressAsync(StorageSyncJob job, CancellationToken cancellationToken)
        {
            // The first call follows this worker's first copy, whatever the
            // job had copied before it was picked up.
            _last ??= job.CopiedObjects - 1;

            var delta = (int)(job.CopiedObjects - _last.Value);

            // Status changes report too; only a moved counter is a copy.
            if (delta <= 0)
            {
                return;
            }

            Copied += delta;
            _last = job.CopiedObjects;

            if (_onCopied is not null)
            {
                await _onCopied(Copied);
            }
        }
    }
}

/// <summary>
/// Two object stores shared by the migration tests, started once for the class.
/// </summary>
/// <remarks>
/// Starting a pair per test cost about twenty seconds each; the tests reset
/// the buckets instead, which is what they actually need to be independent.
/// </remarks>
public sealed class StoragePair : IAsyncLifetime
{
    public IContainer Source { get; } = NeoPrintApiFixture.CloudServer();

    public IContainer Target { get; } = NeoPrintApiFixture.CloudServer();

    public async Task InitializeAsync()
    {
        if (new DockerFactAttribute().Skip is not null)
        {
            return;
        }

        await Task.WhenAll(Source.StartAsync(), Target.StartAsync());
    }

    public async Task DisposeAsync()
        => await Task.WhenAll(Source.DisposeAsync().AsTask(), Target.DisposeAsync().AsTask());

    /// <summary>Empties the store and makes sure the three buckets exist.</summary>
    public static async Task ResetAsync(IContainer store)
    {
        using var client = StorageMigrationTests.Direct(store);

        foreach (var bucket in new[] { NeoPrintApiFixture.FilesBucket, NeoPrintApiFixture.ReceiptsBucket, NeoPrintApiFixture.TempBucket })
        {
            try
            {
                var listing = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });

                foreach (var entry in listing.S3Objects ?? [])
                {
                    await client.DeleteObjectAsync(bucket, entry.Key);
                }
            }
            catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchBucket")
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            }
        }
    }
}
