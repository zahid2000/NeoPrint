using System.Reflection;

using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;

using MassTransit;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Crypto;
using NeoPrint.Infrastructure.Crypto;
using NeoPrint.Infrastructure.HealthChecks;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.SecretStore;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure;

/// <summary>
/// Wires up everything that talks to the outside world.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers MongoDB, Redis, RabbitMQ, Hangfire, the key ring and the
    /// health checks.
    /// </summary>
    /// <remarks>
    /// Every connection string comes from <see cref="IConfiguration"/>, which at
    /// this point is fed by OpenBao (plan section 2.13). Nothing here reads an
    /// environment variable.
    /// </remarks>
    public static IServiceCollection AddNeoPrintInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IClock, SystemClock>();
        services.AddHttpClient();

        AddMongo(services, configuration);
        AddRedis(services, configuration);
        AddCrypto(services);
        AddMessaging(services, configuration);
        AddBackgroundJobs(services, configuration);
        AddHealthChecks(services, configuration);

        return services;
    }

    private static void AddMongo(IServiceCollection services, IConfiguration configuration)
    {
        MongoContext.RegisterConventions();

        var uri = Required(configuration, "NEOPRINT_MONGO_URI");
        var url = MongoUrl.Create(uri);
        var databaseName = string.IsNullOrWhiteSpace(url.DatabaseName) ? "neoprint" : url.DatabaseName;

        services.AddSingleton<IMongoClient>(_ => new MongoClient(url));
        services.AddSingleton(provider => new MongoContext(provider.GetRequiredService<IMongoClient>(), databaseName));
        services.AddScoped<ITransactionRunner, MongoTransactionRunner>();
    }

    private static void AddRedis(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = Required(configuration, "NEOPRINT_REDIS");

        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            // Do not fail startup because Redis is a second late; the
            // multiplexer reconnects on its own.
            options.AbortOnConnectFail = false;
            options.ConnectRetry = 5;
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddStackExchangeRedisCache(options => options.Configuration = connectionString);
    }

    private static void AddCrypto(IServiceCollection services)
    {
        // The key ring is rebuilt whenever the configuration provider picks up
        // a rotation, so a new key takes effect without a restart.
        services.AddSingleton<IKeyEncryptionKeyProvider, ConfigurationKeyRingProvider>();
        services.AddSingleton(provider => new EnvelopeEncryptor(
            provider.GetRequiredService<IKeyEncryptionKeyProvider>()));
    }

    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var rabbitUri = Required(configuration, "NEOPRINT_RABBITMQ_URI");
        var mongoUri = Required(configuration, "NEOPRINT_MONGO_URI");
        var databaseName = MongoUrl.Create(mongoUri).DatabaseName is { Length: > 0 } name ? name : "neoprint";

        services.AddMassTransit(bus =>
        {
            // Transactional outbox on MongoDB: an event published in the same
            // transaction as the state change cannot be lost (plan section 10).
            bus.AddMongoDbOutbox(outbox =>
            {
                outbox.ClientFactory(provider => provider.GetRequiredService<IMongoClient>());
                outbox.DatabaseFactory(provider =>
                    provider.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UseBusOutbox();
            });

            bus.SetKebabCaseEndpointNameFormatter();

            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(new Uri(rabbitUri));

                // Failed messages are retried, then land in the _error queue,
                // whose depth the admin dashboard shows (plan section 6).
                rabbit.UseMessageRetry(retry => retry.Exponential(
                    retryLimit: 5,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromMinutes(2),
                    intervalDelta: TimeSpan.FromSeconds(5)));

                rabbit.ConfigureEndpoints(context);
            });
        });
    }

    private static void AddBackgroundJobs(IServiceCollection services, IConfiguration configuration)
    {
        var mongoUri = Required(configuration, "NEOPRINT_MONGO_URI");
        var url = MongoUrl.Create(mongoUri);
        var databaseName = string.IsNullOrWhiteSpace(url.DatabaseName) ? "neoprint" : url.DatabaseName;

        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseMongoStorage(mongoUri, databaseName, new MongoStorageOptions
            {
                MigrationOptions = new MongoMigrationOptions
                {
                    MigrationStrategy = new MigrateMongoMigrationStrategy(),
                    BackupStrategy = new CollectionMongoBackupStrategy(),
                },
                CheckConnection = false,
                Prefix = "hangfire",
            }));

        services.AddHangfireServer(options => options.ServerName = $"neoprint-{Environment.MachineName}");
    }

    private static void AddHealthChecks(IServiceCollection services, IConfiguration configuration)
    {
        var rabbitUri = configuration["NEOPRINT_RABBITMQ_URI"];

        var builder = services.AddHealthChecks()
            .AddCheck<MongoHealthCheck>("mongo", tags: ["ready", "infrastructure"])
            .AddCheck<RedisHealthCheck>("redis", tags: ["ready", "infrastructure"]);

        if (!string.IsNullOrWhiteSpace(rabbitUri) && Uri.TryCreate(rabbitUri, UriKind.Absolute, out var amqp))
        {
            builder.Add(new HealthCheckRegistration(
                "rabbitmq",
                provider => new RabbitMqHealthCheck(provider.GetRequiredService<IHttpClientFactory>(), amqp),
                HealthStatus.Unhealthy,
                ["ready", "infrastructure"]));
        }

        builder.Add(new HealthCheckRegistration(
            "openbao",
            provider => new OpenBaoHealthCheck(
                provider.GetRequiredService<ISecretStore>(),
                provider.GetRequiredService<OpenBaoOptions>()),
            // Degraded, not unhealthy: the service keeps running on the secrets
            // it already loaded (plan section 2.13).
            HealthStatus.Degraded,
            ["ready", "secrets"]));
    }

    /// <summary>Registers every <see cref="IValidator{T}"/> in an assembly.</summary>
    public static IServiceCollection AddValidatorsFrom(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var contract in type.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
            {
                services.AddTransient(contract, type);
            }
        }

        return services;
    }

    /// <summary>
    /// Reads a value the application cannot start without.
    /// </summary>
    /// <remarks>
    /// The configuration provider has already refused to start when a mandatory
    /// secret is missing; this is the second line of defence, and it names the
    /// key without its value.
    /// </remarks>
    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The mandatory secret '{key}' is not configured. It is read from OpenBao (docs/OPENBAO.md).");
        }

        return value;
    }
}
