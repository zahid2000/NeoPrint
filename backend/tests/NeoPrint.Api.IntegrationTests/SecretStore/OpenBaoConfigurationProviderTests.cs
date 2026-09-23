using NeoPrint.Infrastructure.SecretStore;

namespace NeoPrint.Api.IntegrationTests.SecretStore;

/// <summary>
/// Startup behaviour of the configuration provider (plan sections 2.13, 11):
/// retry, refusing to start without a mandatory secret, refreshing rotated
/// values, and carrying on when the store drops out.
/// </summary>
public sealed class OpenBaoConfigurationProviderTests
{
    /// <summary>
    /// Hand-written secret store (plan section 11: no mocking library).
    /// </summary>
    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, Dictionary<string, string?>> _paths = new(StringComparer.Ordinal);

        public int FailuresRemaining { get; set; }

        public int ReadCount { get; private set; }

        public FakeSecretStore Set(string path, params (string Key, string? Value)[] entries)
        {
            _paths[path] = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            return this;
        }

        public Task<IReadOnlyDictionary<string, string?>> ReadAsync(
            string path, CancellationToken cancellationToken = default)
        {
            ReadCount++;

            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new SecretStoreUnavailableException("the store is not up yet");
            }

            IReadOnlyDictionary<string, string?> result = _paths.TryGetValue(path, out var values)
                ? values
                : new Dictionary<string, string?>(StringComparer.Ordinal);

            return Task.FromResult(result);
        }
    }

    private static OpenBaoOptions Options(params string[] paths) => new()
    {
        Address = "http://openbao.test:8200",
        RoleId = "r",
        SecretId = "s",
        Paths = paths.Length == 0 ? ["backend/core"] : [.. paths],
        // Short, so the retry paths run fast; the backoff shape is the same
        // as in production, only the scale differs.
        StartupTimeout = TimeSpan.FromSeconds(5),
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(40),
        RefreshInterval = TimeSpan.FromMilliseconds(50),
    };

    private static FakeSecretStore StoreWithEveryMandatorySecret()
    {
        var store = new FakeSecretStore();
        store.Set("backend/core",
            ("NEOPRINT_MONGO_URI", "mongodb://m"),
            ("NEOPRINT_REDIS", "redis:6379"),
            ("NEOPRINT_RABBITMQ_URI", "amqp://r"),
            ("NEOPRINT_JWT_SIGNING_KEY", "signing-key"));
        store.Set("backend/crypto",
            ("NEOPRINT_KEK_CURRENT_ID", "k1"),
            ("NEOPRINT_KEK__k1", "kek-material"),
            ("NEOPRINT_STORAGE_SECRETS_KEY", "storage-key"),
            ("NEOPRINT_DATA_PROTECTION_KEY", "dp-key"),
            ("NEOPRINT_QR_HMAC_KEY", "qr-key"));
        return store;
    }

    [Fact]
    public async Task Loads_every_configured_path_into_configuration()
    {
        var store = StoreWithEveryMandatorySecret();
        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);

        provider.TryGet("NEOPRINT_MONGO_URI", out var mongo).Should().BeTrue();
        mongo.Should().Be("mongodb://m");
        provider.TryGet("NEOPRINT_QR_HMAC_KEY", out _).Should().BeTrue();
        provider.HasValues.Should().BeTrue();
    }

    [Fact]
    public async Task Configuration_lookup_is_case_insensitive_like_the_rest_of_configuration()
    {
        var store = StoreWithEveryMandatorySecret();
        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);

        provider.TryGet("neoprint_mongo_uri", out var value).Should().BeTrue();
        value.Should().Be("mongodb://m");
    }

    [Fact]
    public async Task Retries_while_the_store_is_still_coming_up()
    {
        var store = StoreWithEveryMandatorySecret();
        store.FailuresRemaining = 3;

        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);

        provider.HasValues.Should().BeTrue("startup must survive OpenBao booting a little later than the API");
    }

    [Fact]
    public async Task Does_not_start_when_the_store_stays_unreachable()
    {
        var store = StoreWithEveryMandatorySecret();
        store.FailuresRemaining = int.MaxValue;

        using var provider = new OpenBaoConfigurationProvider(store, Options(), validateRequiredSecrets: true);

        var act = () => provider.LoadAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<SecretStoreUnavailableException>())
            .Which.Message.Should().Contain("did not become available");
    }

    [Fact]
    public async Task Starts_without_the_store_only_when_it_is_explicitly_optional()
    {
        var store = StoreWithEveryMandatorySecret();
        store.FailuresRemaining = int.MaxValue;

        var options = Options();
        options.Optional = true;

        using var provider = new OpenBaoConfigurationProvider(store, options, validateRequiredSecrets: false);

        await provider.LoadAsync(CancellationToken.None);

        provider.HasValues.Should().BeFalse();
    }

    [Fact]
    public async Task Does_not_start_when_a_mandatory_secret_is_missing_and_says_which_one()
    {
        var store = StoreWithEveryMandatorySecret();
        store.Set("backend/core",
            ("NEOPRINT_MONGO_URI", "mongodb://m"),
            ("NEOPRINT_REDIS", "redis:6379"),
            ("NEOPRINT_RABBITMQ_URI", "amqp://r"));
        // NEOPRINT_JWT_SIGNING_KEY was deleted from the store.

        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        var act = () => provider.LoadAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MissingSecretsException>();
        exception.Which.MissingKeys.Should().Contain("NEOPRINT_JWT_SIGNING_KEY");
        exception.Which.Message.Should().Contain("NEOPRINT_JWT_SIGNING_KEY");
    }

    [Fact]
    public async Task Treats_a_present_but_empty_mandatory_secret_as_missing()
    {
        var store = StoreWithEveryMandatorySecret();
        store.Set("backend/core",
            ("NEOPRINT_MONGO_URI", "mongodb://m"),
            ("NEOPRINT_REDIS", "redis:6379"),
            ("NEOPRINT_RABBITMQ_URI", "amqp://r"),
            ("NEOPRINT_JWT_SIGNING_KEY", "   "));

        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        var act = () => provider.LoadAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<MissingSecretsException>())
            .Which.MissingKeys.Should().Contain("NEOPRINT_JWT_SIGNING_KEY");
    }

    [Fact]
    public async Task Does_not_start_when_the_current_key_encryption_key_was_never_written()
    {
        var store = StoreWithEveryMandatorySecret();
        store.Set("backend/crypto",
            // Points at k2, but only k1 exists.
            ("NEOPRINT_KEK_CURRENT_ID", "k2"),
            ("NEOPRINT_KEK__k1", "kek-material"),
            ("NEOPRINT_STORAGE_SECRETS_KEY", "storage-key"),
            ("NEOPRINT_DATA_PROTECTION_KEY", "dp-key"),
            ("NEOPRINT_QR_HMAC_KEY", "qr-key"));

        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        var act = () => provider.LoadAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<MissingSecretsException>())
            .Which.MissingKeys.Should().Contain("NEOPRINT_KEK__k2");
    }

    [Fact]
    public async Task Outside_production_a_missing_optional_secret_does_not_block_startup()
    {
        var store = new FakeSecretStore().Set("backend/core", ("NEOPRINT_MONGO_URI", "mongodb://m"));

        using var provider = new OpenBaoConfigurationProvider(store, Options(), validateRequiredSecrets: false);

        await provider.LoadAsync(CancellationToken.None);

        provider.HasValues.Should().BeTrue();
    }

    [Fact]
    public async Task A_rotated_secret_reaches_configuration_without_a_restart()
    {
        var store = StoreWithEveryMandatorySecret();
        store.Set("backend/providers", ("NEOPRINT_TELEGRAM_BOT_TOKEN", "old-token"));

        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto", "backend/providers"), validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);
        provider.TryGet("NEOPRINT_TELEGRAM_BOT_TOKEN", out var before).Should().BeTrue();
        before.Should().Be("old-token");

        var reloaded = new TaskCompletionSource();
        using var registration = provider.GetReloadToken().RegisterChangeCallback(_ => reloaded.TrySetResult(), null);

        // The operator rotates the token in OpenBao.
        store.Set("backend/providers", ("NEOPRINT_TELEGRAM_BOT_TOKEN", "new-token"));
        provider.StartRefreshing();

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        provider.TryGet("NEOPRINT_TELEGRAM_BOT_TOKEN", out var after).Should().BeTrue();
        after.Should().Be("new-token");
    }

    [Fact]
    public async Task Keeps_serving_the_last_known_values_when_the_store_drops_out()
    {
        var store = StoreWithEveryMandatorySecret();

        using var provider = new OpenBaoConfigurationProvider(
            store, Options("backend/core", "backend/crypto"), validateRequiredSecrets: true);

        await provider.LoadAsync(CancellationToken.None);
        var readsAfterLoad = store.ReadCount;

        // OpenBao goes away while the service is running.
        store.FailuresRemaining = int.MaxValue;
        provider.StartRefreshing();

        // Wait until the refresh loop has actually tried and failed.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.ReadCount <= readsAfterLoad && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        store.ReadCount.Should().BeGreaterThan(readsAfterLoad, "the refresh loop should have run");
        provider.TryGet("NEOPRINT_MONGO_URI", out var value).Should().BeTrue(
            "plan section 2.13: if OpenBao drops out at runtime the service carries on with the values it has");
        value.Should().Be("mongodb://m");
    }
}
