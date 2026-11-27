using NeoPrint.Application.Common;
using NeoPrint.Infrastructure.Monitoring;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Monitoring (plan step 8, T-477, T-479, docs/OBSERVABILITY.md): the API's
/// metrics and traces, pushed over OTLP to the OpenTelemetry Collector, which
/// Prometheus scrapes for the metrics and passes the traces on to Tempo;
/// Grafana reads both.
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

    /// <summary>
    /// Where spans come from besides the incoming request (T-479): outgoing
    /// HTTP (bank, fiscal, storage), MongoDB - commands only, never their
    /// text - and RabbitMQ through MassTransit, so a trace follows a message
    /// from the API to its consumer.
    /// </summary>
    internal static readonly string[] TraceSources =
    [
        "System.Net.Http",
        MongoDB.Driver.MongoTelemetry.ActivitySourceName,
        MassTransit.Logging.DiagnosticHeaders.DefaultListenerName,
    ];

    /// <summary>The share of requests traced, 0..1 (<c>Monitoring:TraceSampleRatio</c>, default all).</summary>
    internal static double SampleRatio(IConfiguration configuration)
    {
        var configured = configuration[$"{SectionName}:TraceSampleRatio"];

        return double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ratio)
            ? Math.Clamp(ratio, 0, 1)
            : 1;
    }

    /// <summary>Health probes and SignalR's long connections are not traced.</summary>
    internal static bool IsNoise(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/hubs");

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
                }))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(SampleRatio(configuration))))
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Probes and the live connections say nothing about a request.
                    options.Filter = context => !IsNoise(context.Request.Path);
                    options.EnrichWithHttpRequest = (activity, _) =>
                    {
                        // Who called is not the trace's business (docs/OBSERVABILITY.md);
                        // the query string is already left out by the instrumentation.
                        activity.SetTag("client.address", null);
                        activity.SetTag("user_agent.original", null);
                    };
                })
                .AddSource(TraceSources)
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = new Uri(collector, "v1/traces");
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                }));

        return services;
    }
}
