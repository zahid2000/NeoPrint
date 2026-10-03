using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Persistence for user accounts and everything attached to signing in.
/// </summary>
/// <remarks>
/// Handlers depend on these narrow interfaces rather than on the MongoDB
/// driver, so they can be exercised with hand-written fakes - plan section 11
/// rules out a mocking library.
/// </remarks>
public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<User?> FindByReferralCodeAsync(string referralCode, CancellationToken cancellationToken = default);

    Task<bool> ExistsByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<bool> ExistsByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    Task<bool> ExistsByReferralCodeAsync(string referralCode, CancellationToken cancellationToken = default);

    Task AddAsync(User user, CancellationToken cancellationToken = default);

    Task UpdateAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>The admin list of customers, newest first; deleted accounts are left out.</summary>
    Task<(IReadOnlyList<User> Items, long Total)> SearchAsync(
        Customers.CustomerFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>How many accounts this user invited (plan section 2.6).</summary>
    Task<long> CountReferredAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a device for this user's push, replacing an older entry for
    /// the same token. The token is taken off every other account first: a
    /// phone someone else signs in on stops getting the previous user's
    /// notifications. Beyond <see cref="User.MaxPushDevices"/> the oldest goes.
    /// </summary>
    Task SavePushDeviceAsync(Guid userId, PushDevice device, CancellationToken cancellationToken = default);

    /// <summary>Takes a push token off whichever account has it (signed out, or FCM no longer knows it).</summary>
    Task RemovePushDeviceAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for email confirmation codes.</summary>
public interface IEmailConfirmationRepository
{
    Task AddAsync(EmailConfirmation confirmation, CancellationToken cancellationToken = default);

    /// <summary>The most recent unconfirmed code for a user.</summary>
    Task<EmailConfirmation?> FindLatestPendingAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent code for a user, confirmed or not.
    /// </summary>
    /// <remarks>
    /// Used to answer a confirmation that arrives twice: the address is already
    /// confirmed, so there is no pending code, but the caller still has to
    /// present the right one (<c>docs/IDEMPOTENCY.md</c>).
    /// </remarks>
    Task<EmailConfirmation?> FindLatestAsync(Guid userId, CancellationToken cancellationToken = default);

    Task UpdateAsync(EmailConfirmation confirmation, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for refresh tokens.</summary>
public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default);

    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task UpdateAsync(RefreshToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every token a user holds.
    /// </summary>
    /// <remarks>
    /// Used when an already rotated token is presented, which means a token was
    /// stolen: the whole chain goes, not just the one that was replayed.
    /// </remarks>
    Task RevokeAllForUserAsync(Guid userId, DateTime revokedAt, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for legal documents and the consents recorded against them.</summary>
public interface ILegalDocumentRepository
{
    /// <summary>The newest published version of each document type, for one language.</summary>
    Task<IReadOnlyDictionary<LegalDocumentType, LegalDocument>> GetCurrentAsync(
        string language, CancellationToken cancellationToken = default);

    Task<LegalDocument?> GetCurrentAsync(
        LegalDocumentType docType, string language, CancellationToken cancellationToken = default);

    Task AddConsentsAsync(IEnumerable<Consent> consents, CancellationToken cancellationToken = default);

    /// <summary>The version of each document type this user has accepted.</summary>
    Task<IReadOnlyDictionary<LegalDocumentType, string>> GetAcceptedVersionsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every published version, newest first.
    /// </summary>
    /// <remarks>
    /// The admin panel lists the history: a published version has to stay
    /// identifiable, because consents point at it
    /// (<c>docs/DATA_RETENTION.md</c>).
    /// </remarks>
    Task<IReadOnlyList<LegalDocument>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Publishes a new version.</summary>
    Task AddAsync(LegalDocument document, CancellationToken cancellationToken = default);

    /// <summary>How many users have accepted one version of a document.</summary>
    Task<long> CountAcceptancesAsync(
        LegalDocumentType docType, string version, CancellationToken cancellationToken = default);
}
