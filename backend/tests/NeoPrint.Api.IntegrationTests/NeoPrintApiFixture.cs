using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Seeding;

using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The whole API against real infrastructure in containers (plan section 11).
/// </summary>
/// <remarks>
/// <para>
/// MongoDB runs as a single-node replica set, because the MassTransit outbox
/// and the wallet updates need transactions. OpenBao runs in dev mode and is
/// seeded exactly the way <c>openbao/init/init.sh</c> seeds it, so the API
/// takes its secrets from a real secret store here too rather than from a test
/// shortcut - which is the point of the exercise.
/// </para>
/// <para>
/// Shared across a test collection: starting five containers per test would
/// make the suite unusable.
/// </para>
/// </remarks>
public sealed class NeoPrintApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string RootToken = "neoprint-integration-root";

    private MongoDbContainer? _mongo;
    private IContainer? _storage;
    private RedisContainer? _redis;
    private RabbitMqContainer? _rabbit;
    private IContainer? _openBao;

    private string? _roleIdFile;
    private string? _secretIdFile;

    /// <summary>True when Docker is unavailable and the fixture did not start.</summary>
    public bool Skipped { get; private set; }

    public async Task InitializeAsync()
    {
        if (new DockerFactAttribute().Skip is not null)
        {
            Skipped = true;
            return;
        }

        // A single-node replica set: the outbox and the wallet updates need
        // transactions, which MongoDB only offers on a replica set.
        _mongo = new MongoDbBuilder("mongo:7.0.43").WithReplicaSet("rs0").Build();
        _redis = new RedisBuilder("redis:7.2.16-alpine").Build();
        _rabbit = new RabbitMqBuilder("rabbitmq:3.13-management").Build();

        // Object storage, so the encryption is exercised against a real S3
        // server rather than a stub: what "the stored object is unreadable"
        // means is what the server hands back, not what a fake decides to
        // return.
        //
        // RustFS, the image docker-compose.yml pins, so a difference between
        // the development store and the tested one cannot hide here
        // (docs/DECISIONS.md: why RustFS rather than MinIO or Zenko).
        _storage = RustFs();

        _openBao = new ContainerBuilder("openbao/openbao:2.6.2")
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", RootToken)
            .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
            .WithCommand("server", "-dev")
            .WithPortBinding(8200, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(8200).ForPath("/v1/sys/health").ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

        await Task.WhenAll(
            _mongo.StartAsync(),
            _redis.StartAsync(),
            _rabbit.StartAsync(),
            _storage.StartAsync(),
            _openBao.StartAsync());

        await CreateBucketsAsync();
        await SeedOpenBaoAsync();

        // Force the host to build, so startup (indexes and seeding) has run
        // before the first test touches an endpoint.
        _ = Server;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        foreach (var file in new[] { _roleIdFile, _secretIdFile })
        {
            if (file is not null && File.Exists(file))
            {
                File.Delete(file);
            }
        }

        await Task.WhenAll(
            _mongo?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _redis?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _rabbit?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _storage?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _openBao?.DisposeAsync().AsTask() ?? Task.CompletedTask);
    }

    // -----------------------------------------------------------------------
    // OpenBao, set up as openbao/init/init.sh does
    // -----------------------------------------------------------------------

    private string OpenBaoAddress =>
        $"http://{_openBao!.Hostname}:{_openBao.GetMappedPublicPort(8200)}";

    private async Task SeedOpenBaoAsync()
    {
        using var bao = new HttpClient { BaseAddress = new Uri(OpenBaoAddress + "/") };
        bao.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        bao.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        (await bao.PostAsJsonAsync("v1/sys/mounts/neoprint", new
        {
            type = "kv",
            options = new { version = "2" },
        })).EnsureSuccessStatusCode();

        (await bao.PostAsJsonAsync("v1/sys/auth/approle", new { type = "approle" }))
            .EnsureSuccessStatusCode();

        (await bao.PutAsJsonAsync("v1/sys/policies/acl/neoprint-api", new
        {
            policy = """
                path "neoprint/data/backend/*" { capabilities = ["read"] }
                path "auth/token/renew-self"   { capabilities = ["update"] }
                path "auth/token/lookup-self"  { capabilities = ["read"] }
                """,
        })).EnsureSuccessStatusCode();

        (await bao.PostAsJsonAsync("v1/auth/approle/role/neoprint-api", new
        {
            token_policies = "neoprint-api",
            token_ttl = "1h",
            token_max_ttl = "24h",
            secret_id_num_uses = 0,
            secret_id_ttl = 0,
        })).EnsureSuccessStatusCode();

        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        await WriteAsync(bao, "backend/core", new Dictionary<string, string>
        {
            ["NEOPRINT_MONGO_URI"] = _mongo!.GetConnectionString(),
            ["NEOPRINT_REDIS"] = _redis!.GetConnectionString(),
            ["NEOPRINT_RABBITMQ_URI"] = _rabbit!.GetConnectionString(),
            ["NEOPRINT_JWT_SIGNING_KEY"] = key,
        });

        await WriteAsync(bao, "backend/crypto", new Dictionary<string, string>
        {
            ["NEOPRINT_KEK_CURRENT_ID"] = "k1",
            ["NEOPRINT_KEK__k1"] = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["NEOPRINT_STORAGE_SECRETS_KEY"] = key,
            ["NEOPRINT_DATA_PROTECTION_KEY"] = key,
            ["NEOPRINT_QR_HMAC_KEY"] = key,
        });

        await WriteAsync(bao, "backend/providers", new Dictionary<string, string>
        {
            // No SMTP host: the email sender logs a warning instead of sending,
            // which is what these tests want.
            ["NEOPRINT_SMTP_HOST"] = string.Empty,

            // The mock bank signs its callbacks with this (plan section 10).
            ["NEOPRINT_BANK_CALLBACK_SECRET"] = BankCallbackSecret,
        });

        await WriteAsync(bao, "backend/seed", new Dictionary<string, string>
        {
            ["NEOPRINT_SEED_ADMIN_EMAIL"] = SeedAdminEmail,
            ["NEOPRINT_SEED_ADMIN_PASSWORD"] = SeedAdminPassword,
            [DevelopmentSeeder.TestPasswordKey] = DevTestPassword,

            // The application seeds its first storage provider from these, as
            // it does in production, rather than the test inserting a
            // document the application never validated.
            ["NEOPRINT_STORAGE_SEED_TYPE"] = "RustFS",
            ["NEOPRINT_STORAGE_SEED_NAME"] = "Test object store",
            ["NEOPRINT_STORAGE_SEED_ENDPOINT"] = StorageEndpoint,
            ["NEOPRINT_STORAGE_SEED_REGION"] = "us-east-1",
            ["NEOPRINT_STORAGE_SEED_USE_SSL"] = "false",
            ["NEOPRINT_STORAGE_SEED_ACCESS_KEY"] = StorageAccessKey,
            ["NEOPRINT_STORAGE_SEED_SECRET_KEY"] = StorageSecretKey,
            ["NEOPRINT_STORAGE_SEED_BUCKET_FILES"] = FilesBucket,
            ["NEOPRINT_STORAGE_SEED_BUCKET_RECEIPTS"] = ReceiptsBucket,
            ["NEOPRINT_STORAGE_SEED_BUCKET_TEMP"] = TempBucket,
        });

        var roleIdResponse = await bao.GetFromJsonAsync<JsonElement>("v1/auth/approle/role/neoprint-api/role-id");
        var roleId = roleIdResponse.GetProperty("data").GetProperty("role_id").GetString()!;

        var secretIdResponse = await bao.PostAsJsonAsync("v1/auth/approle/role/neoprint-api/secret-id", new { });
        secretIdResponse.EnsureSuccessStatusCode();
        var secretId = (await secretIdResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("secret_id").GetString()!;

        // The API reads its credentials from files, exactly as it does in
        // production where they arrive as Docker secrets.
        _roleIdFile = Path.Combine(Path.GetTempPath(), $"neoprint-test-{Guid.NewGuid():N}.role_id");
        _secretIdFile = Path.Combine(Path.GetTempPath(), $"neoprint-test-{Guid.NewGuid():N}.secret_id");
        await File.WriteAllTextAsync(_roleIdFile, roleId);
        await File.WriteAllTextAsync(_secretIdFile, secretId);
    }

    private static async Task WriteAsync(HttpClient bao, string path, Dictionary<string, string> data)
        => (await bao.PostAsJsonAsync($"v1/neoprint/data/{path}", new { data })).EnsureSuccessStatusCode();

    /// <summary>The shared secret the mock bank signs callbacks with.</summary>
    public const string BankCallbackSecret = "test-bank-callback-secret";

    /// <summary>The seeded SuperAdmin, which must change its password at first sign-in.</summary>
    public const string SeedAdminEmail = "admin@neoprint.test";

    /// <summary>The password of the development test accounts (DevelopmentSeeder).</summary>
    public const string DevTestPassword = "DevTest!2026";

    public const string SeedAdminPassword = "IntegrationSeed!2026";

    // -----------------------------------------------------------------------
    // Object storage
    // -----------------------------------------------------------------------

    public const string StorageAccessKey = "neoprinttest";

    public const string StorageSecretKey = "neoprinttestsecret";

    public const string FilesBucket = "neoprint-files";

    public const string ReceiptsBucket = "neoprint-receipts";

    public const string TempBucket = "neoprint-temp";

    /// <summary>
    /// The Mongo replica set, for tests that need a database of their own next
    /// to the application's rather than sharing its collections.
    /// </summary>
    public string MongoConnectionString => _mongo!.GetConnectionString();

    /// <summary>The image docker-compose.yml pins; the two must not drift apart.</summary>
    public const string RustFsImage =
        "rustfs/rustfs:1.0.0@sha256:8cc9801755448b71a786705ce76692c77e14936cccd87cf2fc31842e58f4d1ff";

    /// <summary>The S3 port inside the container.</summary>
    public const int StoragePort = 9000;

    /// <summary>
    /// A RustFS container, as the development environment runs it.
    /// </summary>
    /// <remarks>
    /// Ready means a signed ListBuckets succeeds - the question every test
    /// actually asks - rather than that the port is open: credentials and
    /// the S3 API are what the tests depend on, not the process being up.
    /// </remarks>
    public static IContainer RustFs() =>
        new ContainerBuilder(RustFsImage)
            .WithEnvironment("RUSTFS_ACCESS_KEY", StorageAccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", StorageSecretKey)
            // No web console, and logs to stdout rather than a directory.
            .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
            .WithEnvironment("RUSTFS_OBS_LOG_DIRECTORY", string.Empty)
            .WithPortBinding(StoragePort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new S3ApiReady()))
            .Build();

    /// <summary>Waits until the container answers a signed S3 request.</summary>
    private sealed class S3ApiReady : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            using var client = new AmazonS3Client(
                new BasicAWSCredentials(StorageAccessKey, StorageSecretKey),
                new AmazonS3Config
                {
                    ServiceURL = $"http://{container.Hostname}:{container.GetMappedPublicPort(StoragePort)}",
                    ForcePathStyle = true,
                    AuthenticationRegion = "us-east-1",
                    UseHttp = true,
                    MaxErrorRetry = 0,
                    Timeout = TimeSpan.FromSeconds(3),
                });

            try
            {
                await client.ListBucketsAsync();
                return true;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                return false;
            }
        }
    }

    /// <summary>Where the object store is listening, from the test process.</summary>
    public string StorageEndpoint =>
        $"http://{_storage!.Hostname}:{_storage.GetMappedPublicPort(StoragePort)}";

    /// <summary>
    /// A client straight to the object store, bypassing the application.
    /// </summary>
    /// <remarks>
    /// This is what proves the encryption is real: a test reads the object the
    /// way anybody with the storage credentials would, and has to find
    /// ciphertext.
    /// </remarks>
    public IAmazonS3 CreateDirectStorageClient() => new AmazonS3Client(
        new BasicAWSCredentials(StorageAccessKey, StorageSecretKey),
        new AmazonS3Config
        {
            ServiceURL = StorageEndpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    /// <summary>
    /// Creates the three buckets, as the init container does for the
    /// development environment.
    /// </summary>
    private async Task CreateBucketsAsync()
    {
        using var client = CreateDirectStorageClient();

        foreach (var bucket in new[] { FilesBucket, ReceiptsBucket, TempBucket })
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }
    }

    // -----------------------------------------------------------------------
    // Host configuration
    // -----------------------------------------------------------------------

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Bootstrap settings only; every secret comes from OpenBao, as in
        // production (plan section 2.13).
        Environment.SetEnvironmentVariable("BAO_ADDR", OpenBaoAddress);
        Environment.SetEnvironmentVariable("BAO_KV_MOUNT", "neoprint");
        Environment.SetEnvironmentVariable("BAO_ROLE_ID_FILE", _roleIdFile);
        Environment.SetEnvironmentVariable("BAO_SECRET_ID_FILE", _secretIdFile);

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Every test comes from the same address, so the real per-IP limits
        // would throttle the suite rather than the thing under test. The
        // limiter itself is covered by its own test, which turns it back on.
        builder.UseSetting("RateLimits:Disabled", "true");

        // The mock bank calls back over HTTP; here the test server answers.
        builder.UseSetting("Payments:MockBank:CallbackBaseUrl", "http://localhost");

        builder.ConfigureLogging(logging => logging
            .ClearProviders()
            // Warnings and above: the seeding and Hangfire chatter would bury
            // the test output otherwise.
            .SetMinimumLevel(LogLevel.Warning));

        builder.ConfigureTestServices(services =>
        {
            // Keeps what would have been emailed, so a test can read a
            // confirmation code the way the person receiving it does. There is
            // no SMTP server in the test environment; Mailpit is the
            // development one (plan section 4).
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<CapturedEmails>();
            services.AddSingleton<IEmailSender>(
                provider => provider.GetRequiredService<CapturedEmails>());

            // The mock bank's callbacks reach the in-memory server, exactly
            // as they reach the API over the network in Development.
            services.AddHttpClient(NeoPrint.Infrastructure.Payments.MockBankSimulator.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    /// <summary>The emails the API tried to send during a test.</summary>
    public CapturedEmails Emails => Services.GetRequiredService<CapturedEmails>();

    /// <summary>A client that does not follow redirects, so status codes are visible.</summary>
    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });
}

/// <summary>
/// Shares one fixture across the API tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<NeoPrintApiFixture>
{
    public const string Name = "neoprint-api";
}
