using MediatR;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Auth;

/// <summary>
/// Hashing and verification of the low-entropy secrets these handlers deal
/// with: email confirmation codes.
/// </summary>
/// <remarks>
/// Implemented in the infrastructure layer, so the handlers stay free of
/// cryptographic detail and can be tested with a trivial fake.
/// </remarks>
public interface ISecretHasher
{
    string HashToken(string token);

    string HashSecret(string secret);

    bool VerifySecret(string secret, string? storedHash);
}

/// <summary>
/// Registers a user (plan section 2.8).
/// </summary>
/// <remarks>
/// A duplicate email is not reported as such: telling an anonymous caller
/// whether an address is registered would turn this endpoint into an account
/// oracle. The response is the same either way, and the existing owner gets an
/// email instead.
/// </remarks>
public sealed class RegisterUserHandler : IRequestHandler<RegisterUserCommand, RegistrationResult>
{
    private readonly IUserRepository _users;
    private readonly IEmailConfirmationRepository _confirmations;
    private readonly ILegalDocumentRepository _legal;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly ISecretHasher _secrets;
    private readonly IEmailSender _email;
    private readonly IClock _clock;
    private readonly ILogger<RegisterUserHandler> _logger;

    /// <summary>How long a confirmation code stays valid.</summary>
    public static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(30);

    public RegisterUserHandler(
        IUserRepository users,
        IEmailConfirmationRepository confirmations,
        ILegalDocumentRepository legal,
        IPasswordHasher<User> passwordHasher,
        ISecretHasher secrets,
        IEmailSender email,
        IClock clock,
        ILogger<RegisterUserHandler> logger)
    {
        _users = users;
        _confirmations = confirmations;
        _legal = legal;
        _passwordHasher = passwordHasher;
        _secrets = secrets;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RegistrationResult> Handle(RegisterUserCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);

        var existing = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Same shape of response as a successful registration. The person
            // who actually owns the address is told someone tried.
            _logger.LogInformation("Registration attempted for an address that is already in use.");

            await SendAlreadyRegisteredNoticeAsync(email, cancellationToken).ConfigureAwait(false);

            return new RegistrationResult(existing.Id, email, ConfirmationRequired: true);
        }

        var user = new User
        {
            Email = email,
            FullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName.Trim(),
            Language = string.IsNullOrWhiteSpace(request.Language) ? "az" : request.Language,
            PublicId = await GenerateUniquePublicIdAsync(cancellationToken).ConfigureAwait(false),
            ReferralCode = await GenerateUniqueReferralCodeAsync(cancellationToken).ConfigureAwait(false),
            EmailConfirmed = false,
            CreatedAt = _clock.UtcNow,
        };

        if (!string.IsNullOrWhiteSpace(request.DeviceId))
        {
            user.DeviceIds.Add(request.DeviceId);
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        if (!string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            var inviter = await _users
                .FindByReferralCodeAsync(request.ReferralCode.Trim().ToUpperInvariant(), cancellationToken)
                .ConfigureAwait(false);

            // An unknown code is ignored rather than rejected: it must not stop
            // someone registering. Self-referral is impossible here because the
            // account does not exist yet.
            if (inviter is not null)
            {
                user.ReferredBy = inviter.Id;
            }
        }

        await _users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        await _legal.AddConsentsAsync(
                request.Consents.Select(consent => new Consent
                {
                    UserId = user.Id,
                    DocType = consent.DocType,
                    Version = consent.Version,
                    AcceptedAt = _clock.UtcNow,
                    Ip = request.Ip,
                    DeviceId = request.DeviceId,
                }),
                cancellationToken)
            .ConfigureAwait(false);

        await IssueConfirmationCodeAsync(user, cancellationToken).ConfigureAwait(false);

        return new RegistrationResult(user.Id, email, ConfirmationRequired: true);
    }

    private async Task IssueConfirmationCodeAsync(User user, CancellationToken cancellationToken)
    {
        var code = EmailConfirmation.NewCode();

        await _confirmations.AddAsync(
                new EmailConfirmation
                {
                    UserId = user.Id,
                    CodeHash = _secrets.HashSecret(code),
                    ExpiresAt = _clock.UtcNow + ConfirmationLifetime,
                    CreatedAt = _clock.UtcNow,
                },
                cancellationToken)
            .ConfigureAwait(false);

        await _email.SendAsync(
                user.Email,
                "NeoPrint — email təsdiqi",
                $"""
                 <p>NeoPrint hesabınızı təsdiqləmək üçün kod:</p>
                 <p style="font-size:28px;letter-spacing:6px;font-weight:bold">{code}</p>
                 <p>Kod {ConfirmationLifetime.TotalMinutes:0} dəqiqə ərzində etibarlıdır.</p>
                 <p>Bu qeydiyyatı siz etməmisinizsə, bu məktubu nəzərə almayın.</p>
                 """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task SendAlreadyRegisteredNoticeAsync(string email, CancellationToken cancellationToken)
        => _email.SendAsync(
            email,
            "NeoPrint — qeydiyyat cəhdi",
            """
            <p>Bu email ünvanı ilə NeoPrint hesabı artıq mövcuddur.</p>
            <p>Şifrənizi unutmusunuzsa, tətbiqdəki "Şifrəni unutdum" bölməsindən istifadə edin.</p>
            <p>Bu cəhd sizə aid deyilsə, heç nə etmək lazım deyil.</p>
            """,
            cancellationToken);

    private async Task<string> GenerateUniquePublicIdAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = User.NewPublicId();
            if (!await _users.ExistsByPublicIdAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a unique public id after ten attempts.");
    }

    private async Task<string> GenerateUniqueReferralCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = User.NewReferralCode();
            if (!await _users.ExistsByReferralCodeAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a unique referral code after ten attempts.");
    }
}

/// <summary>
/// Confirms an email address and signs the user in.
/// </summary>
public sealed class ConfirmEmailHandler : IRequestHandler<ConfirmEmailCommand, AuthResult>
{
    /// <summary>Attempts allowed per code before it is burned.</summary>
    public const int MaxAttempts = 5;

    private readonly IUserRepository _users;
    private readonly IEmailConfirmationRepository _confirmations;
    private readonly ISecretHasher _secrets;
    private readonly IAuthSessionFactory _sessions;
    private readonly IClock _clock;

    public ConfirmEmailHandler(
        IUserRepository users,
        IEmailConfirmationRepository confirmations,
        ISecretHasher secrets,
        IAuthSessionFactory sessions,
        IClock clock)
    {
        _users = users;
        _confirmations = confirmations;
        _secrets = secrets;
        _sessions = sessions;
        _clock = clock;
    }

    public async Task<AuthResult> Handle(ConfirmEmailCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        // One message for every failure, so this cannot be used to discover
        // which addresses are registered.
        var invalid = new ValidationException([new ValidationError("code", "Kod yanlışdır və ya vaxtı keçib.")]);

        if (user is null)
        {
            throw invalid;
        }

        if (user.EmailConfirmed)
        {
            // Confirming twice is not an error: a client whose response was
            // lost has to be able to send the same request again and get the
            // same answer (docs/IDEMPOTENCY.md). The code is still checked,
            // though - handing out a session because the address happens to be
            // confirmed would mean knowing a registered address is enough to
            // sign in as its owner, with no password anywhere in sight.
            var used = await _confirmations.FindLatestAsync(user.Id, cancellationToken).ConfigureAwait(false);

            if (used?.ConfirmedAt is null
                || used.ExpiresAt <= _clock.UtcNow
                || !_secrets.VerifySecret(request.Code.Trim(), used.CodeHash))
            {
                throw invalid;
            }

            return await _sessions.CreateAsync(user, request.DeviceId, cancellationToken).ConfigureAwait(false);
        }

        var confirmation = await _confirmations.FindLatestPendingAsync(user.Id, cancellationToken)
            .ConfigureAwait(false);

        if (confirmation is null || confirmation.ExpiresAt <= _clock.UtcNow || confirmation.Attempts >= MaxAttempts)
        {
            throw invalid;
        }

        if (!_secrets.VerifySecret(request.Code.Trim(), confirmation.CodeHash))
        {
            // Count the attempt, so a six-digit code cannot be guessed.
            confirmation.Attempts++;
            await _confirmations.UpdateAsync(confirmation, cancellationToken).ConfigureAwait(false);
            throw invalid;
        }

        confirmation.ConfirmedAt = _clock.UtcNow;
        await _confirmations.UpdateAsync(confirmation, cancellationToken).ConfigureAwait(false);

        user.EmailConfirmed = true;
        if (!string.IsNullOrWhiteSpace(request.DeviceId) && !user.DeviceIds.Contains(request.DeviceId))
        {
            user.DeviceIds.Add(request.DeviceId);
        }

        await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);

        return await _sessions.CreateAsync(user, request.DeviceId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Signs a user in with email and password.
/// </summary>
public sealed class LoginUserHandler : IRequestHandler<LoginUserCommand, AuthResult>
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IAuthSessionFactory _sessions;

    public LoginUserHandler(
        IUserRepository users, IPasswordHasher<User> passwordHasher, IAuthSessionFactory sessions)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _sessions = sessions;
    }

    public async Task<AuthResult> Handle(LoginUserCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        var invalid = new ValidationException(
            [new ValidationError("", "Email və ya şifrə yanlışdır.")]);

        if (user is null)
        {
            // Hash a throwaway value so a missing account takes the same time
            // as a wrong password; otherwise the response time says which
            // addresses exist.
            _passwordHasher.HashPassword(new User(), request.Password);
            throw invalid;
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            throw invalid;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The hasher's work factor has moved on; upgrade the stored hash.
            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
            await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
        }

        if (!user.CanSignIn)
        {
            throw new ValidationException(
                [new ValidationError("", "Bu hesab bloklanıb. Dəstəklə əlaqə saxlayın.")]);
        }

        if (!user.EmailConfirmed)
        {
            throw new ValidationException(
                [new ValidationError("", "Email ünvanı hələ təsdiqlənməyib. Tətbiqdəki kodu daxil edin.")]);
        }

        if (!string.IsNullOrWhiteSpace(request.DeviceId) && !user.DeviceIds.Contains(request.DeviceId))
        {
            user.DeviceIds.Add(request.DeviceId);
            await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
        }

        return await _sessions.CreateAsync(user, request.DeviceId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Exchanges a refresh token for a new pair.
/// </summary>
/// <remarks>
/// Rotating: the presented token is revoked and replaced. A token that has
/// already been rotated being presented again means it was stolen, so every
/// token the user holds is revoked (plan section 2.8).
/// </remarks>
public sealed class RefreshTokenHandler : IRequestHandler<RefreshTokenCommand, AuthResult>
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly IUserRepository _users;
    private readonly ISecretHasher _secrets;
    private readonly IAuthSessionFactory _sessions;
    private readonly IClock _clock;
    private readonly ILogger<RefreshTokenHandler> _logger;

    public RefreshTokenHandler(
        IRefreshTokenRepository tokens,
        IUserRepository users,
        ISecretHasher secrets,
        IAuthSessionFactory sessions,
        IClock clock,
        ILogger<RefreshTokenHandler> logger)
    {
        _tokens = tokens;
        _users = users;
        _secrets = secrets;
        _sessions = sessions;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AuthResult> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var hash = _secrets.HashToken(request.RefreshToken);
        var stored = await _tokens.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);

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
                // Replay of a rotated token: assume theft and end every session.
                _logger.LogWarning(
                    "A already-rotated refresh token was presented for user {UserId}; revoking all their tokens.",
                    stored.UserId);

                await _tokens.RevokeAllForUserAsync(stored.UserId, _clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw invalid;
        }

        var user = await _users.FindByIdAsync(stored.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.CanSignIn)
        {
            throw invalid;
        }

        var session = await _sessions
            .CreateAsync(user, request.DeviceId ?? stored.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        stored.RevokedAt = _clock.UtcNow;
        stored.ReplacedBy = session.ReplacementTokenId;
        await _tokens.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

        return session;
    }
}

/// <summary>Revokes a refresh token.</summary>
public sealed class LogoutHandler : IRequestHandler<LogoutCommand, Unit>
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly ISecretHasher _secrets;
    private readonly IClock _clock;

    public LogoutHandler(IRefreshTokenRepository tokens, ISecretHasher secrets, IClock clock)
    {
        _tokens = tokens;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<Unit> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unit.Value;
        }

        var stored = await _tokens
            .FindByHashAsync(_secrets.HashToken(request.RefreshToken), cancellationToken)
            .ConfigureAwait(false);

        // Signing out always succeeds. An unknown or already revoked token is
        // the state the caller wanted anyway.
        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = _clock.UtcNow;
            await _tokens.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}

/// <summary>Records acceptance of the current document versions.</summary>
public sealed class AcceptConsentsHandler : IRequestHandler<AcceptConsentsCommand, Unit>
{
    private readonly ILegalDocumentRepository _legal;
    private readonly IClock _clock;

    public AcceptConsentsHandler(ILegalDocumentRepository legal, IClock clock)
    {
        _legal = legal;
        _clock = clock;
    }

    public async Task<Unit> Handle(AcceptConsentsCommand request, CancellationToken cancellationToken)
    {
        await _legal.AddConsentsAsync(
                request.Consents.Select(consent => new Consent
                {
                    UserId = request.UserId,
                    DocType = consent.DocType,
                    Version = consent.Version,
                    AcceptedAt = _clock.UtcNow,
                    Ip = request.Ip,
                    DeviceId = request.DeviceId,
                }),
                cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}

/// <summary>
/// Builds a signed-in session: an access token, a stored refresh token and the
/// profile summary, including any document the user still has to accept.
/// </summary>
public interface IAuthSessionFactory
{
    Task<AuthSession> CreateAsync(User user, string? deviceId, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="AuthResult"/> plus the id of the refresh token that was stored.</summary>
public sealed record AuthSession(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    UserSummary User,
    Guid ReplacementTokenId)
{
    public static implicit operator AuthResult(AuthSession session)
        => new(session.AccessToken, session.AccessTokenExpiresAt,
            session.RefreshToken, session.RefreshTokenExpiresAt, session.User);
}
