using MongoDB.Driver;

using NeoPrint.Domain.Common;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Saves an aggregate: checks the row version, bumps it, and writes whatever
/// state transitions the domain recorded.
/// </summary>
/// <remarks>
/// One place, so neither half can be forgotten. Writing the transitions here
/// rather than in the handlers is what makes
/// <c>docs/STATE_MACHINES.md</c>'s "every transition is recorded" true by
/// construction: a handler that calls a transition method and saves gets the
/// record whether or not it thought about it.
///
/// The transition rows and the aggregate are two writes. Where the caller runs
/// inside a transaction - device activation does - they commit together; where
/// it does not, a crash between them loses the record but not the state change,
/// which is the safer way round.
/// </remarks>
public static class AggregateWriter
{
    /// <summary>
    /// Replaces the document if nobody else has written it since it was read.
    /// </summary>
    /// <exception cref="ConcurrencyException">Somebody else wrote it first.</exception>
    public static async Task ReplaceAsync<TEntity>(
        MongoContext context,
        IMongoCollection<TEntity> collection,
        TEntity entity,
        CancellationToken cancellationToken)
        where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(entity);

        var expected = entity.RowVersion;
        entity.RowVersion = expected + 1;

        var result = await collection
            .ReplaceOneAsync(
                existing => existing.Id == entity.Id && existing.RowVersion == expected,
                entity,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.MatchedCount == 0)
        {
            // Put the entity back the way it was, so a caller that catches this
            // and retries is not working from a version that was never written.
            entity.RowVersion = expected;

            throw new ConcurrencyException(typeof(TEntity).Name, entity.Id);
        }

        await FlushTransitionsAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts a new document and writes any transitions it recorded.</summary>
    public static async Task InsertAsync<TEntity>(
        MongoContext context,
        IMongoCollection<TEntity> collection,
        TEntity entity,
        CancellationToken cancellationToken)
        where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(entity);

        await collection.InsertOneAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);
        await FlushTransitionsAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    private static Task FlushTransitionsAsync(
        MongoContext context, Entity entity, CancellationToken cancellationToken)
    {
        var transitions = entity.DrainTransitions();

        return transitions.Count == 0
            ? Task.CompletedTask
            : context.StateTransitions.InsertManyAsync(
                transitions, cancellationToken: cancellationToken);
    }
}
