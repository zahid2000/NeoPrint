using System.Reflection;

using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;

using MassTransit;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

using Microsoft.AspNetCore.Identity;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Auth;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Features;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Crypto;
using NeoPrint.Infrastructure.Admin;
using NeoPrint.Infrastructure.Auth;
using NeoPrint.Infrastructure.Conversion;
using NeoPrint.Infrastructure.Crypto;
using NeoPrint.Infrastructure.Devices;
using NeoPrint.Infrastructure.Email;
using NeoPrint.Infrastructure.Features;
using NeoPrint.Infrastructure.HealthChecks;
using NeoPrint.Infrastructure.Observability;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.PrintJobs;
using NeoPrint.Infrastructure.SecretStore;
using NeoPrint.Infrastructure.Settings;
using NeoPrint.Infrastructure.Storage;

using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Users;

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

        // Scoped: one id per request, consumed message or job run
        // (docs/OBSERVABILITY.md).
        services.AddScoped<ICorrelationContext, CorrelationContext>();

        // Idempotency, from the first business operation rather than from
        // step 7 (docs/IDEMPOTENCY.md).
        services.AddScoped<IIdempotencyStore, MongoIdempotencyStore>();
        services.AddScoped<ISettingsStore, MongoSettingsStore>();
        services.AddScoped<IProviderEventStore, MongoProviderEventStore>();

        AddMongo(services, configuration);
        AddRedis(services, configuration);
        AddCrypto(services);
        AddStorage(services, configuration);
        AddMessaging(services, configuration);
        AddBackgroundJobs(services, configuration);
        AddFiscal(services, configuration);
        AddHealthChecks(services, configuration);
        AddAuth(services);
        AddPush(services, configuration);

        return services;
    }

    private static void AddAuth(IServiceCollection services)
    {
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<ISecretHasher, SecretHasher>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IEmailConfirmationRepository, EmailConfirmationRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<ILegalDocumentRepository, LegalDocumentRepository>();
        services.AddScoped<IAuthSessionFactory, AuthSessionFactory>();
        services.AddScoped<IEmailSender, MailKitEmailSender>();

        // Devices (plan section 7.11)
        services.AddSingleton<IDeviceSignatureVerifier, DeviceSignatureVerifier>();
        services.AddScoped<IDeviceChallengeStore, RedisDeviceChallengeStore>();
        services.AddScoped<IKioskTokenBlocklist, RedisKioskTokenBlocklist>();
        services.AddScoped<Application.Customers.IUserBlocklist, Auth.RedisUserBlocklist>();
        services.AddScoped<IDeviceAccessChecker, DeviceAccessChecker>();
        services.AddScoped<IKioskDeviceRepository, KioskDeviceRepository>();
        services.AddScoped<IEnrollmentTokenRepository, EnrollmentTokenRepository>();
        services.AddScoped<IKioskRepository, KioskRepository>();
        services.AddScoped<NeoPrint.Application.Kiosks.IKioskSearch, KioskSearch>();
        services.AddScoped<Application.Sessions.IKioskSessionStore, Sessions.RedisKioskSessionStore>();
        services.AddScoped<Application.Sessions.IKioskSessionAccess, Application.Sessions.KioskSessionHandlers>();

        // Print jobs, the queue and kiosk commands (plan step 3)
        services.AddScoped<IKioskCommandRepository, KioskCommandRepository>();
        services.AddScoped<IPrintJobRepository, PrintJobRepository>();
        services.AddScoped<IPrintAttemptRepository, PrintAttemptRepository>();
        services.AddScoped<Application.Kiosks.ISupplyEventRepository, SupplyEventRepository>();

        // Supplies, service tasks and the warehouse (plan section 2.15)
        services.AddScoped<Application.Supplies.ISupplyAlertRuleRepository, Supplies.SupplyAlertRuleRepository>();
        services.AddScoped<Application.Supplies.ISupplyAlertRepository, Supplies.SupplyAlertRepository>();
        services.AddScoped<Application.Supplies.IServiceTaskRepository, Supplies.ServiceTaskRepository>();
        services.AddScoped<Application.Supplies.IInventoryRepository, Supplies.InventoryRepository>();
        services.AddScoped<Application.Supplies.ISupplyUsageReader, Supplies.SupplyUsageReader>();
        services.AddScoped<Application.Supplies.ServiceTaskDesk>();
        services.AddScoped<Application.Supplies.SupplyOutlook>();
        services.AddScoped<Supplies.SupplyJobs>();
        services.AddScoped<Application.Analytics.IAnalyticsEventRepository, AnalyticsEventRepository>();
        services.AddScoped<Application.Kiosks.IKioskTelemetryStore, Kiosks.KioskTelemetryStore>();
        services.AddScoped<Application.Kiosks.IKioskStatusCache, Kiosks.RedisKioskStatusCache>();
        services.AddScoped<Application.Kiosks.IPrinterTelemetryStore, Kiosks.PrinterTelemetryStore>();
        services.AddScoped<Application.Kiosks.IPrinterEventStore, Kiosks.PrinterEventStore>();
        services.AddScoped<IPrintQueue, RedisPrintQueue>();
        services.AddScoped<ITransitionHistory, TransitionHistory>();
        services.AddScoped<PrintQueueService>();
        services.AddScoped<Application.PrintJobs.PrintOrderRules>();
        services.AddScoped<Application.PrintJobs.AppPaidOrders>();

        // Personal QR and files prepared for a kiosk (plan section 9.4).
        services.AddSingleton<Application.UserQr.IUserQrSigner, UserQr.HmacUserQrSigner>();
        services.AddSingleton<Application.UserQr.IQrImages, UserQr.QrCoderImages>();
        services.AddSingleton<Application.UserQr.UserQrCodes>();
        services.AddScoped<Application.UserQr.IUserQrGrantStore, UserQr.RedisUserQrGrantStore>();
        services.AddScoped<Application.PrintJobs.IPreparedPrintRepository, PreparedPrintRepository>();
        services.AddScoped<UserQr.UncollectedPrintJobsJob>();
        services.AddScoped<Application.Schedules.IScheduledPrintRepository, Schedules.ScheduledPrintRepository>();
        services.AddScoped<Application.Sharing.IShareRepository, Sharing.ShareRepository>();
        services.AddScoped<Application.Referrals.IReferralRepository, Referrals.ReferralRepository>();
        services.AddScoped<Schedules.ScheduledPrintDueJob>();
        services.AddScoped<Schedules.ScheduledPrintAutoCancelJob>();
        services.AddScoped<Application.Files.FileEraser>();
        services.AddScoped<Application.Files.FileIntake>();
        services.AddScoped<Files.FileAutoDeleteJob>();
        services.AddScoped<Application.Privacy.IDataExportRepository, Privacy.DataExportRepository>();
        services.AddScoped<Application.Privacy.IDataExportBuilder, Privacy.DataExportBuilder>();
        services.AddSingleton<Application.Privacy.IExportLinkSigner, Privacy.HmacExportLinkSigner>();
        services.AddScoped<Privacy.DataExportCleanupJob>();
        services.AddScoped<Application.Privacy.IPrivacyRecords, Privacy.PrivacyRecords>();
        services.AddScoped<Privacy.AccountDeletionJob>();
        services.AddScoped<PrintQueueReconciliationJob>();

        // Payments (plan section 2.5, step 4). The mock bank is the only bank
        // QR provider until a real one is contracted (docs/DECISIONS.md).
        services.AddScoped<Application.Payments.IPaymentRepository, Payments.PaymentRepository>();
        services.AddScoped<Payments.MockRefunds>();
        services.AddScoped<Payments.MockBankProvider>();
        services.AddScoped<Application.Payments.IBankQrProvider>(provider => provider.GetRequiredService<Payments.MockBankProvider>());
        services.AddScoped<Payments.MockCardProvider>();
        services.AddScoped<Application.Payments.ICardPaymentProvider>(provider => provider.GetRequiredService<Payments.MockCardProvider>());
        services.AddScoped<Application.Payments.IPaymentProviders, Payments.PaymentProviders>();
        services.AddScoped<Application.Payments.PaymentSettlement>();

        // The wallet: the ledger is the truth, the balance its sum (plan 2.14).
        services.AddScoped<Application.Wallets.IWalletRepository, Wallets.WalletRepository>();
        services.AddScoped<Application.Wallets.IWalletTransactionRepository, Wallets.WalletTransactionRepository>();
        services.AddScoped<Application.Wallets.WalletLedger>();

        // Refunds, vouchers and incidents (plan section 2.5).
        services.AddScoped<Application.Refunds.IRefundRepository, Refunds.RefundRepository>();
        services.AddScoped<Application.Refunds.IRefundVoucherRepository, Refunds.RefundVoucherRepository>();
        services.AddScoped<Application.Refunds.IIncidentRepository, Refunds.IncidentRepository>();
        services.AddScoped<Application.Refunds.IncidentReporter>();
        services.AddScoped<Application.Refunds.RefundPlanner>();
        services.AddScoped<Refunds.RefundJobs>();
        services.AddScoped<Refunds.WalletLedgerReconciliationJob>();
        services.AddScoped<Payments.PaymentJobs>();
        services.AddScoped<Sessions.KioskSessionJobs>();

        // Jobs whose outcome is unknown and customers' claims (plan section 2.5, scenario B).
        services.AddScoped<Application.PrintJobs.SuspectedJobIncidents>();
        services.AddScoped<Application.Support.ISupportClaimRepository, Support.SupportClaimRepository>();
        services.AddScoped<StuckJobWatchdogJob>();

        // Incidents and a stopped kiosk (plan section 9.6): the monitor, and
        // Telegram - off until a bot token is in OpenBao. Its client has no
        // HTTP loggers: the token is in the address they would log.
        services.AddScoped<Kiosks.KioskHeartbeatMonitor>();
        services.AddHttpClient(Kiosks.TelegramSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        services.AddSingleton<Application.Incidents.ITelegramSender, Kiosks.TelegramSender>();

        // Administration and RBAC (plan section 8A.2)
        services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<IAdminRoleRepository, AdminRoleRepository>();
        services.AddScoped<IPermissionResolver, CachedPermissionResolver>();
        services.AddScoped<IAdminSessionFactory, AdminSessionFactory>();

        services.AddScoped<IAuditWriter, MongoAuditWriter>();
        services.AddScoped<ISettingsReader, CachedSettingsReader>();
        services.AddScoped<CachedSettingsReader>();

        // Feature flags (plan section 2.16)
        services.AddScoped<IFeatureFlagRepository, FeatureFlagRepository>();
        services.AddScoped<IFeatureFlagEvaluator, CachedFeatureFlagEvaluator>();
        services.AddScoped<CachedFeatureFlagEvaluator>();
    }

    /// <summary>
    /// Notifications and push (plan section 2.1): FCM HTTP v1 when OpenBao has the
    /// service account key, otherwise <see cref="Notifications.LoggingPushSender"/>
    /// - a missing or broken key never stops the service.
    /// </summary>
    private static void AddPush(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Application.Notifications.INotificationRepository, Notifications.NotificationRepository>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(Notifications.FcmPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Notifications.LoggingPushSender>();
        services.AddSingleton<Application.Notifications.IPushSender>(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("NeoPrint.Push");
            var account = Notifications.FcmServiceAccount.TryParse(
                configuration[Notifications.FcmServiceAccount.ConfigurationKey], out var problem);

            if (account is not null)
            {
                logger.LogInformation("Push: FCM HTTP v1, project {ProjectId}.", account.ProjectId);
                return ActivatorUtilities.CreateInstance<Notifications.FcmPushSender>(provider, account);
            }

            if (problem is not null)
            {
                logger.LogError("Push: the FCM key in OpenBao cannot be used ({Problem}); pushes are only logged.", problem);
            }
            else
            {
                logger.LogInformation("Push: FCM not configured; pushes are only logged (LoggingPushSender).");
            }

            return provider.GetRequiredService<Notifications.LoggingPushSender>();
        });
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

    /// <summary>
    /// Object storage (plan section 2.10).
    /// </summary>
    /// <remarks>
    /// The registry and the client factory are singletons: both hold a cache
    /// that exists precisely so a read does not open a connection or run a
    /// query. The storage itself is scoped, because it resolves the request's
    /// own logger and correlation id.
    /// </remarks>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<StorageSecretProtector>();
        services.AddSingleton<S3ClientFactory>();
        services.AddSingleton<StorageProviderRegistry>();
        services.AddSingleton<StorageConnectionTester>();
        services.AddSingleton<KeyRewrapJob>();

        services.AddScoped<StorageProviderSeeder>();
        services.AddScoped<IFileStorage, S3FileStorage>();
        services.AddScoped<NeoPrint.Application.Files.IFileRepository, FileRepository>();
        services.AddScoped<NeoPrint.Application.Dashboard.IFileStatistics, FileStatisticsReader>();
        services.AddScoped<NeoPrint.Application.Pricing.IPricingProfileRepository, PricingProfileRepository>();

        // Conversion. The converters are singletons - they hold no per-request
        // state - and the service is scoped because it writes through the
        // request's repositories.
        services.AddSingleton<SandboxedProcess>();

        // Where converters run: inside the API (Development only) or in the
        // network-less converter container (docs/adr/ADR-0005-isolated-converter.md).
        services.Configure<ConversionOptions>(configuration.GetSection(ConversionOptions.SectionName));
        services.AddSingleton<IsolatedProcessSandbox>();
        services.AddSingleton<IProcessSandbox>(provider =>
            provider.GetRequiredService<IOptions<ConversionOptions>>().Value.IsIsolated
                ? provider.GetRequiredService<IsolatedProcessSandbox>()
                : provider.GetRequiredService<SandboxedProcess>());
        services.AddSingleton<IDocumentConverter, LibreOfficeConverter>();
        services.AddSingleton<IDocumentConverter, GhostscriptConverter>();
        services.AddSingleton<IDocumentConverter, ImageConverter>();
        services.AddScoped<DocumentConversionService>();

        // Everything that parses a user's PDF or image goes through the
        // converter tool, a process of its own - none of it runs in here.
        services.AddSingleton<ConverterTools>();
        services.AddSingleton<IPageRenderer, ToolPageRenderer>();
        services.AddSingleton<IPagePreviewCache>(provider => new PagePreviewCache(provider.GetRequiredService<IOptions<ConversionOptions>>()));
        services.AddSingleton<NeoPrint.Application.Files.IFileEditor, ToolFileEditor>();
        services.AddSingleton<NeoPrint.Application.Files.ILayoutDocuments, LayoutDocuments>();
        services.AddScoped<IEventPublisher, Messaging.MassTransitEventPublisher>();

        // Storage sync (plan section 2.10). The worker is transient: each Hangfire
        // job gets its own, with its own worker id in the lease.
        services.AddTransient<StorageMigrationWorker>();
        services.AddScoped<IStorageSyncObserver, AdminStorageSyncObserver>();
        services.AddScoped<StorageSyncScheduler>();
        services.AddSingleton<StorageMirror>();
        services.AddTransient<StorageMirrorJobs>();
        services.AddScoped<StorageSourcePurge>();
        services.AddTransient<StorageSourcePurgeJob>();
        services.AddScoped<NeoPrint.Application.Storage.IStorageAdministration, StorageAdministration>();
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

            bus.AddConsumer<DocumentConversionConsumer>();
            bus.AddConsumer<StorageMirrorConsumer>();
            bus.AddConsumer<Refunds.RefundRequestedConsumer>();
            bus.AddConsumer<Refunds.AutoRefundOnKioskErrorConsumer>();
            bus.AddConsumer<Kiosks.KioskFaultOnInterruptedJobConsumer>();
            bus.AddConsumer<Kiosks.AffectedUsersNotificationConsumer>();
            bus.AddConsumer<Kiosks.IncidentNotificationConsumer>();
            bus.AddConsumer<Supplies.SupplyEvaluatorConsumer>();
            bus.AddConsumer<Refunds.PrintJobCancelledConsumer>();
            bus.AddConsumer<Fiscal.PaymentFiscalizationConsumer>();
            bus.AddConsumer<Fiscal.RefundFiscalizationConsumer>();
            bus.AddConsumer<Receipts.ReceiptConsumer>();
            bus.AddConsumer<Receipts.ReceiptFiscalConsumer>();
            bus.AddConsumer<Notifications.UserNotificationConsumer>();
            bus.AddConsumer<Schedules.ScheduledPrintJobConsumer>();
            bus.AddConsumer<Notifications.NotificationDeliveryConsumer>();
            bus.AddConsumer<Notifications.NotificationTriggerConsumer>();
            bus.AddConsumer<Files.FilePrintedConsumer>();
            bus.AddConsumer<Referrals.ReferralRewardConsumer>();
            bus.AddConsumer<Referrals.ReferralNotificationConsumer>();
            bus.AddConsumer<Privacy.DataExportConsumer>();

            // Every process caches the storage providers, so every process needs
            // its own copy of this event: a queue per instance, deleted when the
            // instance goes. The default - one queue for the consumer type -
            // would hand it to just one of them.
            bus.AddConsumer<StorageConfigChangedConsumer>()
                .Endpoint(endpoint =>
                {
                    endpoint.InstanceId = $"-{Environment.MachineName}-{Environment.ProcessId}";
                    endpoint.Temporary = true;
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

    /// <summary>
    /// Fiscal receipts (plan section 2.12). Only the mock e-kassa exists until a
    /// provider is contracted (plan section 14); any other name stops the start
    /// rather than silently issuing nothing.
    /// </summary>
    private static void AddFiscal(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["NEOPRINT_FISCAL_PROVIDER"] is { Length: > 0 } name ? name : Fiscal.MockFiscalProvider.ProviderName;

        if (!string.Equals(provider, Fiscal.MockFiscalProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"NEOPRINT_FISCAL_PROVIDER '{provider}' is not available; only '{Fiscal.MockFiscalProvider.ProviderName}' is.");
        }

        services.AddScoped<Application.Fiscal.IFiscalReceiptRepository, Fiscal.FiscalReceiptRepository>();
        services.AddScoped<Fiscal.MockFiscalProvider>();
        services.AddScoped<Application.Fiscal.IFiscalProvider>(sp => sp.GetRequiredService<Fiscal.MockFiscalProvider>());
        services.AddScoped<Fiscal.FiscalJobs>();

        // NeoPrint's own receipts (plan section 2.12): numbered, rendered with QuestPDF, stored encrypted.
        services.AddScoped<Application.Receipts.IReceiptRepository, Receipts.ReceiptRepository>();
        services.AddScoped<Application.Receipts.IReceiptNumbers, Receipts.ReceiptNumbers>();
        services.AddSingleton<Application.Receipts.IReceiptRenderer, Receipts.QuestPdfReceiptRenderer>();
        services.AddScoped<Receipts.ReceiptJobs>();

        // Promo codes (plan section 2.7).
        services.AddScoped<Application.Promos.IPromoCodeRepository, Promos.PromoCodeRepository>();
        services.AddScoped<Application.Promos.IPromoUsageRepository, Promos.PromoUsageRepository>();
        services.AddScoped<Application.Promos.PromoService>();
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

        // The converter container's heartbeat, where conversions go to it.
        // Degraded when it is missing: uploads still work, conversions wait.
        if (configuration.GetSection(ConversionOptions.SectionName).Get<ConversionOptions>() is { IsIsolated: true })
        {
            builder.AddCheck<ConverterHealthCheck>("converter", HealthStatus.Degraded, ["ready"]);
        }
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
