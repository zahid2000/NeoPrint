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

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegalDocument>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.LegalDocuments
            .Find(FilterDefinition<LegalDocument>.Empty)
            .SortByDescending(document => document.PublishedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task AddAsync(LegalDocument document, CancellationToken cancellationToken = default)
        => _context.LegalDocuments.InsertOneAsync(document, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<long> CountAcceptancesAsync(
        LegalDocumentType docType, string version, CancellationToken cancellationToken = default)
        => _context.Consents.CountDocumentsAsync(
            consent => consent.DocType == docType && consent.Version == version,
            cancellationToken: cancellationToken);

    public Task<LegalDocument?> GetCurrentAsync(
        LegalDocumentType docType, string language, CancellationToken cancellationToken = default)
        => _context.LegalDocuments
            .Find(document => document.DocType == docType && document.Language == language)
            .SortByDescending(document => document.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken)!;

    /// <summary>
    /// Records acceptances, once each.
    /// </summary>
    /// <remarks>
    /// Idempotent: accepting the same document version again keeps the first
    /// acceptance, with its original timestamp, IP and device. A second row
    /// would make it impossible to say when the user actually accepted, and a
    /// retried request must not rewrite consent history
    /// (docs/DATA_RETENTION.md, docs/IDEMPOTENCY.md).
    /// </remarks>
    public async Task AddConsentsAsync(
        IEnumerable<Consent> consents, CancellationToken cancellationToken = default)
    {
        var batch = consents.ToList();

        if (batch.Count == 0)
        {
            return;
        }

        var writes = batch
            .Select(consent => new UpdateOneModel<Consent>(
                Builders<Consent>.Filter.Where(existing =>
                    existing.UserId == consent.UserId &&
                    existing.DocType == consent.DocType &&
                    existing.Version == consent.Version),
                // SetOnInsert throughout: an existing acceptance is left
                // exactly as it was.
                Builders<Consent>.Update
                    .SetOnInsert(existing => existing.Id, consent.Id)
                    .SetOnInsert(existing => existing.UserId, consent.UserId)
                    .SetOnInsert(existing => existing.DocType, consent.DocType)
                    .SetOnInsert(existing => existing.Version, consent.Version)
                    .SetOnInsert(existing => existing.AcceptedAt, consent.AcceptedAt)
                    .SetOnInsert(existing => existing.Ip, consent.Ip)
                    .SetOnInsert(existing => existing.DeviceId, consent.DeviceId)
                    .SetOnInsert(existing => existing.CreatedAt, consent.CreatedAt))
            {
                IsUpsert = true,
            })
            .Cast<WriteModel<Consent>>()
            .ToList();

        try
        {
            await _context.Consents
                .BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<Consent> exception)
            when (exception.WriteErrors.All(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            // Two requests upserting the same acceptance at the same moment:
            // one wins, and the loser's duplicate key error means the row it
            // wanted is already there. Nothing to report.
        }
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
