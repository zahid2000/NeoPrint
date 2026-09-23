using System.Net.Http.Headers;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.SecretStore;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.HealthChecks;

/// <summary>
/// Reports whether MongoDB answers and is running as a replica set.
/// </summary>
/// <remarks>
/// Written by hand rather than taken from a health-check package: plan section
/// 2.0 asks for the platform's own facilities first, and
/// <see cref="IHealthCheck"/> is one.
/// </remarks>
public sealed class MongoHealthCheck : IHealthCheck
{
    private readonly MongoContext _context;

    public MongoHealthCheck(MongoContext context) => _context = context;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _context.Database
                .RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Transactions are only available on a replica set, and the outbox
            // and wallet updates depend on them (plan section 4).
            if (!result.Contains("setName"))
            {
                return HealthCheckResult.Degraded(
                    "MongoDB is reachable but is not running as a replica set; transactions are unavailable.");
            }

            return result.GetValue("isWritablePrimary", false).ToBoolean() || result.GetValue("secondary", false).ToBoolean()
                ? HealthCheckResult.Healthy($"Replica set '{result["setName"].AsString}'.")
                : HealthCheckResult.Degraded("The replica set has no primary yet.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("MongoDB is not reachable.", exception);
        }
    }
}

/// <summary>Reports whether Redis answers.</summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await _redis.GetDatabase().PingAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy($"Ping {latency.TotalMilliseconds:0.#} ms.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", exception);
        }
    }
}

/// <summary>
/// Reports whether RabbitMQ answers on its management API.
/// </summary>
/// <remarks>
/// Uses the HTTP API rather than opening an AMQP connection: a health check
/// should not disturb the connection MassTransit is holding.
/// </remarks>
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _managementUri;
    private readonly string _user;
    private readonly string _password;

    public RabbitMqHealthCheck(IHttpClientFactory httpClientFactory, Uri amqpUri)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(amqpUri);

        _httpClientFactory = httpClientFactory;

        // The management plugin listens on 15672 alongside the AMQP port.
        _managementUri = new UriBuilder("http", amqpUri.Host, 15672, "/api/health/checks/alarms").Uri;

        var credentials = amqpUri.UserInfo.Split(':', 2);
        _user = credentials.Length > 0 ? Uri.UnescapeDataString(credentials[0]) : "guest";
        _password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "guest";
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("health");
            client.Timeout = TimeSpan.FromSeconds(5);

            using var request = new HttpRequestMessage(HttpMethod.Get, _managementUri);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_user}:{_password}")));

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"RabbitMQ reported {(int)response.StatusCode}.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is not reachable.", exception);
        }
    }
}

/// <summary>
/// Reports whether the secrets are loaded and the store is still answering.
/// </summary>
/// <remarks>
/// Degraded rather than unhealthy when OpenBao drops out at runtime: the
/// service keeps running on the values it already has (plan section 2.13), and
/// reporting it as unhealthy would take a working instance out of the load
/// balancer for no reason.
/// </remarks>
public sealed class OpenBaoHealthCheck : IHealthCheck
{
    private readonly ISecretStore _store;
    private readonly OpenBaoOptions _options;

    public OpenBaoHealthCheck(ISecretStore store, OpenBaoOptions options)
    {
        _store = store;
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = _options.Paths.FirstOrDefault() ?? "backend/core";
            var secrets = await _store.ReadAsync(path, cancellationToken).ConfigureAwait(false);

            return secrets.Count > 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded($"OpenBao answered but '{path}' is empty.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Degraded(
                "OpenBao is not reachable; the service is running on the last loaded secrets.", exception);
        }
    }
}
