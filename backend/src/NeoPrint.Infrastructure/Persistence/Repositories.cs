using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Users;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IUserRepository" />
public sealed class UserRepository : IUserRepository
{
    private readonly MongoContext _context;

    public UserRepository(MongoContext context) => _context = context;

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        => _context.Users.Find(user => user.Email == normalizedEmail).FirstOrDefaultAsync(cancellationToken)!;

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Users.Find(user => user.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<User?> FindByReferralCodeAsync(string referralCode, CancellationToken cancellationToken = default)
        => _context.Users.Find(user => user.ReferralCode == referralCode).FirstOrDefaultAsync(cancellationToken)!;

    public Task<bool> ExistsByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        => _context.Users.Find(user => user.Email == normalizedEmail).AnyAsync(cancellationToken);

    public Task<bool> ExistsByPublicIdAsync(string publicId, CancellationToken cancellationToken = default)
        => _context.Users.Find(user => user.PublicId == publicId).AnyAsync(cancellationToken);

    public Task<bool> ExistsByReferralCodeAsync(string referralCode, CancellationToken cancellationToken = default)
        => _context.Users.Find(user => user.ReferralCode == referralCode).AnyAsync(cancellationToken);

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
        => _context.Users.InsertOneAsync(user, cancellationToken: cancellationToken);

    public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        => _context.Users.ReplaceOneAsync(
            existing => existing.Id == user.Id, user, cancellationToken: cancellationToken);
}

/// <inheritdoc cref="IEmailConfirmationRepository" />
public sealed class EmailConfirmationRepository : IEmailConfirmationRepository
{
    private readonly MongoContext _context;

    public EmailConfirmationRepository(MongoContext context) => _context = context;

    public Task AddAsync(EmailConfirmation confirmation, CancellationToken cancellationToken = default)
        => _context.EmailConfirmations.InsertOneAsync(confirmation, cancellationToken: cancellationToken);

    public Task<EmailConfirmation?> FindLatestPendingAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.EmailConfirmations
            .Find(confirmation => confirmation.UserId == userId && confirmation.ConfirmedAt == null)
            // Newest first: asking for a fresh code must invalidate the old one
            // in practice, even though both rows stay until they expire.
            .SortByDescending(confirmation => confirmation.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(EmailConfirmation confirmation, CancellationToken cancellationToken = default)
        => _context.EmailConfirmations.ReplaceOneAsync(
            existing => existing.Id == confirmation.Id, confirmation, cancellationToken: cancellationToken);
}

/// <inheritdoc cref="IRefreshTokenRepository" />
public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly MongoContext _context;

    public RefreshTokenRepository(MongoContext context) => _context = context;

    public Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
        => _context.RefreshTokens.InsertOneAsync(token, cancellationToken: cancellationToken);

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => _context.RefreshTokens.Find(token => token.TokenHash == tokenHash)
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(RefreshToken token, CancellationToken cancellationToken = default)
        => _context.RefreshTokens.ReplaceOneAsync(
            existing => existing.Id == token.Id, token, cancellationToken: cancellationToken);

    public Task RevokeAllForUserAsync(
        Guid userId, DateTime revokedAt, CancellationToken cancellationToken = default)
        => _context.RefreshTokens.UpdateManyAsync(
            token => token.UserId == userId && token.RevokedAt == null,
            Builders<RefreshToken>.Update.Set(token => token.RevokedAt, revokedAt),
            cancellationToken: cancellationToken);
}

/// <inheritdoc cref="ILegalDocumentRepository" />
public sealed class LegalDocumentRepository : ILegalDocumentRepository
{
    private readonly MongoContext _context;

    public LegalDocumentRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyDictionary<LegalDocumentType, LegalDocument>> GetCurrentAsync(
        string language, CancellationToken cancellationToken = default)
    {
        var documents = await _context.LegalDocuments
            .Find(document => document.Language == language)
            .SortByDescending(document => document.PublishedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (documents.Count == 0 && language != "az")
        {
            // Fall back to Azerbaijani, the default language: a document that
            // has not been translated yet must still be shown.
            return await GetCurrentAsync("az", cancellationToken).ConfigureAwait(false);
        }

        return documents
            .GroupBy(document => document.DocType)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public Task<LegalDocument?> GetCurrentAsync(
        LegalDocumentType docType, string language, CancellationToken cancellationToken = default)
        => _context.LegalDocuments
            .Find(document => document.DocType == docType && document.Language == language)
            .SortByDescending(document => document.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task AddConsentsAsync(IEnumerable<Consent> consents, CancellationToken cancellationToken = default)
    {
        var batch = consents.ToList();

        return batch.Count == 0
            ? Task.CompletedTask
            : _context.Consents.InsertManyAsync(batch, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyDictionary<LegalDocumentType, string>> GetAcceptedVersionsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var consents = await _context.Consents
            .Find(consent => consent.UserId == userId)
            .SortByDescending(consent => consent.AcceptedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return consents
            .GroupBy(consent => consent.DocType)
            .ToDictionary(group => group.Key, group => group.First().Version);
    }
}
