using System.Diagnostics;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;

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
/// Logs each request with the fields <c>docs/OBSERVABILITY.md</c> asks for:
/// who, on what, how long, and how it ended.
/// </summary>
/// <remarks>
/// The request type name and the outcome are logged; the request contents are
/// not. They carry email addresses, file names and payment details, and plan
/// section 2.13 keeps personal data out of the log. Identifiers - the actor,
/// the kiosk, the device - are safe and are what makes a transaction
/// traceable, so those are logged as a scope that covers the handler and
/// everything it calls.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly ICorrelationContext _correlation;
    private readonly ICurrentUser _currentUser;

    public LoggingBehavior(
        ILogger<LoggingBehavior<TRequest, TResponse>> logger,
        ICorrelationContext correlation,
        ICurrentUser currentUser)
    {
        _logger = logger;
        _correlation = correlation;
        _currentUser = currentUser;
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var operation = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        using var scope = _logger.BeginScope(Fields(operation));

        try
        {
            var response = await next().ConfigureAwait(false);

            _logger.LogInformation(
                "{Operation} {Status} in {DurationMs} ms.", operation, "Succeeded", stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (ValidationException)
        {
            // Expected outcome, not a fault: logged at a lower level, and
            // without the errors themselves, which quote user input.
            _logger.LogInformation(
                "{Operation} {Status} in {DurationMs} ms.", operation, "Rejected", stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "{Operation} {Status} in {DurationMs} ms.", operation, "Cancelled", stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is DomainException or AuthenticationException)
        {
            // A rule said no - a revoked device registering again, a receipt
            // that is already issued: the caller gets a 4xx and nothing is
            // broken. A warning with the message key, not an error with a
            // stack trace, so alerts on errors stay meaningful.
            _logger.LogWarning(
                "{Operation} {Status} in {DurationMs} ms with {ErrorCode}.",
                operation,
                "Rejected",
                stopwatch.ElapsedMilliseconds,
                exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "{Operation} {Status} in {DurationMs} ms with {ErrorCode}.",
                operation,
                "Failed",
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// The identifying fields, omitting the ones that do not apply: an
    /// anonymous kiosk session has no user, and an admin request has no device.
    /// </summary>
    private Dictionary<string, object> Fields(string operation)
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = _correlation.CorrelationId,
            ["Operation"] = operation,
        };

        Add(fields, "UserId", _currentUser.UserId);
        Add(fields, "ActorId", _currentUser.AdminUserId);
        Add(fields, "KioskId", _currentUser.KioskId);
        Add(fields, "DeviceId", _currentUser.DeviceId);

        return fields;
    }

    private static void Add(Dictionary<string, object> fields, string name, Guid? value)
    {
        if (value is not null)
        {
            fields[name] = value.Value;
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
