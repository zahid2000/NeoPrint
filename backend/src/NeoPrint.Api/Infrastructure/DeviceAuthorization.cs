using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Requires that the device a token names is still allowed to act
/// (plan section 7.11).
/// </summary>
/// <remarks>
/// Added to every device-facing policy. Without it a detached device would keep
/// working until its token expired an hour later, which contradicts both the
/// plan ("a device can be detached at any time - it becomes invalid at once")
/// and <c>docs/STATE_MACHINES.md</c> ("a revoked device must never be accepted
/// again").
/// </remarks>
public sealed class DeviceIsActiveRequirement : IAuthorizationRequirement;

/// <summary>
/// Confirms the claims in a device token against the device's current state.
/// </summary>
/// <remarks>
/// The same shape as <see cref="PermissionAuthorizationHandler"/>: what the
/// token says is a fast path, and the authoritative answer comes from the
/// database on every request.
/// </remarks>
public sealed class DeviceAuthorizationHandler : AuthorizationHandler<DeviceIsActiveRequirement>
{
    private readonly IDeviceAccessChecker _checker;

    public DeviceAuthorizationHandler(IDeviceAccessChecker checker) => _checker = checker;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DeviceIsActiveRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tokenType = context.User.FindFirstValue(NeoPrintClaims.TokenType);
        var deviceId = ParseGuid(context.User.FindFirstValue(NeoPrintClaims.DeviceId));

        if (tokenType is null || deviceId is null)
        {
            // A device policy with no device in the token: nothing to confirm,
            // so nothing is granted.
            return;
        }

        var result = await _checker
            .CheckAsync(
                deviceId.Value,
                tokenType,
                ParseGuid(context.User.FindFirstValue(NeoPrintClaims.KioskId)))
            .ConfigureAwait(false);

        if (result.IsAllowed)
        {
            context.Succeed(requirement);
        }
    }

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;
}
