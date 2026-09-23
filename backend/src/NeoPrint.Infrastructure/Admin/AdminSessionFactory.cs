using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Auth;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Users;

namespace NeoPrint.Infrastructure.Admin;

/// <summary>
/// Builds a signed-in administrator session (plan section 2.8).
/// </summary>
/// <remarks>
/// The access token lives in memory in the panel; the refresh token is sent as
/// an HttpOnly, Secure, SameSite=Strict cookie by the controller. Because the
/// panel and the API share an origin behind nginx, a strict cookie works
/// without any CORS relaxation (plan section 8A.1).
/// </remarks>
public sealed class AdminSessionFactory : IAdminSessionFactory
{
    /// <summary>
    /// Shorter than the mobile refresh token: an admin session grants far more,
    /// and administrators work from managed machines where signing in again is
    /// cheap.
    /// </summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    private readonly ITokenService _tokens;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ISecretHasher _secrets;
    private readonly IClock _clock;

    public AdminSessionFactory(
        ITokenService tokens,
        IRefreshTokenRepository refreshTokens,
        ISecretHasher secrets,
        IClock clock)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<AdminSession> CreateAsync(
        AdminUser admin,
        IReadOnlyList<AdminRole> roles,
        EffectivePermissions permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);

        var access = _tokens.CreateAdminToken(
            admin.Id,
            admin.Email,
            permissions.Permissions,
            permissions.AllKiosks,
            permissions.KioskIds,
            admin.MustChangePassword);

        var refreshToken = _tokens.CreateRefreshToken();

        var stored = new RefreshToken
        {
            UserId = admin.Id,
            TokenHash = _secrets.HashToken(refreshToken),
            ExpiresAt = _clock.UtcNow + RefreshTokenLifetime,
            CreatedAt = _clock.UtcNow,
        };

        await _refreshTokens.AddAsync(stored, cancellationToken).ConfigureAwait(false);

        return new AdminSession(
            access.Token,
            access.ExpiresAt,
            refreshToken,
            stored.ExpiresAt,
            new AdminSummary(
                admin.Id,
                admin.Email,
                admin.FullName,
                [.. roles.Select(role => role.Name)],
                permissions.Permissions,
                permissions.AllKiosks,
                permissions.KioskIds,
                admin.MustChangePassword),
            stored.Id);
    }
}
