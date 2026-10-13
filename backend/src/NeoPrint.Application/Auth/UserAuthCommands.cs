using MediatR;

using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Auth;

// ---------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------

/// <summary>What a client gets back after signing in.</summary>
public sealed record AuthResult(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    UserSummary User);

/// <summary>The profile fields a client needs right after signing in.</summary>
public sealed record UserSummary(
    Guid Id,
    string PublicId,
    string Email,
    string? FullName,
    string Language,
    bool EmailConfirmed,
    IReadOnlyCollection<PendingConsent> PendingConsents);

/// <summary>A document the user still has to accept (plan section 2.8).</summary>
public sealed record PendingConsent(LegalDocumentType DocType, string Version);

/// <summary>Outcome of registering; there is no token until the email is confirmed.</summary>
public sealed record RegistrationResult(Guid UserId, string Email, bool ConfirmationRequired);

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

/// <summary>
/// Registers a mobile user (plan section 2.8).
/// </summary>
/// <remarks>
/// Transactional: the user, the accepted consents and the confirmation code are
/// written together, so a half-registered account cannot exist.
/// </remarks>
public sealed record RegisterUserCommand(
    string Email,
    string Password,
    string? FullName,
    string Language,
    string? ReferralCode,
    string? DeviceId,
    IReadOnlyCollection<ConsentAcceptance> Consents,
    string? Ip) : IRequest<RegistrationResult>, ITransactionalRequest;

/// <summary>One document the user accepted, with the version they saw.</summary>
public sealed record ConsentAcceptance(LegalDocumentType DocType, string Version);

/// <summary>Confirms an email address with the six-digit code that was sent.</summary>
public sealed record ConfirmEmailCommand(string Email, string Code, string? DeviceId)
    : IRequest<AuthResult>;

/// <summary>Sends a fresh confirmation code.</summary>
public sealed record ResendConfirmationCommand(string Email) : IRequest<Unit>;

/// <summary>Signs a user in with email and password.</summary>
public sealed record LoginUserCommand(string Email, string Password, string? DeviceId)
    : IRequest<AuthResult>;

/// <summary>Exchanges a refresh token for a new pair, rotating it.</summary>
public sealed record RefreshTokenCommand(string RefreshToken, string? DeviceId) : IRequest<AuthResult>;

/// <summary>Revokes a refresh token.</summary>
/// <param name="FcmToken">
/// The push token of the device that is signing out: taken off the account in
/// the same request, so it needs no access token - which may have run out by
/// the time the user signs out (T-268).
/// </param>
public sealed record LogoutCommand(string RefreshToken, string? FcmToken = null) : IRequest<Unit>;

/// <summary>Records the user's acceptance of the current document versions.</summary>
public sealed record AcceptConsentsCommand(
    Guid UserId,
    IReadOnlyCollection<ConsentAcceptance> Consents,
    string? DeviceId,
    string? Ip) : IRequest<Unit>, ITransactionalRequest;

// ---------------------------------------------------------------------------
// Validators
// ---------------------------------------------------------------------------

/// <summary>
/// Shared rules for the fields several commands have in common.
/// </summary>
internal static class AuthRules
{
    /// <summary>Plan section 2.1: az (default), en and ru.</summary>
    public static readonly string[] SupportedLanguages = ["az", "en", "ru"];

    /// <summary>
    /// Minimum password length. Length is the rule that actually helps;
    /// composition rules mostly push people towards predictable substitutions.
    /// </summary>
    public const int MinimumPasswordLength = 8;

    public static IEnumerable<ValidationError> Email(string? email, string field = "email")
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            yield return new ValidationError(field, "auth.emailRequired");
            yield break;
        }

        // A single @ with something either side, and a dot in the domain, is as
        // much as is worth checking: the confirmation code proves the rest.
        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1 ||
            !trimmed[(at + 1)..].Contains('.', StringComparison.Ordinal) || trimmed.Length > 254)
        {
            yield return new ValidationError(field, "auth.emailInvalid");
        }
    }

    public static IEnumerable<ValidationError> Password(string? password, string field = "password")
    {
        if (string.IsNullOrEmpty(password))
        {
            yield return new ValidationError(field, "auth.passwordRequired");
            yield break;
        }

        if (password.Length < MinimumPasswordLength)
        {
            yield return new ValidationError(
                field, "auth.passwordTooShort", ("min", MinimumPasswordLength));
        }

        if (password.Length > 128)
        {
            yield return new ValidationError(field, "auth.passwordTooLong");
        }
    }

    public static IEnumerable<ValidationError> Language(string? language, string field = "language")
    {
        if (!string.IsNullOrWhiteSpace(language) && !SupportedLanguages.Contains(language))
        {
            yield return new ValidationError(
                field, "common.languageInvalid");
        }
    }
}

public sealed class RegisterUserCommandValidator : Abstractions.IValidator<RegisterUserCommand>
{
    public IEnumerable<ValidationError> Validate(RegisterUserCommand request)
    {
        foreach (var error in AuthRules.Email(request.Email))
        {
            yield return error;
        }

        foreach (var error in AuthRules.Password(request.Password))
        {
            yield return error;
        }

        foreach (var error in AuthRules.Language(request.Language))
        {
            yield return error;
        }

        if (request.FullName is { Length: > 120 })
        {
            yield return new ValidationError("fullName", "auth.nameTooLong");
        }

        // Plan section 2.8: the privacy policy and the terms are accepted at
        // registration. Refusing here means an account cannot exist without a
        // recorded consent.
        var accepted = request.Consents.Select(consent => consent.DocType).ToHashSet();

        if (!accepted.Contains(LegalDocumentType.Privacy))
        {
            yield return new ValidationError("consents", "auth.privacyRequired");
        }

        if (!accepted.Contains(LegalDocumentType.Terms))
        {
            yield return new ValidationError("consents", "auth.termsRequired");
        }

        if (request.Consents.Any(consent => string.IsNullOrWhiteSpace(consent.Version)))
        {
            yield return new ValidationError("consents", "auth.consentVersionRequired");
        }
    }
}

public sealed class ConfirmEmailCommandValidator : Abstractions.IValidator<ConfirmEmailCommand>
{
    public IEnumerable<ValidationError> Validate(ConfirmEmailCommand request)
    {
        foreach (var error in AuthRules.Email(request.Email))
        {
            yield return error;
        }

        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Trim().Length != 6 ||
            !request.Code.Trim().All(char.IsAsciiDigit))
        {
            yield return new ValidationError("code", "auth.codeFormat");
        }
    }
}

public sealed class LoginUserCommandValidator : Abstractions.IValidator<LoginUserCommand>
{
    public IEnumerable<ValidationError> Validate(LoginUserCommand request)
    {
        foreach (var error in AuthRules.Email(request.Email))
        {
            yield return error;
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            yield return new ValidationError("password", "auth.passwordRequired");
        }
    }
}

public sealed class RefreshTokenCommandValidator : Abstractions.IValidator<RefreshTokenCommand>
{
    public IEnumerable<ValidationError> Validate(RefreshTokenCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            yield return new ValidationError("refreshToken", "auth.sessionMissing");
        }
    }
}

public sealed class ResendConfirmationCommandValidator : Abstractions.IValidator<ResendConfirmationCommand>
{
    public IEnumerable<ValidationError> Validate(ResendConfirmationCommand request)
        => AuthRules.Email(request.Email);
}
