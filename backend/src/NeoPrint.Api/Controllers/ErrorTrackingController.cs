using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;

namespace NeoPrint.Api.Controllers;

/// <summary>Where a client sends its unhandled errors (T-478); <c>dsn</c> null = do not report.</summary>
public sealed record ErrorTrackingResponse(string? Dsn, string Environment);

/// <summary>
/// The DSN each client reports its errors with (plan step 8, T-478). Anonymous:
/// a client has to be able to report a crash on its sign-in screen, and a DSN
/// is meant to be public - it lets a client send an error, nothing more.
/// </summary>
[ApiController]
[Route("api/error-tracking")]
[AllowAnonymous]
public sealed class ErrorTrackingController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public ErrorTrackingController(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    /// <param name="client"><c>admin</c>, <c>mobile</c> or <c>kiosk</c>.</param>
    [HttpGet("{client}")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public ActionResult<ErrorTrackingResponse> Get(string client)
    {
        if (!ErrorTracking.ClientKeys.TryGetValue(client, out var key))
        {
            return NotFound();
        }

        return Ok(new ErrorTrackingResponse(ErrorTracking.Dsn(_configuration, key), _environment.EnvironmentName));
    }
}
