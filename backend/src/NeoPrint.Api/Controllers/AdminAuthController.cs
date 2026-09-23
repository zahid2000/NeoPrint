using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Sign-in for the admin panel (plan sections 2.8, 6).
/// </summary>
/// <remarks>
/// The refresh token travels as an HttpOnly, Secure, SameSite=Strict cookie
/// rather than in the response body, so no script in the panel can read it.
/// The panel and the API share an origin behind nginx, which is what lets a
/// strict cookie work at all (plan section 8A.1).
/// </remarks>
[ApiController]
[Route("api/auth/admin")]
[EnableRateLimiting("auth")]
public sealed class AdminAuthController : ControllerBase
{
    /// <summary>Name of the refresh cookie.</summary>
    public const string RefreshCookieName = "neoprint_admin_rt";

    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;
    private readonly IWebHostEnvironment _environment;

    public AdminAuthController(ISender sender, ICurrentUser currentUser, IWebHostEnvironment environment)
    {
        _sender = sender;
        _currentUser = currentUser;
        _environment = environment;
    }

    /// <summary>Signs an administrator in.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AdminLoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminLoginResponse>> Login(
        [FromBody] AdminLoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new AdminLoginCommand(request.Email, request.Password, _currentUser.Ip), cancellationToken);

        return Ok(Respond(result));
    }

    /// <summary>Exchanges the refresh cookie for a new access token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AdminLoginResponse>> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookieName, out var refreshToken) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unauthorized();
        }

        var result = await _sender.Send(new AdminRefreshCommand(refreshToken, _currentUser.Ip), cancellationToken);

        return Ok(Respond(result));
    }

    /// <summary>Signs the administrator out and clears the cookie.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (Request.Cookies.TryGetValue(RefreshCookieName, out var refreshToken) &&
            !string.IsNullOrWhiteSpace(refreshToken))
        {
            await _sender.Send(new AdminLogoutCommand(refreshToken), cancellationToken);
        }

        Response.Cookies.Delete(RefreshCookieName, CookieOptions(TimeSpan.Zero));

        return NoContent();
    }

    /// <summary>
    /// Changes the signed-in administrator's password. Mandatory at first
    /// sign-in for the seeded SuperAdmin.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        await _sender.Send(
            new ChangeAdminPasswordCommand(
                adminId.Value, request.CurrentPassword, request.NewPassword, _currentUser.Ip),
            cancellationToken);

        // Every session was signed out, including this one.
        Response.Cookies.Delete(RefreshCookieName, CookieOptions(TimeSpan.Zero));

        return NoContent();
    }

    /// <summary>The signed-in administrator, with their effective permissions and kiosk scope.</summary>
    [HttpGet("/api/admin/me")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<ActionResult<AdminSummary>> Me(
        [FromServices] IAdminUserRepository admins,
        [FromServices] IAdminRoleRepository roles,
        [FromServices] IPermissionResolver permissions,
        CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        var admin = await admins.FindByIdAsync(adminId.Value, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        var held = await roles.FindByIdsAsync(admin.RoleIds, cancellationToken);

        // Resolved rather than read from the token, so the panel sees a
        // permission change without signing in again (plan section 8A.2).
        var effective = await permissions.ResolveAsync(admin.Id, cancellationToken);

        return Ok(new AdminSummary(
            admin.Id,
            admin.Email,
            admin.FullName,
            [.. held.Select(role => role.Name)],
            effective.Permissions,
            effective.AllKiosks,
            effective.KioskIds,
            admin.MustChangePassword));
    }

    /// <summary>The permission catalogue, grouped as the role editor shows it.</summary>
    [HttpGet("/api/admin/permissions")]
    [HasPermission(Permissions.RolesManage)]
    public ActionResult<IReadOnlyList<PermissionGroup>> Catalogue() => Ok(Permissions.Catalogue);

    private AdminLoginResponse Respond(AdminAuthResult result)
    {
        Response.Cookies.Append(
            RefreshCookieName,
            result.RefreshToken,
            CookieOptions(result.RefreshTokenExpiresAt - DateTime.UtcNow));

        return new AdminLoginResponse(result.AccessToken, result.AccessTokenExpiresAt, result.Admin);
    }

    private CookieOptions CookieOptions(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        // Off in development, where the panel is served over plain HTTP; a
        // Secure cookie would simply never be stored.
        Secure = !_environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth/admin",
        Expires = lifetime <= TimeSpan.Zero ? DateTimeOffset.UnixEpoch : DateTimeOffset.UtcNow + lifetime,
    };

    public sealed record AdminLoginRequest(string Email, string Password);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    /// <summary>The refresh token is deliberately absent: it is in the cookie.</summary>
    public sealed record AdminLoginResponse(string AccessToken, DateTime AccessTokenExpiresAt, AdminSummary Admin);
}
