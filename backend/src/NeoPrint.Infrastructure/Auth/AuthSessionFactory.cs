using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;
using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Security;

namespace NeoPrint.Infrastructure.Auth;

/// <summary>
/// Adapts <see cref="Hashing"/> to the interface the handlers depend on.
/// </summary>
public sealed class SecretHasher : ISecretHasher
{
    public string HashToken(string token) => Hashing.HashToken(token);

    public string HashSecret(string secret) => Hashing.HashSecret(secret);

    public bool VerifySecret(string secret, string? storedHash) => Hashing.VerifySecret(secret, storedHash);
}

/// <summary>
/// Builds a signed-in session (plan section 2.8): a 15 minute access token and
/// a 30 day refresh token, stored hashed and rotated on every use.
/// </summary>
public sealed class AuthSessionFactory : IAuthSessionFactory
{
    /// <summary>Plan section 2.8.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    private readonly ITokenService _tokens;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ILegalDocumentRepository _legal;
    private readonly ISecretHasher _secrets;
    private readonly IClock _clock;

    public AuthSessionFactory(
        ITokenService tokens,
        IRefreshTokenRepository refreshTokens,
        ILegalDocumentRepository legal,
        ISecretHasher secrets,
        IClock clock)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _legal = legal;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<AuthSession> CreateAsync(
        User user, string? deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var access = _tokens.CreateUserToken(user.Id, user.Email);

        var refreshToken = _tokens.CreateRefreshToken();
        var stored = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _secrets.HashToken(refreshToken),
            ExpiresAt = _clock.UtcNow + RefreshTokenLifetime,
            DeviceId = deviceId,
            CreatedAt = _clock.UtcNow,
        };

        await _refreshTokens.AddAsync(stored, cancellationToken).ConfigureAwait(false);

        var pending = await FindPendingConsentsAsync(user, cancellationToken).ConfigureAwait(false);

        return new AuthSession(
            access.Token,
            access.ExpiresAt,
            refreshToken,
            stored.ExpiresAt,
            new UserSummary(
                user.Id,
                user.PublicId,
                user.Email,
                user.FullName,
                user.Language,
                user.EmailConfirmed,
                pending),
            stored.Id);
    }

    /// <summary>
    /// Documents whose current version the user has not accepted.
    /// </summary>
    /// <remarks>
    /// Plan section 2.8: when a new version of a document is published, the
    /// user accepts it again at their next sign-in. The scan notice is left out
    /// - it is shown before the first scan, not at sign-in.
    /// </remarks>
    private async Task<IReadOnlyCollection<PendingConsent>> FindPendingConsentsAsync(
        User user, CancellationToken cancellationToken)
    {
        var current = await _legal.GetCurrentAsync(user.Language, cancellationToken).ConfigureAwait(false);
        var accepted = await _legal.GetAcceptedVersionsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        var pending = new List<PendingConsent>();

        foreach (var docType in new[] { LegalDocumentType.Privacy, LegalDocumentType.Terms })
        {
            if (!current.TryGetValue(docType, out var document))
            {
                continue;
            }

            if (!accepted.TryGetValue(docType, out var acceptedVersion) ||
                !string.Equals(acceptedVersion, document.Version, StringComparison.Ordinal))
            {
                pending.Add(new PendingConsent(docType, document.Version));
            }
        }

        return pending;
    }
}
