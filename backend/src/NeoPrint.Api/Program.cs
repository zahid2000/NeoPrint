using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;

using MediatR;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

using Microsoft.AspNetCore.Authorization;

using Hangfire;
using NeoPrint.Api;
using NeoPrint.Api.Hubs;
using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Settings;
using NeoPrint.Infrastructure;
using NeoPrint.Infrastructure.Auth;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.SecretStore;
using NeoPrint.Infrastructure.Seeding;

using NeoPrint.Infrastructure.Conversion;
using NeoPrint.Infrastructure.PrintJobs;
using NeoPrint.Infrastructure.Storage;

// ---------------------------------------------------------------------------
// The converter container (docs/adr/ADR-0005-isolated-converter.md): the same
// image, started with this switch, runs only the spool worker - before any
// configuration, secret store or database is touched. It has no network and
// no secrets to read.
// ---------------------------------------------------------------------------
if (args is [ConverterWorker.CommandLineSwitch, ..])
{
    Environment.ExitCode = await ConverterHost.RunAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging
//
// Structured JSON, so a log line can be queried by CorrelationId, KioskId or
// JobId rather than grepped (docs/OBSERVABILITY.md). Scopes are included:
// that is how the correlation id set by the middleware reaches every line the
// request produces. Development keeps the plain console, which is readable
// while working; the full OpenTelemetry pipeline lands in step 8.
// ---------------------------------------------------------------------------
builder.Logging.ClearProviders();

if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}
else
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
    });
}

// Service and environment on every line, so logs from several services can be
// read together (docs/OBSERVABILITY.md).
builder.Logging.Configure(options =>
    options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

// ASP.NET Core's exception middleware logs every exception as an error before
// ProblemDetailsExceptionHandler sees it - a refused rule (409) included. The
// handler decides the level instead: an unexpected failure (5xx) is logged
// there as an error with the exception, an expected refusal is not an error.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);

// ---------------------------------------------------------------------------
// Configuration
//
// Every secret comes from OpenBao (plan section 2.13). The environment supplies
// only the address and the AppRole credentials; in production the application
// does not start if the store is unreachable or a mandatory key is missing.
// ---------------------------------------------------------------------------
var isProduction = builder.Environment.IsProduction();

builder.Configuration.AddOpenBao(
    options => options.Optional = !isProduction,
    isProduction,
    log: (message, exception) =>
    {
        // Runs before the logging pipeline exists, so this writes to the
        // console directly. It never prints a secret value.
        if (exception is null)
        {
            Console.WriteLine($"[openbao] {message}");
        }
        else
        {
            Console.Error.WriteLine($"[openbao] {message}: {exception.Message}");
        }
    });

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(OpenBaoOptions.FromEnvironment());
builder.Services.AddSingleton<ISecretStore>(provider =>
{
    var options = provider.GetRequiredService<OpenBaoOptions>();
    var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("openbao");
    http.BaseAddress = new Uri(options.Address.TrimEnd('/') + "/");
    return new OpenBaoClient(http, options);
});

builder.Services.AddNeoPrintInfrastructure(builder.Configuration);

// Outside Development a converter never runs inside the API
// (docs/adr/ADR-0005-isolated-converter.md): refuse to start rather than run
// untrusted documents with the API's network and user.
(builder.Configuration.GetSection(ConversionOptions.SectionName).Get<ConversionOptions>() ?? new ConversionOptions())
    .EnsureAllowedIn(builder.Environment.IsDevelopment());
builder.Services.AddNeoPrintSeeding(builder.Environment.IsDevelopment());

builder.Services.AddMediatR(configuration =>
{
    configuration.RegisterServicesFromAssembly(typeof(ValidationBehavior<,>).Assembly);

    // Order matters: log the attempt, validate it, then open a transaction only
    // for the requests that need one.
    configuration.AddOpenBehavior(typeof(LoggingBehavior<,>));
    configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
    configuration.AddOpenBehavior(typeof(TransactionBehavior<,>));
});

builder.Services.AddValidatorsFrom(typeof(ValidationBehavior<,>).Assembly);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---------------------------------------------------------------------------
// Authentication
//
// One scheme for all four kinds of principal (user, admin, kiosk, device
// awaiting activation). They are told apart by the token type claim, which the
// authorisation policies check (plan section 2.8).
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = JwtTokenService.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            // The signing key is read per validation, so a rotated key applies
            // without a restart.
            IssuerSigningKeyResolver = (_, _, _, _) =>
                [JwtTokenService.ReadSigningKey(builder.Configuration)],
            // Tokens live 15 minutes; the default five minute grace would be a
            // third of that.
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // A browser cannot put a header on a WebSocket or an EventSource
        // request, so the SignalR client sends the token as ?access_token=.
        // Taken from the query on hub paths only: everywhere else a token in
        // a URL would end up in history, proxies and logs for no reason. The
        // admin nginx logs request paths without the query (admin/nginx.conf).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];

                if (!string.IsNullOrEmpty(token) && context.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options => options.AddNeoPrintPolicies());

// Permissions are resolved into policies on demand, so the catalogue does not
// have to be registered policy by policy (plan section 8A.2).
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// Confirms a device token against the device's current state on every request,
// so detaching a device takes effect at once (plan section 7.11).
builder.Services.AddScoped<IAuthorizationHandler, DeviceAuthorizationHandler>();

// Same for an admin token: a deactivated account is refused at once.
builder.Services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, UserAuthorizationHandler>();

// SignalR with the Redis backplane, so several API instances can serve the
// same hub groups (plan section 6).
var signalR = builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
var redisConnection = builder.Configuration["NEOPRINT_REDIS"];
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    signalR.AddStackExchangeRedis(redisConnection, options => options.Configuration.ChannelPrefix =
        StackExchange.Redis.RedisChannel.Literal("neoprint-signalr"));
}

builder.Services.AddScoped<IDeviceNotifier, SignalRDeviceNotifier>();
builder.Services.AddSingleton<AdminConnections>();
builder.Services.AddScoped<IAdminNotifier, SignalRAdminNotifier>();
builder.Services.AddScoped<IKioskNotifier, SignalRKioskNotifier>();
builder.Services.AddScoped<NeoPrint.Application.Payments.IPaymentNotifier, SignalRKioskNotifier>();
builder.Services.AddScoped<NeoPrint.Application.Refunds.IPaymentsAdminNotifier, SignalRPaymentsAdminNotifier>();
builder.Services.AddScoped<NeoPrint.Application.Notifications.IUserNotifier, SignalRUserNotifier>();

// The customer and the bank, played in Development only (plan section 6, "Dev").
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<NeoPrint.Infrastructure.Payments.MockBankSimulator>();
    builder.Services.AddHttpClient(NeoPrint.Infrastructure.Payments.MockBankSimulator.HttpClientName);
}

// A job may skip its payment in Development only (plan step 3). From the
// environment, not from configuration: no Production setting can turn it on.
builder.Services.Configure<PrintJobOptions>(options =>
    options.AllowPaymentSkip = builder.Environment.IsDevelopment());

builder.Services.AddScoped<RequireFeatureFilter>();

// Claims the caller's Idempotency-Key on the endpoints that opt in
// (docs/IDEMPOTENCY.md).
builder.Services.AddScoped<IdempotencyFilter>();

// The Hangfire panel's forms carry an antiforgery token when the service is there (T-215).
builder.Services.AddAntiforgery();

builder.Services.AddControllers(options =>
    {
        // Applied globally; it only acts on endpoints carrying
        // [RequireFeature] (plan section 2.16).
        options.Filters.Add<RequireFeatureFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        // Enums travel as names, not ordinals: a client reads "Privacy", and
        // reordering an enum cannot silently change what a stored value means.
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance = context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    });

builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

// The caller's own address behind the proxy (T-306): only the configured proxy
// networks are believed.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(
    options => RequestLimits.ConfigureForwardedHeaders(options, builder.Configuration));

// Rate limits from plan section 6, tunable through configuration.
var rateLimits = RateLimitSettings.FromConfiguration(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = RequestLimits.OnRejectedAsync;

    options.AddPolicy("auth", context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.AuthPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Refreshing a session is not guessing a credential (docs/DECISIONS.md):
    // it has its own, higher per-IP limit, so reloading the panel or a
    // carrier NAT full of phones does not sign people out.
    options.AddPolicy("refresh", context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.RefreshPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    options.AddPolicy("device-register", context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        // Device registration is anonymous, so it is limited per IP per hour
        // (plan section 7.11).
        : RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.DeviceRegisterPerHour,
                Window = TimeSpan.FromHours(1),
            }));

    // Anonymous support claims name a job by the short codes on a receipt:
    // limited per IP per hour so the pairs cannot be tried at speed.
    options.AddPolicy("support-claim", context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.SupportClaimPerHour,
                Window = TimeSpan.FromHours(1),
            }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(
            RequestLimits.CallerKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.GlobalPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));
});

if (builder.Environment.IsDevelopment())
{
    // Swagger is a development aid only. Every administrative capability is
    // reachable from the admin panel (plan section 1).
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

// Cross-origin access for the Flutter app in a browser: loopback in
// Development, otherwise only the origins configured in Cors:AllowedOrigins
// (see CorsOrigins). A bad entry stops the start rather than widening access.
var corsOrigins = CorsOrigins.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddCors(options => options.AddPolicy(
    CorsOrigins.PolicyName,
    policy => policy
        .SetIsOriginAllowed(corsOrigins.IsAllowed)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

// ---------------------------------------------------------------------------
// Startup work
// ---------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var mongo = scope.ServiceProvider.GetRequiredService<MongoContext>();
    await mongo.EnsureIndexesAsync();

    // Idempotent: creates only what is missing, so it is safe on every start
    // and picks up settings and flags added by a later release.
    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

    // The first storage provider, from the secret store. Only when none is
    // configured: every later change is the administrator's (plan 2.10).
    await scope.ServiceProvider.GetRequiredService<StorageProviderSeeder>().SeedAsync();

    // Every minute: a sync job whose worker died - a crash, a redeploy - is
    // picked up again and resumes from its checkpoint (plan section 2.10).
    scope.ServiceProvider.GetRequiredService<IRecurringJobManager>().AddOrUpdate<StorageSyncScheduler>(
        StorageSyncScheduler.ResumeJobId,
        scheduler => scheduler.ResumeStaleAsync(CancellationToken.None),
        Cron.Minutely());

    var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    // Every minute: the Redis queues rebuilt from MongoDB, so a lost or
    // stale Redis is never the truth for long (plan section 3).
    // Payments: overdue intents expire every minute; payments whose bank
    // answer never came are checked against the bank every five (plan 6).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Payments.PaymentJobs>(
        NeoPrint.Infrastructure.Payments.PaymentJobs.ExpirationJobId,
        jobs => jobs.ExpireAsync(CancellationToken.None),
        Cron.Minutely());
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Payments.PaymentJobs>(
        NeoPrint.Infrastructure.Payments.PaymentJobs.ReconciliationJobId,
        jobs => jobs.ReconcileAsync(CancellationToken.None),
        "*/5 * * * *");

    // Refunds: due retries every minute, provider refunds still open every
    // five; the wallet ledger against its balances once a day (plan 6).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Refunds.RefundJobs>(
        NeoPrint.Infrastructure.Refunds.RefundJobs.RetryJobId,
        jobs => jobs.RetryDueAsync(CancellationToken.None),
        Cron.Minutely());
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Refunds.RefundJobs>(
        NeoPrint.Infrastructure.Refunds.RefundJobs.PollingJobId,
        jobs => jobs.PollAsync(CancellationToken.None),
        "*/5 * * * *");
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Refunds.WalletLedgerReconciliationJob>(
        NeoPrint.Infrastructure.Refunds.WalletLedgerReconciliationJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily(4));

    // Kiosk sessions whose time ran out: the kiosk and the app are told (plan section 9.3).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Sessions.KioskSessionJobs>(
        NeoPrint.Infrastructure.Sessions.KioskSessionJobs.ExpiryJobId,
        jobs => jobs.RunAsync(CancellationToken.None),
        Cron.Minutely());

    // Orders paid in the app for later and never collected: cancelled, money to the wallet (plan section 9.4).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.UserQr.UncollectedPrintJobsJob>(
        NeoPrint.Infrastructure.UserQr.UncollectedPrintJobsJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Hourly());

    // Scheduled prints (plan section 9.5): whose time came every minute, unanswered ones every 10.
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Schedules.ScheduledPrintDueJob>(
        NeoPrint.Infrastructure.Schedules.ScheduledPrintDueJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Minutely());

    recurring.AddOrUpdate<NeoPrint.Infrastructure.Schedules.ScheduledPrintAutoCancelJob>(
        NeoPrint.Infrastructure.Schedules.ScheduledPrintAutoCancelJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        "*/10 * * * *");

    // Files that delete themselves (plan section 2.13); a print also triggers it at once.
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Files.FileAutoDeleteJob>(
        NeoPrint.Infrastructure.Files.FileAutoDeleteJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        "*/5 * * * *");

    // "Məlumatlarımı yüklə" archives past their 24 hours.
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Privacy.DataExportCleanupJob>(
        NeoPrint.Infrastructure.Privacy.DataExportCleanupJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Hourly());

    // Deleted accounts past their grace hours are erased (plan section 2.13).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Privacy.AccountDeletionJob>(
        NeoPrint.Infrastructure.Privacy.AccountDeletionJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        "*/10 * * * *");

    recurring.AddOrUpdate<PrintQueueReconciliationJob>(
        PrintQueueReconciliationJob.RecurringId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Minutely());

    // Fiscal receipts: due tries every minute, and the incident for late ones (plan section 2.12).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Fiscal.FiscalJobs>(
        NeoPrint.Infrastructure.Fiscal.FiscalJobs.RetryJobId,
        jobs => jobs.RunAsync(CancellationToken.None),
        Cron.Minutely());

    // Receipts rendered before their fiscal receipt arrived, in case the event was lost.
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Receipts.ReceiptJobs>(
        NeoPrint.Infrastructure.Receipts.ReceiptJobs.CatchUpJobId,
        jobs => jobs.CatchUpAsync(CancellationToken.None),
        "*/5 * * * *");

    // Kiosks whose heartbeat stopped: Offline, with a Critical incident (plan section 9.6, scenario B).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Kiosks.KioskHeartbeatMonitor>(
        NeoPrint.Infrastructure.Kiosks.KioskHeartbeatMonitor.JobId,
        job => job.RunAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Kiosks.KioskHeartbeatMonitor.Every15Seconds);

    // Jobs whose kiosk went quiet: Suspected, never refunded automatically (plan section 2.5, scenario B).
    recurring.AddOrUpdate<StuckJobWatchdogJob>(
        StuckJobWatchdogJob.JobId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Minutely());

    // Supplies (plan sections 2.15, 6): the evaluator's sweep, the forecast,
    // Critical tasks nobody started, and the warehouse's low stock.
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Supplies.SupplyJobs>(
        NeoPrint.Infrastructure.Supplies.SupplyJobs.EvaluatorJobId,
        jobs => jobs.EvaluateAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Supplies.SupplyJobs.EveryFiveMinutes);
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Supplies.SupplyJobs>(
        NeoPrint.Infrastructure.Supplies.SupplyJobs.ForecastJobId,
        jobs => jobs.ForecastAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Supplies.SupplyJobs.Hourly);
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Supplies.SupplyJobs>(
        NeoPrint.Infrastructure.Supplies.SupplyJobs.EscalationJobId,
        jobs => jobs.EscalateAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Supplies.SupplyJobs.EveryFiveMinutes);
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Supplies.SupplyJobs>(
        NeoPrint.Infrastructure.Supplies.SupplyJobs.LowStockJobId,
        jobs => jobs.LowStockAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Supplies.SupplyJobs.HourlyAtHalfPast);

    // The system's own checks (plan section 6, T-215): orphaned objects, the
    // dead-letter queues, payments against refunds, ledger and receipts.
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Operations.OperationsJobs>(
        NeoPrint.Infrastructure.Operations.OperationsJobs.FileCleanupJobId,
        jobs => jobs.FileCleanupAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Operations.OperationsJobs.FileCleanupCron);
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Operations.OperationsJobs>(
        NeoPrint.Infrastructure.Operations.OperationsJobs.DeadLetterJobId,
        jobs => jobs.DeadLettersAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Operations.OperationsJobs.EveryFiveMinutes);
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Operations.OperationsJobs>(
        NeoPrint.Infrastructure.Operations.OperationsJobs.PaymentConsistencyJobId,
        jobs => jobs.PaymentConsistencyAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Operations.OperationsJobs.PaymentConsistencyCron);

    // The pilot report's days (plan sections 2.17, 6).
    recurring.AddOrUpdate<NeoPrint.Infrastructure.Analytics.AnalyticsRollupJob>(
        NeoPrint.Infrastructure.Analytics.AnalyticsRollupJob.JobId,
        job => job.RunAsync(CancellationToken.None),
        NeoPrint.Infrastructure.Analytics.AnalyticsRollupJob.Hourly);

    // Once at start as well: after an upgrade the days are there without waiting for the hour (it is safe to run again).
    recurring.Trigger(NeoPrint.Infrastructure.Analytics.AnalyticsRollupJob.JobId);

    // Which providers answer: for the panel, and for noticing a primary that
    // has gone quiet (plan section 2.10).
    recurring.AddOrUpdate<StorageMirrorJobs>(
        StorageMirrorJobs.HealthJobId,
        jobs => jobs.CheckHealthAsync(CancellationToken.None),
        Cron.Minutely());

    // The nightly mirror pass, on the schedule the settings give. A change to
    // the setting takes effect at the next start.
    var reconciliationCron = await scope.ServiceProvider.GetRequiredService<ISettingsReader>()
            .GetAsync(SettingKeys.StorageReconciliationCron) is { Length: > 0 } cron
        ? cron
        : "0 3 * * *";

    recurring.AddOrUpdate<StorageMirrorJobs>(
        StorageMirrorJobs.ReconciliationJobId,
        jobs => jobs.ReconcileAsync(CancellationToken.None),
        reconciliationCron);

    // After seeding, because the retention windows come from settings that do
    // not exist yet on a first start (docs/DATA_RETENTION.md).
    var settings = scope.ServiceProvider.GetRequiredService<ISettingsReader>();

    await mongo.ApplyRetentionAsync(
        TimeSpan.FromDays(await settings.GetIntAsync(SettingKeys.RetentionStateTransitionDays, 365)),
        TimeSpan.FromDays(await settings.GetIntAsync(SettingKeys.RetentionProviderEventDays, 180)),
        TimeSpan.FromDays(await settings.GetIntAsync(SettingKeys.AnalyticsRetentionDays, 180)),
        TimeSpan.FromDays(await settings.GetIntAsync(SettingKeys.KioskTelemetryRetentionDays, 30)),
        TimeSpan.FromDays(await settings.GetIntAsync(SettingKeys.KioskPrinterEventRetentionDays, 365)));

    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync();
    }
}

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

// Before anything reads the caller's address: the rate limits, the audit log (T-306).
app.UseForwardedHeaders();

// First, so every line a request produces carries the correlation id -
// including the ones the exception handler writes (docs/OBSERVABILITY.md).
app.UseNeoPrintCorrelationId();

// Before the exception handler, so a problem is written in the caller's
// language: the language is ambient and flows inward only.
app.UseNeoPrintRequestLanguage();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Before the rate limiter and authentication: a preflight carries neither
// credentials nor a body, and answering it must not spend a request from the
// sign-in limit.
app.UseCors(CorsOrigins.PolicyName);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();

// After authentication, so a signed-in caller is counted as itself and not by
// the address it shares with everyone behind the proxy (T-306).
app.UseRateLimiter();

app.UseAuthorization();

// Hangfire's panel, behind its SuperAdmin ticket (T-167, T-215).
app.UseNeoPrintHangfireDashboard();

app.MapControllers();

app.MapHub<ProvisioningHub>("/hubs/provisioning");
// A panel's connection ends with the token it was opened with: the kiosks it
// hears about are decided when it connects, and the panel connects again by
// itself with a token that is good now (T-249).
app.MapHub<AdminHub>("/hubs/admin", options => options.CloseOnAuthenticationExpiration = true);
app.MapHub<KioskHub>("/hubs/kiosk");
app.MapHub<UserHub>("/hubs/user");

// Liveness: the process is up. Used by the container health check, and must not
// depend on anything external, or a brief database blip would restart the API.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness: everything this instance needs in order to serve traffic.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse,
});

app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponse });

await app.RunAsync();

return;

/// <summary>
/// Writes the health report as JSON: which check, what state, and a short
/// description. Never the exception, which can carry a connection string.
/// </summary>
static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    return context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        durationMs = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            description = entry.Value.Description,
            durationMs = entry.Value.Duration.TotalMilliseconds,
        }),
    });
}

/// <summary>
/// Exposed so that <c>WebApplicationFactory</c> in the integration tests can
/// bootstrap this application.
/// </summary>
public partial class Program;
