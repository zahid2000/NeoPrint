using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Infrastructure.Monitoring;

using Sentry;

namespace NeoPrint.Api.Controllers;

/// <summary>Whether monitoring is on, and where its tools are (plan step 8, T-477, T-478).</summary>
/// <param name="MetricsEnabled">The API sends its metrics to a collector.</param>
/// <param name="GrafanaUrl">Grafana's address, as the browser opens it; null when not configured.</param>
/// <param name="CountedAt">When the operational figures were last counted; null before the first count or with metrics off.</param>
/// <param name="ErrorTrackingEnabled">The API reports its unhandled errors to GlitchTip.</param>
/// <param name="GlitchTipUrl">GlitchTip's address, as the browser opens it; null when not configured.</param>
public sealed record MonitoringStatusResponse(
    bool MetricsEnabled,
    string? GrafanaUrl,
    DateTime? CountedAt,
    bool ErrorTrackingEnabled,
    string? GlitchTipUrl);

/// <summary>The test error that was sent; <see cref="EventId"/> is what GlitchTip shows it under.</summary>
public sealed record MonitoringTestErrorResponse(string EventId);

/// <summary>
/// "Monitorinq" in the admin panel: the way to the dashboards and to the
/// error tracker. Grafana and GlitchTip have their own logins
/// (docs/MONITORING.md); this only says where they are and whether data flows.
/// </summary>
[ApiController]
[Route("api/admin/monitoring")]
public sealed class AdminMonitoringController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly OperationalMetrics? _metrics;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AdminMonitoringController> _logger;

    public AdminMonitoringController(
        IConfiguration configuration,
        IServiceProvider services,
        ICurrentUser currentUser,
        ILogger<AdminMonitoringController> logger)
    {
        _configuration = configuration;
        // Registered only when monitoring is on.
        _metrics = services.GetService<OperationalMetrics>();
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpGet]
    [HasPermission(Permissions.MonitoringView)]
    public ActionResult<MonitoringStatusResponse> Get() => Ok(new MonitoringStatusResponse(
        _metrics is not null,
        BrowserUrl("GrafanaUrl"),
        _metrics?.CountedAt,
        SentrySdk.IsEnabled,
        BrowserUrl("GlitchTipUrl")));

    /// <summary>
    /// Sends one error to GlitchTip on purpose, so an administrator can see the
    /// whole way works. Audited by the log line; carries no user data.
    /// </summary>
    [HttpPost("test-error")]
    [HasPermission(Permissions.MonitoringView)]
    public async Task<ActionResult<MonitoringTestErrorResponse>> TestError(CancellationToken cancellationToken)
    {
        if (!SentrySdk.IsEnabled)
        {
            throw new DomainException("monitoring.errorTrackingOff");
        }

        var eventId = SentrySdk.CaptureException(new MonitoringTestException());
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        _logger.LogInformation(
            "Admin {AdminUserId} sent a monitoring test error {EventId}", _currentUser.AdminUserId, eventId);

        return Ok(new MonitoringTestErrorResponse(eventId.ToString()));
    }

    /// <summary>A configured address the panel may link to: http(s) only.</summary>
    private string? BrowserUrl(string key)
    {
        var value = _configuration[$"{MonitoringRegistration.SectionName}:{key}"];

        return Uri.TryCreate(value, UriKind.Absolute, out var url)
               && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;
    }
}

/// <summary>The error the "Sınaq xətası göndər" button sends - nothing went wrong.</summary>
public sealed class MonitoringTestException : Exception
{
    public MonitoringTestException()
        : base("NeoPrint monitoring test error - sent on purpose from the admin panel; nothing is broken.")
    {
    }

    public MonitoringTestException(string message)
        : base(message)
    {
    }

    public MonitoringTestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
