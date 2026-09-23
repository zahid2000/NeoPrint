using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;

using MediatR;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

using Microsoft.AspNetCore.Authorization;

using NeoPrint.Api;
using NeoPrint.Api.Hubs;
using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Auth;
using NeoPrint.Application.Behaviors;
using NeoPrint.Infrastructure;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Seeding;
using NeoPrint.Infrastructure.SecretStore;

var builder = WebApplication.CreateBuilder(args);

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
    });

builder.Services.AddAuthorization(options => options.AddNeoPrintPolicies());

// Permissions are resolved into policies on demand, so the catalogue does not
// have to be registered policy by policy (plan section 8A.2).
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// Confirms a device token against the device's current state on every request,
// so detaching a device takes effect at once (plan section 7.11).
builder.Services.AddScoped<IAuthorizationHandler, DeviceAuthorizationHandler>();

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
builder.Services.AddScoped<IAdminNotifier, SignalRAdminNotifier>();

builder.Services.AddScoped<RequireFeatureFilter>();

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

// Rate limits from plan section 6, tunable through configuration.
var rateLimits = RateLimitSettings.FromConfiguration(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.AuthPerMinute,
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

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => rateLimits.Disabled
        ? RateLimitPartition.GetNoLimiter("all")
        : RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
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

    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync();
    }
}

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseStatusCodePages();

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

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHub<ProvisioningHub>("/hubs/provisioning");
app.MapHub<AdminHub>("/hubs/admin");

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
