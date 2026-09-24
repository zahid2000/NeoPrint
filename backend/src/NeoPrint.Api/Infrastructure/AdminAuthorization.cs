using Microsoft.AspNetCore.Authorization;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Requires that the account an admin token names is still active
/// (plan section 8A.2).
/// </summary>
/// <remarks>
/// Added to the admin policy itself rather than to each permission, because
/// some admin endpoints need no particular permission - reading one's own
/// profile, for one - and without this a deactivated account could still use
/// them until its token expired. The permission handler covers the rest, since
/// a deactivated account resolves to no permissions at all.
/// </remarks>
public sealed class AdminIsActiveRequirement : IAuthorizationRequirement;

/// <summary>
/// Confirms the account behind an admin token is still allowed to act.
/// </summary>
/// <remarks>
/// The same shape as the device check: the claims in the token are a fast path,
/// and the authoritative answer comes from the resolver - whose cache is
/// cleared when the account changes - on every request.
/// </remarks>
public sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminIsActiveRequirement>
{
    private readonly IPermissionResolver _permissions;
    private readonly ILogger<AdminAuthorizationHandler> _logger;

    public AdminAuthorizationHandler(
        IPermissionResolver permissions, ILogger<AdminAuthorizationHandler> logger)
    {
        _permissions = permissions;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminIsActiveRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var adminId = ParseGuid(context.User.FindFirst(NeoPrintClaims.AdminUserId)?.Value);

        if (adminId is null)
        {
            // An admin policy with no admin in the token: nothing to confirm.
            return;
        }

        var effective = await _permissions.ResolveAsync(adminId.Value).ConfigureAwait(false);

        // An account with a role that grants nothing is still a live account
        // and keeps its profile; one that has been deactivated or deleted is
        // refused outright.
        if (effective.IsActive)
        {
            context.Succeed(requirement);
            return;
        }

        _logger.LogWarning(
            "Administrator {AdminId} presented a token for an account that is no longer active.",
            adminId);
    }

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;
}
