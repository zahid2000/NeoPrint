using MediatR;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Admin;

/// <summary>
/// Signs an administrator in (plan section 2.8).
/// </summary>
/// <remarks>
/// Five failed attempts lock the account for fifteen minutes. The lockout is
/// reported plainly, because an administrator locked out by someone else's
/// attempts needs to understand why they cannot get in - and unlike the mobile
/// sign-in, the set of admin addresses is not a secret worth protecting at the
/// cost of that.
/// </remarks>
public sealed class AdminLoginHandler : IRequestHandler<AdminLoginCommand, AdminAuthResult>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IPermissionResolver _permissions;
    private readonly IPasswordHasher<AdminUser> _passwordHasher;
    private readonly IAdminSessionFactory _sessions;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<AdminLoginHandler> _logger;

    public AdminLoginHandler(
        IAdminUserRepository admins,
        IAdminRoleRepository roles,
        IPermissionResolver permissions,
        IPasswordHasher<AdminUser> passwordHasher,
        IAdminSessionFactory sessions,
        IAuditWriter audit,
        IClock clock,
        ILogger<AdminLoginHandler> logger)
    {
        _admins = admins;
        _roles = roles;
        _permissions = permissions;
        _passwordHasher = passwordHasher;
        _sessions = sessions;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AdminAuthResult> Handle(AdminLoginCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var email = User.NormalizeEmail(request.Email);
        var admin = await _admins.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        var invalid = new ValidationException([new ValidationError("", "Email və ya şifrə yanlışdır.")]);

        if (admin is null)
        {
            // Same cost as a real verification, so timing says nothing.
            _passwordHasher.HashPassword(new AdminUser(), request.Password);
            throw invalid;
        }

        if (admin.IsLockedOut(now))
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((admin.LockedUntil!.Value - now).TotalMinutes));
            throw new ValidationException(
                [new ValidationError("", $"Hesab müvəqqəti bloklanıb. {minutes} dəqiqə sonra yenidən cəhd edin.")]);
        }

        var verification = _passwordHasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            admin.RegisterFailedLogin(now);
            await _admins.UpdateAsync(admin, cancellationToken).ConfigureAwait(false);

            if (admin.IsLockedOut(now))
            {
                _logger.LogWarning("Admin account {AdminId} was locked after repeated failed sign-ins.", admin.Id);

                await _audit.WriteAsync(
                        new AuditEntry(admin.Id, "admin.locked", "AdminUser", admin.Id.ToString(),
                            null, null, request.Ip, "Too many failed sign-in attempts."),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw invalid;
        }

        if (!admin.IsActive)
        {
            throw new ValidationException([new ValidationError("", "Bu hesab deaktiv edilib.")]);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            admin.PasswordHash = _passwordHasher.HashPassword(admin, request.Password);
        }

        admin.RegisterSuccessfulLogin(now);
        await _admins.UpdateAsync(admin, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(admin.Id, "admin.login", "AdminUser", admin.Id.ToString(),
                    null, null, request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        var roles = await _roles.FindByIdsAsync(admin.RoleIds, cancellationToken).ConfigureAwait(false);
        var permissions = await _permissions.ResolveAsync(admin.Id, cancellationToken).ConfigureAwait(false);

        return await _sessions.CreateAsync(admin, roles, permissions, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Exchanges an admin refresh token for a new pair, rotating it.</summary>
public sealed class AdminRefreshHandler : IRequestHandler<AdminRefreshCommand, AdminAuthResult>
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IPermissionResolver _permissions;
    private readonly ISecretHasher _secrets;
    private readonly IAdminSessionFactory _sessions;
    private readonly IClock _clock;
    private readonly ILogger<AdminRefreshHandler> _logger;

    public AdminRefreshHandler(
        IRefreshTokenRepository tokens,
        IAdminUserRepository admins,
        IAdminRoleRepository roles,
        IPermissionResolver permissions,
        ISecretHasher secrets,
        IAdminSessionFactory sessions,
        IClock clock,
        ILogger<AdminRefreshHandler> logger)
    {
        _tokens = tokens;
        _admins = admins;
        _roles = roles;
        _permissions = permissions;
        _secrets = secrets;
        _sessions = sessions;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AdminAuthResult> Handle(AdminRefreshCommand request, CancellationToken cancellationToken)
    {
        var stored = await _tokens
            .FindByHashAsync(_secrets.HashToken(request.RefreshToken), cancellationToken)
            .ConfigureAwait(false);

        var invalid = new ValidationException(
            [new ValidationError("refreshToken", "Sessiya bitib. Yenidən daxil olun.")]);

        if (stored is null)
        {
            throw invalid;
        }

        if (!stored.IsActive(_clock.UtcNow))
        {
            if (stored.ReplacedBy is not null)
            {
                _logger.LogWarning(
                    "An already-rotated admin refresh token was presented for {AdminId}; revoking all of them.",
                    stored.UserId);

                await _tokens.RevokeAllForUserAsync(stored.UserId, _clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw invalid;
        }

        var admin = await _admins.FindByIdAsync(stored.UserId, cancellationToken).ConfigureAwait(false);
        if (admin is null || !admin.IsActive)
        {
            throw invalid;
        }

        var roles = await _roles.FindByIdsAsync(admin.RoleIds, cancellationToken).ConfigureAwait(false);

        // Resolved fresh, so a permission taken away since the last sign-in is
        // gone from the new token (plan section 8A.2).
        var permissions = await _permissions.ResolveAsync(admin.Id, cancellationToken).ConfigureAwait(false);

        var session = await _sessions.CreateAsync(admin, roles, permissions, cancellationToken).ConfigureAwait(false);

        stored.RevokedAt = _clock.UtcNow;
        stored.ReplacedBy = session.ReplacementTokenId;
        await _tokens.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

        return session;
    }
}

/// <summary>Revokes an admin refresh token.</summary>
public sealed class AdminLogoutHandler : IRequestHandler<AdminLogoutCommand, Unit>
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly ISecretHasher _secrets;
    private readonly IClock _clock;

    public AdminLogoutHandler(IRefreshTokenRepository tokens, ISecretHasher secrets, IClock clock)
    {
        _tokens = tokens;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<Unit> Handle(AdminLogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unit.Value;
        }

        var stored = await _tokens
            .FindByHashAsync(_secrets.HashToken(request.RefreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = _clock.UtcNow;
            await _tokens.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}

/// <summary>
/// Changes an administrator's own password.
/// </summary>
/// <remarks>
/// Every other session of that account is signed out: if the password is being
/// changed because it may have leaked, leaving other sessions alive would
/// defeat the point.
/// </remarks>
public sealed class ChangeAdminPasswordHandler : IRequestHandler<ChangeAdminPasswordCommand, Unit>
{
    private readonly IAdminUserRepository _admins;
    private readonly IRefreshTokenRepository _tokens;
    private readonly IPasswordHasher<AdminUser> _passwordHasher;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public ChangeAdminPasswordHandler(
        IAdminUserRepository admins,
        IRefreshTokenRepository tokens,
        IPasswordHasher<AdminUser> passwordHasher,
        IAuditWriter audit,
        IClock clock)
    {
        _admins = admins;
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Unit> Handle(ChangeAdminPasswordCommand request, CancellationToken cancellationToken)
    {
        var admin = await _admins.FindByIdAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("AdminUser", request.AdminUserId);

        var verification = _passwordHasher.VerifyHashedPassword(
            admin, admin.PasswordHash, request.CurrentPassword);

        if (verification == PasswordVerificationResult.Failed)
        {
            throw new ValidationException([new ValidationError("currentPassword", "Cari şifrə yanlışdır.")]);
        }

        admin.PasswordHash = _passwordHasher.HashPassword(admin, request.NewPassword);
        admin.MustChangePassword = false;

        await _admins.UpdateAsync(admin, cancellationToken).ConfigureAwait(false);
        await _tokens.RevokeAllForUserAsync(admin.Id, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(admin.Id, "admin.password.change", "AdminUser", admin.Id.ToString(),
                    null, null, request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}

/// <summary>Builds a signed-in administrator session.</summary>
public interface IAdminSessionFactory
{
    Task<AdminSession> CreateAsync(
        AdminUser admin,
        IReadOnlyList<AdminRole> roles,
        EffectivePermissions permissions,
        CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="AdminAuthResult"/> plus the stored refresh token's id.</summary>
public sealed record AdminSession(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    AdminSummary Admin,
    Guid ReplacementTokenId)
{
    public static implicit operator AdminAuthResult(AdminSession session)
        => new(session.AccessToken, session.AccessTokenExpiresAt,
            session.RefreshToken, session.RefreshTokenExpiresAt, session.Admin);
}
