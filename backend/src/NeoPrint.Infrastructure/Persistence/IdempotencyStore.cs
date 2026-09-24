using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Idempotency;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IIdempotencyStore" />
/// <remarks>
/// The claim is an insert against a unique index, which is what makes it
/// atomic: two requests arriving at the same instant cannot both believe they
/// are the first, because the database refuses the second one.
/// </remarks>
public sealed class MongoIdempotencyStore : IIdempotencyStore
{
    private readonly MongoContext _context;
    private readonly IClock _clock;
    private readonly ILogger<MongoIdempotencyStore> _logger;

    public MongoIdempotencyStore(
        MongoContext context, IClock clock, ILogger<MongoIdempotencyStore> logger)
    {
        _context = context;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim> ClaimAsync(
        string key,
        string actorKey,
        string operation,
        string requestHash,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var record = new IdempotencyKey
        {
            Key = key,
            ActorKey = actorKey,
            Operation = operation,
            RequestHash = requestHash,
            Status = IdempotencyStatus.InProgress,
            CorrelationId = correlationId,
            CreatedAt = now,
            ExpiresAt = now + IdempotencyKey.Lifetime,
        };

        try
        {
            await _context.IdempotencyKeys
                .InsertOneAsync(record, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new IdempotencyClaim(IdempotencyOutcome.Claimed, record.Id);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Somebody used this key first. What happens next depends on
            // whether it was the same request.
            return await InspectExistingAsync(key, actorKey, operation, requestHash, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<IdempotencyClaim> InspectExistingAsync(
        string key, string actorKey, string operation, string requestHash, CancellationToken cancellationToken)
    {
        var existing = await _context.IdempotencyKeys
            .Find(candidate =>
                candidate.Key == key &&
                candidate.ActorKey == actorKey &&
                candidate.Operation == operation)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // It expired between the failed insert and this read. Treat it as
            // in progress rather than guessing: the caller retries.
            return new IdempotencyClaim(IdempotencyOutcome.InProgress);
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            // The same key for different content. Answering with the first
            // response would be answering a question that was not asked.
            _logger.LogWarning(
                "Idempotency key reused for a different request on {Operation}.", operation);

            return new IdempotencyClaim(IdempotencyOutcome.Conflict, existing.Id, existing);
        }

        return existing.HasReplayableResponse
            ? new IdempotencyClaim(IdempotencyOutcome.Replay, existing.Id, existing)
            : new IdempotencyClaim(IdempotencyOutcome.InProgress, existing.Id, existing);
    }

    /// <inheritdoc />
    public Task CompleteAsync(
        Guid recordId,
        int statusCode,
        string? body,
        string? contentType,
        CancellationToken cancellationToken = default)
        => _context.IdempotencyKeys.UpdateOneAsync(
            record => record.Id == recordId,
            Builders<IdempotencyKey>.Update
                .Set(record => record.Status, IdempotencyStatus.Completed)
                .Set(record => record.ResponseStatusCode, statusCode)
                .Set(record => record.ResponseBody, body)
                .Set(record => record.ResponseContentType, contentType),
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task ReleaseAsync(Guid recordId, CancellationToken cancellationToken = default)
        => _context.IdempotencyKeys.DeleteOneAsync(
            record => record.Id == recordId, cancellationToken);
}

/// <inheritdoc cref="IProviderEventStore" />
public sealed class MongoProviderEventStore : IProviderEventStore
{
    private readonly MongoContext _context;
    private readonly IClock _clock;
    private readonly ILogger<MongoProviderEventStore> _logger;

    public MongoProviderEventStore(
        MongoContext context, IClock clock, ILogger<MongoProviderEventStore> logger)
    {
        _context = context;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordAsync(
        string provider,
        string providerEventId,
        string type,
        string payloadHash,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.ProviderEvents
                .InsertOneAsync(
                    new ProviderEvent
                    {
                        Provider = provider,
                        ProviderEventId = providerEventId,
                        Type = type,
                        PayloadHash = payloadHash,
                        CorrelationId = correlationId,
                        ProcessedAt = _clock.UtcNow,
                        CreatedAt = _clock.UtcNow,
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The provider sent it again. Providers resend until they get a
            // 2xx, and sometimes afterwards; acting on it twice is a double
            // refund (docs/IDEMPOTENCY.md).
            _logger.LogInformation(
                "Ignoring a repeated {Type} callback from {Provider}.", type, provider);

            return false;
        }
    }
}
