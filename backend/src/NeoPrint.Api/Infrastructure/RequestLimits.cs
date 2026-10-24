using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;

using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Who a request counts against, and what a refused one is told (T-306).
/// </summary>
/// <remarks>
/// The global limit is per caller: a signed-in panel, phone or kiosk has its
/// own, by its token type and subject; only an anonymous request is counted by
/// its address. Before T-306 the limiter ran ahead of authentication and the
/// tokens carry no name claim, so every request was counted by address - and
/// behind the proxy every request has the proxy's address: one bucket of 120 a
/// minute for every panel, phone and kiosk together.
/// </remarks>
public static class RequestLimits
{
    /// <summary>The bucket of a request.</summary>
    public static string CallerKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.User;

        if (principal.Identity?.IsAuthenticated == true
            && principal.FindFirstValue(NeoPrintClaims.TokenType) is { Length: > 0 } tokenType
            && (principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub")) is { Length: > 0 } subject)
        {
            return $"{tokenType}:{subject}";
        }

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    /// <summary>
    /// A 429 from the framework's limiters (T-310). They tell the length of the
    /// window, not what is left of it - so the caller is told to try again a
    /// little later, and `Retry-After` carries the window as an upper bound.
    /// </summary>
    public static ValueTask OnRejectedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejected);

        var seconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;

        return new ValueTask(WriteTooManyAsync(rejected.HttpContext, seconds, exact: false, cancellationToken));
    }

    /// <summary>
    /// The 429 itself: `Retry-After`, a title and a detail in the caller's
    /// language, `code: problem.tooManyRequests`. A number of seconds is said
    /// only when it is what is really left and no more than a minute (T-310);
    /// otherwise "a little later".
    /// </summary>
    public static async Task WriteTooManyAsync(HttpContext context, int seconds, bool exact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        var detail = exact && seconds <= 60
            ? Domain.Common.UserMessage.Of("problem.tooManyRequestsIn", ("seconds", seconds))
            : new Domain.Common.UserMessage("problem.tooManyRequestsDetail");

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = Messages.Write(new Domain.Common.UserMessage("problem.tooManyRequests")),
            Detail = Messages.Write(detail),
            Instance = context.Request.Path,
            Extensions = { ["code"] = "problem.tooManyRequests", ["retryAfter"] = seconds },
        };

        await response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The proxies whose <c>X-Forwarded-For</c> is believed: loopback, and the
    /// networks named in <c>ForwardedHeaders:KnownNetworks</c> - the compose
    /// network the admin panel's nginx and Caddy sit on. Anyone else's header
    /// is ignored, so a client cannot choose the address it is counted by.
    /// </summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Caddy, then the admin panel's nginx: two hops on the test server.
        options.ForwardLimit = configuration.GetValue("ForwardedHeaders:ForwardLimit", 2);

        foreach (var network in Networks(configuration))
        {
            options.KnownNetworks.Add(network);
        }
    }

    /// <summary>The configured networks: an array, or one comma-separated value (an environment variable).</summary>
    public static IReadOnlyList<IPNetwork> Networks(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("ForwardedHeaders:KnownNetworks");
        var values = section.Value is { } single
            ? single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(child => child.Value).OfType<string>().ToArray();

        return [.. values.Select(Parse)];
    }

    private static IPNetwork Parse(string cidr)
    {
        var parts = cidr.Split('/');

        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var prefix))
        {
            throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks: '{cidr}' is not a network such as 172.16.0.0/12.");
        }

        return new IPNetwork(address, prefix);
    }
}
