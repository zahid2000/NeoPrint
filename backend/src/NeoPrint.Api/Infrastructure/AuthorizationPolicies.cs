using Microsoft.AspNetCore.Authorization;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;

namespace NeoPrint.Api;

/// <summary>
/// The authorization policies, one per kind of principal (plan section 2.8).
/// </summary>
/// <remarks>
/// All four token types are issued by the same service and validated by the
/// same scheme, so what separates them is the token type claim. Checking it in
/// a policy means an endpoint for kiosks cannot be reached with a user token,
/// however valid that token is.
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>A registered mobile user.</summary>
    public const string User = "token:user";

    /// <summary>An administrator or operator.</summary>
    public const string Admin = "token:admin";

    /// <summary>An activated kiosk.</summary>
    public const string Kiosk = "token:kiosk";

    /// <summary>
    /// A device holding either a provisioning or a kiosk token: used by the
    /// endpoints a device needs before and after activation.
    /// </summary>
    public const string Device = "token:device";

    /// <summary>A device that is registered but not yet activated.</summary>
    public const string Provisioning = "token:provisioning";

    public static void AddNeoPrintPolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(User, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NeoPrintClaims.TokenType, TokenTypes.User));

        // Also confirms the account is still active, so a deactivated one is
        // refused even on the endpoints that need no particular permission -
        // reading one's own profile, for instance (plan section 8A.2).
        options.AddPolicy(Admin, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NeoPrintClaims.TokenType, TokenTypes.Admin)
            .AddRequirements(new AdminIsActiveRequirement()));

        // The three device policies also confirm the device's current state, so
        // detaching one takes effect on its next request rather than when its
        // token expires an hour later (plan section 7.11).
        options.AddPolicy(Kiosk, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NeoPrintClaims.TokenType, TokenTypes.Kiosk)
            .AddRequirements(new DeviceIsActiveRequirement()));

        options.AddPolicy(Provisioning, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NeoPrintClaims.TokenType, TokenTypes.Provisioning)
            .AddRequirements(new DeviceIsActiveRequirement()));

        options.AddPolicy(Device, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NeoPrintClaims.TokenType, TokenTypes.Provisioning, TokenTypes.Kiosk)
            .AddRequirements(new DeviceIsActiveRequirement()));
    }
}
