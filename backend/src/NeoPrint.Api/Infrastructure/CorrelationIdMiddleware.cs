using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Observability;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Gives every request a correlation id and puts it in scope for the logs
/// (<c>docs/OBSERVABILITY.md</c>).
/// </summary>
/// <remarks>
/// Runs before anything else that logs, so the id appears on every line a
/// request produces - including the ones written when authentication or the
/// exception handler refuses it. The id is echoed back, so a client that
/// reports a problem can quote it, and the mobile app and the kiosk can send
/// their own to tie a user-visible failure to the server side.
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correlation);

        var incoming = context.Request.Headers[CorrelationHeaders.CorrelationId].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(incoming))
        {
            correlation.Set(incoming);
        }

        var correlationId = correlation.CorrelationId;

        // Set before the response starts, since headers cannot be added once
        // the body is on its way.
        context.Response.Headers[CorrelationHeaders.CorrelationId] = correlationId;

        // ASP.NET Core's own TraceIdentifier stays available for anything that
        // reads it, but the correlation id is what crosses process boundaries.
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = context.TraceIdentifier,
        });

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>Registration helpers for the correlation id.</summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds the correlation id to the pipeline. Place it first, so everything
    /// downstream logs with it.
    /// </summary>
    public static IApplicationBuilder UseNeoPrintCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
