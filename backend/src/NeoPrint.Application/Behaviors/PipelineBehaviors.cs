using System.Diagnostics;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;

namespace NeoPrint.Application.Behaviors;

/// <summary>
/// Runs every registered validator before a request reaches its handler.
/// </summary>
/// <remarks>
/// Validation happens once, in one place, so a handler never has to ask whether
/// its input was checked.
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators) => _validators = validators;

    public Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var errors = _validators.SelectMany(validator => validator.Validate(request)).ToList();

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return next();
    }
}

/// <summary>
/// Logs each request, how long it took, and whether it failed.
/// </summary>
/// <remarks>
/// Only the request type name and the outcome are logged. Request contents are
/// not: they carry email addresses, file names and payment details, and plan
/// section 2.13 keeps personal data out of the log.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await next().ConfigureAwait(false);
            _logger.LogInformation("{Request} handled in {Elapsed} ms.", name, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (ValidationException)
        {
            // Expected outcome, not a fault: logged at a lower level and
            // without the errors, which name user input.
            _logger.LogInformation("{Request} was rejected by validation after {Elapsed} ms.",
                name, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "{Request} failed after {Elapsed} ms.", name, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}

/// <summary>
/// Marks a command that must run inside a database transaction.
/// </summary>
/// <remarks>
/// Applied to commands that touch more than one document at once - creating a
/// job with its payment, adjusting a wallet, activating a device and its kiosk.
/// A read or a single-document write does not need one.
/// </remarks>
public interface ITransactionalRequest;

/// <summary>
/// Runs <see cref="ITransactionalRequest"/> commands inside a MongoDB
/// transaction.
/// </summary>
/// <remarks>
/// The session is supplied by the infrastructure layer, which owns the driver.
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ITransactionRunner _transactions;

    public TransactionBehavior(ITransactionRunner transactions) => _transactions = transactions;

    public Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => request is ITransactionalRequest
            ? _transactions.RunAsync(() => next(), cancellationToken)
            : next();
}

/// <summary>Runs a delegate inside a database transaction.</summary>
public interface ITransactionRunner
{
    Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken);
}
