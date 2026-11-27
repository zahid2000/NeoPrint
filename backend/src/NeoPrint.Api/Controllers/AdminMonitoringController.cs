using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Monitoring;

namespace NeoPrint.Api.Controllers;

/// <summary>Whether monitoring is on, and where Grafana is (plan step 8, T-477).</summary>
/// <param name="MetricsEnabled">The API sends its metrics to a collector.</param>
/// <param name="GrafanaUrl">Grafana's address, as the browser opens it; null when not configured.</param>
/// <param name="CountedAt">When the operational figures were last counted; null before the first count or with metrics off.</param>
public sealed record MonitoringStatusResponse(bool MetricsEnabled, string? GrafanaUrl, DateTime? CountedAt);

/// <summary>
/// "Monitorinq" in the admin panel: the way to the dashboards. Grafana has its
/// own login (docs/MONITORING.md); this only tells the panel where it is and
/// whether figures are flowing.
/// </summary>
[ApiController]
[Route("api/admin/monitoring")]
public sealed class AdminMonitoringController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly OperationalMetrics? _metrics;

    public AdminMonitoringController(IConfiguration configuration, IServiceProvider services)
    {
        _configuration = configuration;
        // Registered only when monitoring is on.
        _metrics = services.GetService<OperationalMetrics>();
    }

    [HttpGet]
    [HasPermission(Permissions.MonitoringView)]
    public ActionResult<MonitoringStatusResponse> Get()
    {
        var grafana = _configuration[$"{MonitoringRegistration.SectionName}:GrafanaUrl"];

        // Only an http(s) address reaches the panel's link.
        if (!Uri.TryCreate(grafana, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            grafana = null;
        }

        return Ok(new MonitoringStatusResponse(_metrics is not null, grafana, _metrics?.CountedAt));
    }
}
