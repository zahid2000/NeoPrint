using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Payments;
using NeoPrint.Crypto;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Turns the exceptions the application raises into RFC 7807 problem responses
/// (plan section 6).
/// </summary>
/// <remarks>
/// Anything not recognised here becomes a plain 500 with no detail: an
/// unexpected failure must not leak a stack trace or a connection string to a
/// caller. The full exception is logged instead.
/// </remarks>
public sealed class ProblemDetailsExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<ProblemDetailsExceptionHandler> _logger;

    public ProblemDetailsExceptionHandler(
        IProblemDetailsService problemDetails, ILogger<ProblemDetailsExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var problem = Map(exception);
        var operation = $"{httpContext.Request.Method} {httpContext.Request.Path}";

        // The handler itself is a singleton, so the correlation id - which is
        // scoped to one request - is resolved from the request's own services
        // rather than taken in the constructor.
        // The caller gets it too, so a report of "it failed" can be traced to
        // the exact request (docs/OBSERVABILITY.md).
        problem.Extensions["correlationId"] = httpContext.RequestServices
            .GetRequiredService<ICorrelationContext>()
            .CorrelationId;

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "{Operation} {Status} with {ErrorCode}.",
                operation,
                problem.Status,
                exception.GetType().Name);
        }
        else
        {
            _logger.LogInformation(
                "{Operation} {Status} with {ErrorCode}.",
                operation,
                problem.Status,
                exception.GetType().Name);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    /// <summary>
    /// The problem, written in the request's language (Messages.Current). The
    /// message key goes along as <c>code</c>, so a client can react to a specific
    /// rule without parsing text.
    /// </summary>
    private static ProblemDetails Map(Exception exception)
    {
        var problem = exception switch
        {
            ValidationException validation => new ValidationProblemDetails(
                validation.ToDictionary(message => Messages.Write(message)))
            {
                Title = Title("problem.validation"),
                Status = StatusCodes.Status400BadRequest,
            },

            NotFoundException notFound => new ProblemDetails
            {
                Title = Title("problem.notFound"),
                Detail = Messages.Write(notFound.Text),
                Status = StatusCodes.Status404NotFound,
            },

            DomainException domain => new ProblemDetails
            {
                Title = Title("problem.rejected"),
                Detail = Messages.Write(domain.Text),
                Status = StatusCodes.Status409Conflict,
            },

            // Not signed in, or the credential has expired: 401, not 400. A client
            // that gets a 400 has been told its request was malformed and will
            // retry it; a 401 tells it to sign in again.
            AuthenticationException authentication => new ProblemDetails
            {
                Title = Title("problem.notAuthenticated"),
                Detail = Messages.Write(authentication.Text),
                Status = StatusCodes.Status401Unauthorized,
            },

            UnauthorizedAccessException => new ProblemDetails
            {
                Title = Title("problem.forbidden"),
                Status = StatusCodes.Status403Forbidden,
            },

            // Never reveal whether a key is missing or the content was tampered
            // with; both mean the object cannot be served.
            NeoPrintCryptoException => new ProblemDetails
            {
                Title = Title("problem.unreadable"),
                Status = StatusCodes.Status500InternalServerError,
            },

            // The bank or card provider did not answer: nothing was charged, the
            // customer can try again or pay another way.
            PaymentProviderUnavailableException => new ProblemDetails
            {
                Title = Title("problem.providerUnavailable"),
                Detail = Messages.Write(new UserMessage("payments.providerUnavailable")),
                Status = StatusCodes.Status503ServiceUnavailable,
            },

            OperationCanceledException => new ProblemDetails
            {
                Title = Title("problem.cancelled"),
                // 499 Client Closed Request: nginx's code, not in StatusCodes.
                Status = 499,
            },

            _ => new ProblemDetails
            {
                Title = Title("problem.unexpected"),
                Status = StatusCodes.Status500InternalServerError,
            },
        };

        var code = exception switch
        {
            DomainException domain => domain.Text.Key,
            AuthenticationException authentication => authentication.Text.Key,
            PaymentProviderUnavailableException => "payments.providerUnavailable",
            ValidationException { Errors.Count: 1 } validation => validation.Errors.First().Message.Key,
            _ => null,
        };

        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        return problem;
    }

    private static string Title(string key) => Messages.Write(new UserMessage(key));
}

