using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Requires a permission from the catalogue (plan section 8A.2).
/// </summary>
/// <remarks>
/// This is the real protection. The admin panel also hides what a user cannot
/// do, but that is only presentation - every admin endpoint is guarded here.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    /// <summary>Prefix that marks a policy name as a permission requirement.</summary>
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(string permission)
        : base(PolicyPrefix + permission)
    {
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>The requirement carried into the handler.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission) => Permission = permission;

    public string Permission { get; }
}

/// <summary>
/// Creates a policy on demand for each permission, so the catalogue does not
/// have to be registered policy by policy at startup.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            // Only an admin token can carry permissions.
            .RequireClaim(NeoPrintClaims.TokenType, TokenTypes.Admin)
            .AddRequirements(new PermissionRequirement(permission))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>
/// Decides whether the caller holds a permission.
/// </summary>
/// <remarks>
/// The permissions in the token are only a fast path. They are confirmed
/// against the resolver, whose cache is cleared when a role changes, so
/// revoking a permission takes effect without waiting for the token to expire
/// (plan section 8A.2).
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionResolver _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    public PermissionAuthorizationHandler(
        IPermissionResolver permissions,
        ICurrentUser currentUser,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        _permissions = permissions;
        _currentUser = currentUser;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return;
        }

        // Not in the token: nothing further to check, the answer is no.
        var claimed = context.User
            .FindAll(NeoPrintClaims.Permission)
            .Any(claim => string.Equals(claim.Value, requirement.Permission, StringComparison.Ordinal));

        if (!claimed)
        {
            return;
        }

        var effective = await _permissions.ResolveAsync(adminId.Value).ConfigureAwait(false);

        if (effective.Has(requirement.Permission))
        {
            context.Succeed(requirement);
            return;
        }

        // The token still carries a permission the account no longer has.
        _logger.LogInformation(
            "Administrator {AdminId} presented a token carrying '{Permission}', which has since been revoked.",
            adminId, requirement.Permission);
    }
}
