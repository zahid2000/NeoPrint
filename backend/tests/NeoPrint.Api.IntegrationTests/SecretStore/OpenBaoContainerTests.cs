using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using NeoPrint.Infrastructure.SecretStore;

namespace NeoPrint.Api.IntegrationTests.SecretStore;

/// <summary>
/// The secret store against a real OpenBao server (plan section 11): AppRole
/// login, KV v2 reads, token renewal, refusing to start without a mandatory
/// key, and a rotated secret arriving without a restart.
/// </summary>
public sealed class OpenBaoContainerTests : IAsyncLifetime
{
    private const string RootToken = "neoprint-test-root-token";
    private const string Image = "openbao/openbao:2.6.2";

    private IContainer? _container;
    private HttpClient? _root;

    private HttpClient Root => _root ?? throw new InvalidOperationException("The container was not started.");

    private string Address => $"http://{_container!.Hostname}:{_container.GetMappedPublicPort(8200)}";

    public async Task InitializeAsync()
    {
        if (new DockerFactAttribute().Skip is not null)
        {
            return;
        }

        _container = new ContainerBuilder(Image)
            // Dev mode: in-memory, already unsealed, fixed root token. Only ever
            // used for tests - production is configured as docs/OPENBAO.md says.
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", RootToken)
            .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
            .WithCommand("server", "-dev")
            .WithPortBinding(8200, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(8200).ForPath("/v1/sys/health").ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

        await _container.StartAsync();

        _root = new HttpClient { BaseAddress = new Uri(Address + "/") };
        Root.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        Root.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task DisposeAsync()
    {
        _root?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Server setup, mirroring openbao/init/init.sh
    // -----------------------------------------------------------------------

    private async Task SetUpMountAndRoleAsync()
    {
        (await Root.PostAsJsonAsync("v1/sys/mounts/neoprint", new
        {
            type = "kv",
            options = new { version = "2" },
        })).EnsureSuccessStatusCode();

        (await Root.PostAsJsonAsync("v1/sys/auth/approle", new { type = "approle" }))
            .EnsureSuccessStatusCode();

        // The same read-only shape as openbao/policies/neoprint-api.hcl.
        (await Root.PutAsJsonAsync("v1/sys/policies/acl/neoprint-api", new
        {
            policy = """
                path "neoprint/data/backend/*" { capabilities = ["read"] }
                path "auth/token/renew-self"   { capabilities = ["update"] }
                path "auth/token/lookup-self"  { capabilities = ["read"] }
                """,
        })).EnsureSuccessStatusCode();

        (await Root.PostAsJsonAsync("v1/auth/approle/role/neoprint-api", new
        {
            token_policies = "neoprint-api",
            token_ttl = "1h",
            token_max_ttl = "24h",
            secret_id_num_uses = 0,
            secret_id_ttl = 0,
        })).EnsureSuccessStatusCode();
    }

    private async Task<(string RoleId, string SecretId)> IssueCredentialsAsync()
    {
        var roleIdResponse = await Root.GetFromJsonAsync<JsonElement>("v1/auth/approle/role/neoprint-api/role-id");
        var roleId = roleIdResponse.GetProperty("data").GetProperty("role_id").GetString()!;

        var secretIdResponse = await Root.PostAsJsonAsync(
            "v1/auth/approle/role/neoprint-api/secret-id", new { });
        secretIdResponse.EnsureSuccessStatusCode();
        var secretIdJson = await secretIdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secretId = secretIdJson.GetProperty("data").GetProperty("secret_id").GetString()!;

        return (roleId, secretId);
    }

    private async Task WriteSecretsAsync(string path, object data)
    {
        (await Root.PostAsJsonAsync($"v1/neoprint/data/{path}", new { data }))
            .EnsureSuccessStatusCode();
    }

    private OpenBaoOptions OptionsFor((string RoleId, string SecretId) credentials, params string[] paths) => new()
    {
        Address = Address,
        RoleId = credentials.RoleId,
        SecretId = credentials.SecretId,
        KvMount = "neoprint",
        Paths = [.. paths],
        StartupTimeout = TimeSpan.FromSeconds(10),
        InitialRetryDelay = TimeSpan.FromMilliseconds(50),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        RefreshInterval = TimeSpan.FromMilliseconds(200),
    };

    private async Task SeedEveryMandatorySecretAsync()
    {
        await WriteSecretsAsync("backend/core", new Dictionary<string, string>
        {
            ["NEOPRINT_MONGO_URI"] = "mongodb://mongo:27017/neoprint",
            ["NEOPRINT_REDIS"] = "redis:6379",
            ["NEOPRINT_RABBITMQ_URI"] = "amqp://rabbitmq:5672/",
            ["NEOPRINT_JWT_SIGNING_KEY"] = Convert.ToBase64String(new byte[32]),
        });

        await WriteSecretsAsync("backend/crypto", new Dictionary<string, string>
        {
            ["NEOPRINT_KEK_CURRENT_ID"] = "k1",
            ["NEOPRINT_KEK__k1"] = Convert.ToBase64String(new byte[32]),
            ["NEOPRINT_STORAGE_SECRETS_KEY"] = Convert.ToBase64String(new byte[32]),
            ["NEOPRINT_DATA_PROTECTION_KEY"] = Convert.ToBase64String(new byte[32]),
            ["NEOPRINT_QR_HMAC_KEY"] = Convert.ToBase64String(new byte[32]),
        });
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Logs_in_with_approle_and_reads_the_secrets()
    {
        await SetUpMountAndRoleAsync();
        await SeedEveryMandatorySecretAsync();
        var credentials = await IssueCredentialsAsync();

        using var http = new HttpClient();
        var options = OptionsFor(credentials, "backend/core", "backend/crypto");
        using var client = new OpenBaoClient(http, options);

        var core = await client.ReadAsync("backend/core");

        core["NEOPRINT_MONGO_URI"].Should().Be("mongodb://mongo:27017/neoprint");
        core["NEOPRINT_REDIS"].Should().Be("redis:6379");
    }

    [DockerFact]
    public async Task Renews_its_own_token()
    {
        await SetUpMountAndRoleAsync();
        await SeedEveryMandatorySecretAsync();
        var credentials = await IssueCredentialsAsync();

        using var http = new HttpClient();
        using var client = new OpenBaoClient(http, OptionsFor(credentials, "backend/core"));

        // The policy grants auth/token/renew-self; several reads in a row must
        // keep working on one lease.
        for (var i = 0; i < 3; i++)
        {
            (await client.ReadAsync("backend/core")).Should().NotBeEmpty();
        }
    }

    [DockerFact]
    public async Task The_policy_really_is_read_only()
    {
        await SetUpMountAndRoleAsync();
        await SeedEveryMandatorySecretAsync();
        var credentials = await IssueCredentialsAsync();

        // Log in as the application role and try to write.
        using var http = new HttpClient { BaseAddress = new Uri(Address + "/") };
        var login = await http.PostAsJsonAsync("v1/auth/approle/login", new
        {
            role_id = credentials.RoleId,
            secret_id = credentials.SecretId,
        });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("auth").GetProperty("client_token").GetString();

        using var write = new HttpRequestMessage(HttpMethod.Post, "v1/neoprint/data/backend/core")
        {
            Content = JsonContent.Create(new { data = new Dictionary<string, string> { ["INJECTED"] = "x" } }),
        };
        write.Headers.Add("X-Vault-Token", token);

        var response = await http.SendAsync(write);

        response.IsSuccessStatusCode.Should().BeFalse(
            "plan section 2.13: the application's role must not be able to write a secret");
    }

    [DockerFact]
    public async Task Configuration_provider_loads_every_path()
    {
        await SetUpMountAndRoleAsync();
        await SeedEveryMandatorySecretAsync();
        var credentials = await IssueCredentialsAsync();

        using var http = new HttpClient();
        var options = OptionsFor(credentials, "backend/core", "backend/crypto");
        using var client = new OpenBaoClient(http, options);
        using var provider = new OpenBaoConfigurationProvider(client, options, validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);

        provider.TryGet("NEOPRINT_MONGO_URI", out var mongo).Should().BeTrue();
        mongo.Should().Be("mongodb://mongo:27017/neoprint");
        provider.TryGet("NEOPRINT_KEK__k1", out _).Should().BeTrue();
    }

    [DockerFact]
    public async Task Does_not_start_when_a_mandatory_secret_is_absent_from_the_real_store()
    {
        await SetUpMountAndRoleAsync();
        // Everything except NEOPRINT_JWT_SIGNING_KEY.
        await WriteSecretsAsync("backend/core", new Dictionary<string, string>
        {
            ["NEOPRINT_MONGO_URI"] = "mongodb://mongo:27017/neoprint",
            ["NEOPRINT_REDIS"] = "redis:6379",
            ["NEOPRINT_RABBITMQ_URI"] = "amqp://rabbitmq:5672/",
        });
        var credentials = await IssueCredentialsAsync();

        using var http = new HttpClient();
        var options = OptionsFor(credentials, "backend/core");
        using var client = new OpenBaoClient(http, options);
        using var provider = new OpenBaoConfigurationProvider(client, options, validateRequiredSecrets: true);

        var act = () => provider.LoadAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<MissingSecretsException>())
            .Which.MissingKeys.Should().Contain("NEOPRINT_JWT_SIGNING_KEY");
    }

    [DockerFact]
    public async Task A_secret_rotated_in_the_store_reaches_configuration_without_a_restart()
    {
        await SetUpMountAndRoleAsync();
        await SeedEveryMandatorySecretAsync();
        await WriteSecretsAsync("backend/providers", new Dictionary<string, string>
        {
            ["NEOPRINT_TELEGRAM_BOT_TOKEN"] = "before-rotation",
        });
        var credentials = await IssueCredentialsAsync();

        using var http = new HttpClient();
        var options = OptionsFor(credentials, "backend/core", "backend/crypto", "backend/providers");
        using var client = new OpenBaoClient(http, options);
        using var provider = new OpenBaoConfigurationProvider(client, options, validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);
        provider.TryGet("NEOPRINT_TELEGRAM_BOT_TOKEN", out var before).Should().BeTrue();
        before.Should().Be("before-rotation");

        var reloaded = new TaskCompletionSource();
        using var registration = provider.GetReloadToken().RegisterChangeCallback(_ => reloaded.TrySetResult(), null);

        // The operator rotates the token with `bao kv put`.
        await WriteSecretsAsync("backend/providers", new Dictionary<string, string>
        {
            ["NEOPRINT_TELEGRAM_BOT_TOKEN"] = "after-rotation",
        });

        provider.StartRefreshing();
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30));

        provider.TryGet("NEOPRINT_TELEGRAM_BOT_TOKEN", out var after).Should().BeTrue();
        after.Should().Be("after-rotation");
    }
}
