using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Clients;
using NeoPrint.Application.Localization;
using NeoPrint.Crypto.Releases;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// A mobile app older than the backend supports is told to update (ADR-0009,
/// T-492): <c>426</c>, <c>code: app.updateRequired</c>, and the app shows its
/// "update the app" screen instead of failing request by request.
/// </summary>
/// <remarks>
/// <para>
/// The oldest version is the newer of the API's own (<c>minSupported.mobile</c>
/// in <c>contracts/client-api.json</c>) and <c>Mobile.MinVersion</c> - the
/// panel can ask for a newer app, never let an older one in than the API serves.
/// </para>
/// <para>
/// Only a request that says its version (<see cref="SupportedClients.AppVersionHeader"/>)
/// is judged: the kiosk and the panel do not send it - a kiosk is never refused,
/// its update comes through the API too.
/// </para>
/// </remarks>
public sealed class MobileVersionGate
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(1);

    private readonly RequestDelegate _next;
    private readonly IClock _clock;

    // One reference, swapped whole: requests read it at the same time.
    private Remembered? _cached;

    public MobileVersionGate(RequestDelegate next, IClock clock)
    {
        _next = next;
        _clock = clock;
    }

    public async Task InvokeAsync(HttpContext context, ISettingsReader settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        if (!context.Request.Headers.TryGetValue(SupportedClients.AppVersionHeader, out var header)
            || !ReleaseVersion.TryParse(header.ToString(), out var version)
            || !(context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                 || context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var minimum = await MinimumAsync(settings, context.RequestAborted).ConfigureAwait(false);
        if (minimum is null || version.CompareTo(minimum) >= 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status426UpgradeRequired,
            Title = Messages.Write(new UserMessage("app.updateRequired")),
            Detail = Messages.Write(UserMessage.Of("app.updateRequiredDetail", ("minVersion", minimum.ToString()))),
            Instance = context.Request.Path,
            Extensions = { ["code"] = "app.updateRequired", ["minVersion"] = minimum.ToString() },
        };

        await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task<ReleaseVersion?> MinimumAsync(ISettingsReader settings, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (Volatile.Read(ref _cached) is { } cached && now < cached.Until)
        {
            return cached.Minimum;
        }

        var configured = await settings.GetAsync(SettingKeys.MobileMinVersion, cancellationToken).ConfigureAwait(false);
        var minimum = SupportedClients.Newest(
            SupportedClients.Mobile,
            ReleaseVersion.TryParse(configured, out var chosen) ? chosen : null);

        Volatile.Write(ref _cached, new Remembered(now + Remember, minimum));
        return minimum;
    }

    private sealed record Remembered(DateTime Until, ReleaseVersion? Minimum);
}
