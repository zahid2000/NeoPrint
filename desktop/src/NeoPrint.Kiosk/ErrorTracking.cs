using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Sentry;

namespace NeoPrint.Kiosk;

/// <summary>
/// Error tracking (plan step 8, T-478; docs/MONITORING.md): the kiosk's
/// unhandled errors go to the self-hosted GlitchTip through the Sentry SDK.
/// </summary>
/// <remarks>
/// <para>
/// The DSN is asked of the API (<c>GET api/error-tracking/kiosk</c>) - one
/// build for every kiosk carries none (plan section 7.11), and a DSN is no
/// secret. Asked beside the start, never before it: a kiosk with no network
/// starts as it always did, and reports nothing.
/// </para>
/// <para>
/// What leaves the kiosk is the error and its stack: no machine name, no user,
/// no breadcrumbs of what the customer did. The crash handler and the log
/// stay as they were; this is one more place the crash goes.
/// </para>
/// </remarks>
internal static class ErrorTracking
{
    /// <summary>What the API answers.</summary>
    internal sealed record Config(
        [property: JsonPropertyName("dsn")] string? Dsn,
        [property: JsonPropertyName("environment")] string? Environment);

    /// <summary>Asks for the DSN and starts the SDK with it; never throws.</summary>
    public static async Task<bool> StartAsync(
        string apiBaseUrl,
        ILogger logger,
        Func<Uri, CancellationToken, Task<Config?>>? fetch = null,
        Action<SentryOptions>? init = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await (fetch ?? FetchAsync)(new Uri(new Uri(apiBaseUrl.TrimEnd('/') + "/"), "api/error-tracking/kiosk"), cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(config?.Dsn))
            {
                return false;
            }

            var options = Options(config.Dsn, config.Environment);
            (init ?? (o => SentrySdk.Init(o)))(options);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No network, an old API, a bad answer: the kiosk runs on.
            logger.LogInformation("Error tracking not started: {Reason}", exception.Message);
            return false;
        }
    }

    /// <summary>Reports a crash the UI could not survive, before the process ends.</summary>
    public static void CaptureFatal(Exception exception)
    {
        if (!SentrySdk.IsEnabled)
        {
            return;
        }

        SentrySdk.CaptureException(exception);
        SentrySdk.Flush(TimeSpan.FromSeconds(3));
    }

    internal static SentryOptions Options(string dsn, string? environment)
    {
        var options = new SentryOptions
        {
            Dsn = dsn,
            Environment = environment,
            Release = $"neoprint-kiosk@{Core.KioskRuntime.AppVersion}",
            SendDefaultPii = false,
            // The machine's name is the kiosk's own business.
            ServerName = string.Empty,
            TracesSampleRate = 0,
            AutoSessionTracking = false,
            MaxBreadcrumbs = 0,
        };
        options.SetBeforeSend(Scrub);
        return options;
    }

    internal static SentryEvent Scrub(SentryEvent sentryEvent, SentryHint hint)
    {
        sentryEvent.User = new SentryUser();
        sentryEvent.ServerName = null;
        return sentryEvent;
    }

    private static async Task<Config?> FetchAsync(Uri address, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return await http.GetFromJsonAsync<Config>(address, cancellationToken).ConfigureAwait(false);
    }
}
