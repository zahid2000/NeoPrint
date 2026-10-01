using MassTransit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Behaviors;
using NeoPrint.Infrastructure.Messaging;

using OutboxContext = MassTransit.MongoDbIntegration.MongoDbContext;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Runs a command inside a MongoDB transaction (plan section 10).
/// </summary>
/// <remarks>
/// <para>
/// The transaction is MassTransit's: its <see cref="OutboxContext"/> opens the
/// session and commits it, and that commit is what writes the events the
/// command published to the outbox - in the same transaction as the state
/// change, so an event is stored exactly when the change is. A transaction
/// opened on the side would commit the change and leave the events buffered
/// in memory, never written: which is what happened before this was
/// (docs/DECISIONS.md).
/// </para>
/// <para>
/// Each attempt takes its outbox context and publish endpoint from a scope of
/// its own. Inside a consumer the request's scope holds the message being
/// consumed, and its publish endpoint is that <c>ConsumeContext</c>: an event
/// published there reached the broker before the commit - or after an abort -
/// and a consumer read the state it announced before it existed (T-119;
/// docs/adr/ADR-0006-transaction-outbox-scope.md). The new scope has no
/// message, so its endpoint is always the bus outbox on this transaction.
/// </para>
/// <para>
/// MongoDB can abort a transaction with a transient error when two of them
/// touch the same document, and the documented remedy is to run it again. That
/// retry lives here so no handler has to think about it; an aborted attempt's
/// events go with its scope.
/// </para>
/// </remarks>
public sealed class MongoTransactionRunner : ITransactionRunner
{
    private const int MaxAttempts = 3;

    private readonly MongoContext _context;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MongoTransactionRunner> _logger;

    public MongoTransactionRunner(MongoContext context, IServiceScopeFactory scopes, ILogger<MongoTransactionRunner> logger)
    {
        _context = context;
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 1; ; attempt++)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<OutboxContext>();
            var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            await outbox.BeginTransaction(cancellationToken).ConfigureAwait(false);

            try
            {
                TResult result;

                // Everything the operation touches goes through the outbox's
                // session, which is what makes the commit and the abort mean
                // anything: the driver only enlists an operation when the
                // session is passed to it, and nothing passes it by hand
                // (docs/adr/ADR-0004-ambient-mongo-session.md).
                using (_context.Enlist(outbox.Session
                    ?? throw new InvalidOperationException("The outbox started no session.")))
                using (TransactionOutbox.Use(publish))
                {
                    result = await operation().ConfigureAwait(false);
                }

                await outbox.CommitTransaction(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (MongoException exception)
                when (attempt < MaxAttempts && exception.HasErrorLabel("TransientTransactionError"))
            {
                await SafeAbortAsync(outbox, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "A transaction hit a transient conflict on attempt {Attempt}; retrying.", attempt);
            }
            catch
            {
                await SafeAbortAsync(outbox, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task SafeAbortAsync(OutboxContext outbox, CancellationToken cancellationToken)
    {
        try
        {
            await outbox.AbortTransaction(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The original failure is the one worth reporting; an abort that
            // also fails must not replace it.
            _logger.LogWarning(exception, "Aborting a transaction failed.");
        }
    }
}
