using System.Linq.Expressions;

using MongoDB.Driver;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// A MongoDB collection that joins the transaction the request is running in.
/// </summary>
/// <remarks>
/// <para>
/// The driver only enlists an operation in a transaction when the session is
/// passed to it. A repository that calls the collection directly writes outside
/// the transaction, so an abort leaves its write behind - which is how a failed
/// device activation used to leave the device bound to a kiosk that never heard
/// about it.
/// </para>
/// <para>
/// So the collections are only reachable through this type, which takes the
/// session from <see cref="MongoContext.Session"/> and passes it on. Forgetting
/// is not a mistake that can be made any more: there is nothing to forget, and
/// the driver collection itself is internal to this assembly and used only for
/// index work, which a transaction must not include
/// (<c>docs/adr/ADR-0004-ambient-mongo-session.md</c>).
/// </para>
/// </remarks>
public readonly struct MongoSet<TDocument> : IEquatable<MongoSet<TDocument>>
{
    private readonly MongoContext _context;
    private readonly IMongoCollection<TDocument> _collection;

    internal MongoSet(MongoContext context, IMongoCollection<TDocument> collection)
    {
        _context = context;
        _collection = collection;
    }

    /// <summary>
    /// The driver collection, for the operations a session must not join.
    /// </summary>
    /// <remarks>
    /// Index creation only. MongoDB refuses most index commands inside a
    /// transaction, and indexes are applied at startup, where there is none.
    ///
    /// Asking for it while a transaction is running is refused rather than
    /// served: that is the one way a write could still escape the transaction,
    /// and a write that escapes survives an abort. Failing loudly on the first
    /// request beats losing half a command in production.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A transaction is running.</exception>
    internal IMongoCollection<TDocument> WithoutSession
        => _context.Session is null
            ? _collection
            : throw new InvalidOperationException(
                $"The '{_collection.CollectionNamespace.CollectionName}' collection was asked for " +
                "without the session while a transaction is running. An operation that goes round " +
                "the session is not part of the transaction and survives an abort, so it is refused " +
                "(docs/adr/ADR-0004-ambient-mongo-session.md).");

    private IClientSessionHandle? Session => _context.Session;

    // -----------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------
    //
    // Reads join the transaction as well: a command that reads a document,
    // decides on it and writes it has to see its own earlier writes, and to be
    // isolated from everybody else's.

    public IFindFluent<TDocument, TDocument> Find(Expression<Func<TDocument, bool>> filter)
        => Session is { } session
            ? _collection.Find(session, filter)
            : _collection.Find(filter);

    public IFindFluent<TDocument, TDocument> Find(FilterDefinition<TDocument> filter)
        => Session is { } session
            ? _collection.Find(session, filter)
            : _collection.Find(filter);

    public Task<long> CountDocumentsAsync(
        Expression<Func<TDocument, bool>> filter, CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken)
            : _collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

    public Task<long> CountDocumentsAsync(
        FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken)
            : _collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

    // -----------------------------------------------------------------------
    // Writes
    // -----------------------------------------------------------------------

    public Task InsertOneAsync(TDocument document, CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.InsertOneAsync(session, document, cancellationToken: cancellationToken)
            : _collection.InsertOneAsync(document, cancellationToken: cancellationToken);

    public Task InsertManyAsync(
        IEnumerable<TDocument> documents, CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.InsertManyAsync(session, documents, cancellationToken: cancellationToken)
            : _collection.InsertManyAsync(documents, cancellationToken: cancellationToken);

    public Task<ReplaceOneResult> ReplaceOneAsync(
        Expression<Func<TDocument, bool>> filter,
        TDocument replacement,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.ReplaceOneAsync(session, filter, replacement, cancellationToken: cancellationToken)
            : _collection.ReplaceOneAsync(filter, replacement, cancellationToken: cancellationToken);

    public Task<UpdateResult> UpdateOneAsync(
        Expression<Func<TDocument, bool>> filter,
        UpdateDefinition<TDocument> update,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.UpdateOneAsync(session, filter, update, cancellationToken: cancellationToken)
            : _collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

    public Task<UpdateResult> UpdateOneAsync(
        FilterDefinition<TDocument> filter,
        UpdateDefinition<TDocument> update,
        UpdateOptions? options = null,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.UpdateOneAsync(session, filter, update, options, cancellationToken)
            : _collection.UpdateOneAsync(filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateManyAsync(
        Expression<Func<TDocument, bool>> filter,
        UpdateDefinition<TDocument> update,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.UpdateManyAsync(session, filter, update, cancellationToken: cancellationToken)
            : _collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);

    /// <summary>
    /// Updates every match, by a filter built rather than written as a lambda -
    /// needed where the field is a nested path such as <c>Original.ProviderId</c>
    /// or the update uses array filters.
    /// </summary>
    public Task<UpdateResult> UpdateManyAsync(
        FilterDefinition<TDocument> filter,
        UpdateDefinition<TDocument> update,
        UpdateOptions? options = null,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.UpdateManyAsync(session, filter, update, options, cancellationToken)
            : _collection.UpdateManyAsync(filter, update, options, cancellationToken);

    /// <summary>
    /// Updates one document and returns it, atomically.
    /// </summary>
    /// <remarks>
    /// What a lease needs: claiming a document and reading what was claimed
    /// must be one operation, or two workers could each see the lease free.
    /// </remarks>
    public async Task<TDocument?> FindOneAndUpdateAsync(
        FilterDefinition<TDocument> filter,
        UpdateDefinition<TDocument> update,
        FindOneAndUpdateOptions<TDocument, TDocument>? options = null,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? await _collection.FindOneAndUpdateAsync(session, filter, update, options, cancellationToken).ConfigureAwait(false)
            : await _collection.FindOneAndUpdateAsync(filter, update, options, cancellationToken).ConfigureAwait(false);

    public Task<DeleteResult> DeleteOneAsync(
        Expression<Func<TDocument, bool>> filter, CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.DeleteOneAsync(session, filter, cancellationToken: cancellationToken)
            : _collection.DeleteOneAsync(filter, cancellationToken: cancellationToken);

    public Task<DeleteResult> DeleteManyAsync(
        Expression<Func<TDocument, bool>> filter, CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.DeleteManyAsync(session, filter, cancellationToken: cancellationToken)
            : _collection.DeleteManyAsync(filter, cancellationToken: cancellationToken);

    public Task<BulkWriteResult<TDocument>> BulkWriteAsync(
        IEnumerable<WriteModel<TDocument>> requests,
        BulkWriteOptions? options = null,
        CancellationToken cancellationToken = default)
        => Session is { } session
            ? _collection.BulkWriteAsync(session, requests, options, cancellationToken)
            : _collection.BulkWriteAsync(requests, options, cancellationToken);

    // -----------------------------------------------------------------------
    // A struct wrapping two references; equality is here because the analyzer
    // asks for it, and it means what it says.
    // -----------------------------------------------------------------------

    public bool Equals(MongoSet<TDocument> other)
        => ReferenceEquals(_context, other._context) && ReferenceEquals(_collection, other._collection);

    public override bool Equals(object? obj) => obj is MongoSet<TDocument> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_context, _collection);

    public static bool operator ==(MongoSet<TDocument> left, MongoSet<TDocument> right) => left.Equals(right);

    public static bool operator !=(MongoSet<TDocument> left, MongoSet<TDocument> right) => !left.Equals(right);
}
