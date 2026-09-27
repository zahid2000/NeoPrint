using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Behaviors;

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
/// MongoDB can abort a transaction with a transient error when two of them
/// touch the same document, and the documented remedy is to run it again. That
/// retry lives here so no handler has to think about it.
/// </para>
/// </remarks>
public sealed class MongoTransactionRunner : ITransactionRunner
{
    private const int MaxAttempts = 3;

    private readonly MongoContext _context;
    private readonly OutboxContext _outbox;
    private readonly ILogger<MongoTransactionRunner> _logger;

    public MongoTransactionRunner(
        MongoContext context, OutboxContext outbox, ILogger<MongoTransactionRunner> logger)
    {
        _context = context;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 1; ; attempt++)
        {
            await _outbox.BeginTransaction(cancellationToken).ConfigureAwait(false);

            try
            {
                TResult result;

                // Everything the operation touches goes through the outbox's
                // session, which is what makes the commit and the abort mean
                // anything: the driver only enlists an operation when the
                // session is passed to it, and nothing passes it by hand
                // (docs/adr/0004-ambient-mongo-session.md).
                using (_context.Enlist(_outbox.Session
                    ?? throw new InvalidOperationException("The outbox started no session.")))
                {
                    result = await operation().ConfigureAwait(false);
                }

                await _outbox.CommitTransaction(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (MongoException exception)
                when (attempt < MaxAttempts && exception.HasErrorLabel("TransientTransactionError"))
            {
                await SafeAbortAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "A transaction hit a transient conflict on attempt {Attempt}; retrying.", attempt);
            }
            catch
            {
                await SafeAbortAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task SafeAbortAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _outbox.AbortTransaction(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The original failure is the one worth reporting; an abort that
            // also fails must not replace it.
            _logger.LogWarning(exception, "Aborting a transaction failed.");
        }
    }
}
