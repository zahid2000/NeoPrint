using Microsoft.AspNetCore.Mvc.Filters;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Features;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Closes an endpoint whose feature flag is off (plan section 2.16).
/// </summary>
/// <remarks>
/// Hiding a feature in the UI is presentation only. A flag that is off has to
/// close the endpoint as well, or the capability stays reachable to anyone who
/// calls it directly.
/// </remarks>
public sealed class RequireFeatureFilter : IAsyncActionFilter
{
    private readonly IFeatureFlagEvaluator _features;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<RequireFeatureFilter> _logger;

    public RequireFeatureFilter(
        IFeatureFlagEvaluator features, ICurrentUser currentUser, ILogger<RequireFeatureFilter> logger)
    {
        _features = features;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // The attribute can sit on the action or on the controller.
        var required = context.ActionDescriptor.EndpointMetadata
            .OfType<RequireFeatureAttribute>()
            .Select(attribute => attribute.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var key in required)
        {
            var enabled = await _features
                .IsEnabledAsync(key, _currentUser.KioskId, _currentUser.UserId, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (enabled)
            {
                continue;
            }

            _logger.LogInformation("Request refused: feature '{Feature}' is disabled.", key);

            // The same 404 as a refusal from inside a use case
            // (ProblemDetailsExceptionHandler), with the flag key, so a client
            // can refresh its flags and hide the entry point.
            throw new FeatureDisabledException(key);
        }

        await next().ConfigureAwait(false);
    }
}
