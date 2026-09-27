namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// The origins a developer's own machine serves the Flutter web app from.
/// </summary>
/// <remarks>
/// The admin panel shares an origin with the API in every environment: nginx
/// proxies <c>/api</c> and <c>/hubs</c> to the backend, which is what lets the
/// refresh cookie be <c>SameSite=Strict</c> (plan section 8A.1). So production
/// needs no cross-origin access at all and is given none.
///
/// The Flutter app in a browser is the exception. <c>flutter run -d chrome</c>
/// serves the app from a localhost port the tool picks, and without a policy
/// the browser blocks every request before it is sent - which showed up only as
/// "network error" on the register screen and told nobody what was wrong.
///
/// Loopback on any port is accepted in Development only; a deployment accepts
/// the origins in <c>Cors:AllowedOrigins</c> and nothing else
/// (<see cref="CorsOrigins"/>, docs/DECISIONS.md).
/// </remarks>
public static class DevelopmentCors
{

    /// <summary>
    /// The hosts a development origin may name. 10.0.2.2 is how the Android
    /// emulator addresses the host machine.
    /// </summary>
    private static readonly string[] LoopbackHosts =
    [
        "localhost",
        "127.0.0.1",
        "[::1]",
        "::1",
        "10.0.2.2",
    ];

    /// <summary>
    /// Whether a browser origin belongs to a developer's own machine.
    /// </summary>
    /// <remarks>
    /// The port is deliberately not checked: the Flutter tooling picks a new one
    /// on every run. The host is what matters, and a page served from loopback
    /// is already running on the same machine as the API.
    /// </remarks>
    public static bool IsLoopbackOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
