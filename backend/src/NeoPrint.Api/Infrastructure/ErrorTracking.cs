using System.Reflection;

using Sentry.AspNetCore;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Error tracking (plan step 8, T-478, docs/MONITORING.md): unhandled errors
/// go to the self-hosted GlitchTip, which speaks the Sentry protocol.
/// </summary>
/// <remarks>
/// <para>
/// One DSN per client, from configuration (<c>ErrorTracking:ApiDsn</c>,
/// <c>AdminDsn</c>, <c>MobileDsn</c>, <c>KioskDsn</c>); an empty one turns that
/// client's reporting off. A DSN is not a secret - it names the project and
/// lets a client send to it, and every browser and phone holds one - so the
/// clients get theirs from <c>GET /api/error-tracking/{client}</c>.
/// </para>
/// <para>
/// What leaves the API is the exception and its stack: no request body, no
/// cookies, no headers that identify a person, no user (SendDefaultPii off),
/// and breadcrumbs only from warnings up - the log lines below that are not
/// sent (docs/OBSERVABILITY.md, "Do not log").
/// </para>
/// </remarks>
public static class ErrorTracking
{
    public const string SectionName = "ErrorTracking";

    /// <summary>The clients that can ask for their DSN, and the key each is configured under.</summary>
    public static readonly IReadOnlyDictionary<string, string> ClientKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["admin"] = "AdminDsn",
        ["mobile"] = "MobileDsn",
        ["kiosk"] = "KioskDsn",
    };

    /// <summary>A DSN from configuration, or null when it is missing or not an http(s) address.</summary>
    public static string? Dsn(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var dsn = configuration[$"{SectionName}:{key}"];

        return Uri.TryCreate(dsn, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.UserInfo)
            ? dsn
            : null;
    }

    public static IWebHostBuilder AddNeoPrintErrorTracking(
        this IWebHostBuilder webHost, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(environment);

        var dsn = Dsn(configuration, "ApiDsn");

        if (dsn is null)
        {
            return webHost;
        }

        var version = typeof(ErrorTracking).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        return webHost.UseSentry((SentryAspNetCoreOptions options) =>
        {
            options.Dsn = dsn;
            options.Environment = environment.EnvironmentName;
            options.Release = $"neoprint-api@{version}";
            options.SendDefaultPii = false;
            options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
            options.MinimumBreadcrumbLevel = LogLevel.Warning;
            options.MinimumEventLevel = LogLevel.Error;
            // Errors only: no performance tracing, no sessions.
            options.TracesSampleRate = 0;
            options.AutoSessionTracking = false;
            options.SetBeforeSend(Scrub);
        });
    }

    /// <summary>Drops what could identify a caller or carry a credential, whatever the SDK collected.</summary>
    internal static SentryEvent Scrub(SentryEvent sentryEvent, SentryHint hint)
    {
        sentryEvent.User = new SentryUser();
        sentryEvent.Request.Cookies = null;
        sentryEvent.Request.Data = null;
        sentryEvent.Request.QueryString = null;
        sentryEvent.Request.Headers.Clear();
        sentryEvent.Request.Env.Clear();

        return sentryEvent;
    }
}
