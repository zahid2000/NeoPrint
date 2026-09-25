using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Behaviors;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Runs a command inside a MongoDB transaction (plan section 10).
/// </summary>
/// <remarks>
/// MongoDB can abort a transaction with a transient error when two of them
/// touch the same document, and the documented remedy is to run it again. That
/// retry lives here so no handler has to think about it.
/// </remarks>
public sealed class MongoTransactionRunner : ITransactionRunner
{
    private const int MaxAttempts = 3;

    private readonly MongoContext _context;
    private readonly ILogger<MongoTransactionRunner> _logger;

    public MongoTransactionRunner(MongoContext context, ILogger<MongoTransactionRunner> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 1; ; attempt++)
        {
            using var session = await _context.Client
                .StartSessionAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            session.StartTransaction();

            // Everything the operation touches now goes through this session,
            // which is what makes the commit and the abort mean anything: the
            // driver only enlists an operation when the session is passed to
            // it, and nothing passes it by hand
            // (docs/adr/0004-ambient-mongo-session.md).
            using var enlistment = _context.Enlist(session);

            try
            {
                var result = await operation().ConfigureAwait(false);
                await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (MongoException exception)
                when (attempt < MaxAttempts && exception.HasErrorLabel("TransientTransactionError"))
            {
                await SafeAbortAsync(session, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "A transaction hit a transient conflict on attempt {Attempt}; retrying.", attempt);
            }
            catch
            {
                await SafeAbortAsync(session, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task SafeAbortAsync(IClientSessionHandle session, CancellationToken cancellationToken)
    {
        try
        {
            if (session.IsInTransaction)
            {
                await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The original failure is the one worth reporting; an abort that
            // also fails must not replace it.
            _logger.LogWarning(exception, "Aborting a transaction failed.");
        }
    }
}
