using NeoPrint.Application.Common;
using NeoPrint.Infrastructure.Monitoring;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Monitoring (plan step 8, T-477, docs/OBSERVABILITY.md): the API's metrics,
/// pushed over OTLP to the OpenTelemetry Collector, which Prometheus scrapes
/// and Grafana reads.
/// </summary>
/// <remarks>
/// Off unless <c>Monitoring:OtlpEndpoint</c> is set - the compose file sets it
/// only for the API, and the collector only runs with the <c>monitoring</c>
/// profile. A collector that is down costs nothing but the lost points: the
/// exporter drops what it cannot send and the API carries on.
/// </remarks>
public static class MonitoringRegistration
{
    public const string SectionName = "Monitoring";

    /// <summary>The meters the dashboards read.</summary>
    internal static readonly string[] Meters =
    [
        // Our own: event counters and the operational gauges.
        NeoPrintMetrics.MeterName,
        OperationalMetrics.MeterName,
        // ASP.NET Core: request duration and status (latency and errors),
        // connections, SignalR, rate limiting, unhandled exceptions.
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Diagnostics",
        // .NET: GC, memory, thread pool, CPU; outgoing HTTP (bank, fiscal).
        "System.Runtime",
        "System.Net.Http",
    ];

    public static IServiceCollection AddNeoPrintMonitoring(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var endpoint = configuration[$"{SectionName}:OtlpEndpoint"];

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return services;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var collector)
            || (collector.Scheme != Uri.UriSchemeHttp && collector.Scheme != Uri.UriSchemeHttps))
        {
            // A typo must not pass silently as "monitoring is on".
            throw new InvalidOperationException($"{SectionName}:OtlpEndpoint is not an http(s) address.");
        }

        services.AddSingleton<OperationalMetrics>();
        services.AddHostedService<OperationalMetricsCollector>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService("neoprint-api", serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", environment.EnvironmentName)]))
            .WithMetrics(metrics => metrics
                .AddMeter(Meters)
                .AddOtlpExporter((exporter, reader) =>
                {
                    exporter.Endpoint = new Uri(collector, "v1/metrics");
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15_000;
                    // Prometheus wants cumulative counters.
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                }));

        return services;
    }
}
