using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Common;
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
        var problem = Map(exception);

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogInformation("{ExceptionType} on {Method} {Path} -> {Status}.",
                exception.GetType().Name, httpContext.Request.Method, httpContext.Request.Path, problem.Status);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Map(Exception exception) => exception switch
    {
        ValidationException validation => new ValidationProblemDetails(validation.ToDictionary())
        {
            Title = "Validation failed",
            Status = StatusCodes.Status400BadRequest,
        },

        NotFoundException notFound => new ProblemDetails
        {
            Title = "Not found",
            Detail = notFound.Message,
            Status = StatusCodes.Status404NotFound,
        },

        DomainException domain => new ProblemDetails
        {
            Title = "Request rejected",
            Detail = domain.Message,
            Status = StatusCodes.Status409Conflict,
        },

        UnauthorizedAccessException => new ProblemDetails
        {
            Title = "Forbidden",
            Status = StatusCodes.Status403Forbidden,
        },

        // Never reveal whether a key is missing or the content was tampered
        // with; both mean the object cannot be served.
        NeoPrintCryptoException => new ProblemDetails
        {
            Title = "The stored object could not be read",
            Status = StatusCodes.Status500InternalServerError,
        },

        OperationCanceledException => new ProblemDetails
        {
            Title = "Request cancelled",
            // 499 Client Closed Request: nginx's code, not in StatusCodes.
            Status = 499,
        },

        _ => new ProblemDetails
        {
            Title = "An unexpected error occurred",
            Status = StatusCodes.Status500InternalServerError,
        },
    };
}

