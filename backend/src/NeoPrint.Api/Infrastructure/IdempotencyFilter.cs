using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Marks an endpoint that must not act twice on the same request
/// (<c>docs/IDEMPOTENCY.md</c>).
/// </summary>
/// <remarks>
/// Opt-in rather than applied to every write: a key only means something where
/// repeating the operation would do real damage - taking a payment, issuing a
/// refund, activating a device. On the rest, demanding a key would be
/// ceremony.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IdempotentAttribute : ServiceFilterAttribute
{
    public IdempotentAttribute()
        : base(typeof(IdempotencyFilter))
    {
    }
}

/// <summary>
/// Claims the caller's <c>Idempotency-Key</c>, replays the first response to a
/// retry, and refuses the key if it is reused for something else.
/// </summary>
/// <remarks>
/// Runs as a filter rather than as middleware so it sees the action and the
/// model-bound body, which is what makes the request hash meaningful and lets
/// the key be scoped per operation.
/// </remarks>
public sealed class IdempotencyFilter : IAsyncActionFilter
{
    /// <summary>The header the caller sends.</summary>
    public const string HeaderName = "Idempotency-Key";

    private const int MaxKeyLength = 128;

    private readonly IIdempotencyStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly ICorrelationContext _correlation;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<IdempotencyFilter> _logger;

    public IdempotencyFilter(
        IIdempotencyStore store,
        ICurrentUser currentUser,
        ICorrelationContext correlation,
        IOptions<JsonOptions> jsonOptions,
        ILogger<IdempotencyFilter> logger)
    {
        ArgumentNullException.ThrowIfNull(jsonOptions);

        _store = store;
        _currentUser = currentUser;
        _correlation = correlation;
        // The same options the pipeline serializes with, so a replayed
        // response is byte for byte what the first one was. Serializing with
        // the defaults instead would give the caller PascalCase where it had
        // camelCase the first time.
        _json = jsonOptions.Value.JsonSerializerOptions;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var key = ReadKey(context.HttpContext);

        if (key is null)
        {
            // No key, no protection. Refusing outright would break every client
            // that has not been updated yet; the endpoints where a key is
            // mandatory say so in their own validation.
            await next().ConfigureAwait(false);
            return;
        }

        var operation = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

        var claim = await _store
            .ClaimAsync(key, ActorKey(), operation, HashArguments(context), _correlation.CorrelationId)
            .ConfigureAwait(false);

        switch (claim.Outcome)
        {
            case IdempotencyOutcome.Replay:
                context.Result = Replay(claim.Existing!);
                return;

            case IdempotencyOutcome.InProgress:
                // The first request has not answered yet. There is nothing to
                // replay, and running the action again would be the duplicate
                // this exists to prevent.
                context.Result = Problem(
                    StatusCodes.Status409Conflict,
                    "Request in progress",
                    "Eyni açarla göndərilmiş sorğu hələ icra olunur. Bir azdan yenidən yoxlayın.");
                return;

            case IdempotencyOutcome.Conflict:
                context.Result = Problem(
                    StatusCodes.Status409Conflict,
                    "Idempotency key reused",
                    "Bu Idempotency-Key başqa məzmunlu sorğu üçün istifadə olunub.");
                return;

            default:
                break;
        }

        var executed = await next().ConfigureAwait(false);

        await RecordOutcomeAsync(claim.RecordId, executed).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a successful response to replay, and releases the key otherwise.
    /// </summary>
    /// <remarks>
    /// A failed request is one worth genuinely retrying, so it must not leave
    /// the caller locked out by the record of the attempt that failed.
    /// </remarks>
    private async Task RecordOutcomeAsync(Guid recordId, ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            await _store.ReleaseAsync(recordId).ConfigureAwait(false);
            return;
        }

        var statusCode = executed.HttpContext.Response.StatusCode;

        if (executed.Result is ObjectResult { StatusCode: { } resultStatus })
        {
            statusCode = resultStatus;
        }
        else if (executed.Result is StatusCodeResult statusResult)
        {
            statusCode = statusResult.StatusCode;
        }

        if (statusCode is < 200 or >= 300)
        {
            await _store.ReleaseAsync(recordId).ConfigureAwait(false);
            return;
        }

        var (body, contentType) = Serialize(executed.Result);

        await _store.CompleteAsync(recordId, statusCode, body, contentType).ConfigureAwait(false);
    }

    /// <summary>Reads and checks the key the caller sent.</summary>
    /// <exception cref="DomainException">The key is not usable.</exception>
    private static string? ReadKey(HttpContext httpContext)
    {
        var value = httpContext.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = value.Trim();

        if (key.Length > MaxKeyLength)
        {
            throw new DomainException($"Idempotency-Key {MaxKeyLength} simvoldan uzun ola bilməz.");
        }

        return key;
    }

    /// <summary>
    /// Who the key belongs to.
    /// </summary>
    /// <remarks>
    /// Part of what makes a key unique, so one caller's key can never replay
    /// another's response. An anonymous kiosk customer has no identity of their
    /// own, so the kiosk's does: that still keeps two kiosks apart.
    /// </remarks>
    private string ActorKey()
    {
        if (_currentUser.UserId is { } userId)
        {
            return $"user:{userId}";
        }

        if (_currentUser.AdminUserId is { } adminId)
        {
            return $"admin:{adminId}";
        }

        if (_currentUser.DeviceId is { } deviceId)
        {
            return $"device:{deviceId}";
        }

        return "anonymous";
    }

    /// <summary>
    /// Hashes the action's arguments, which is how "the same request again" is
    /// told from "a different request reusing a key".
    /// </summary>
    /// <remarks>
    /// The arguments themselves are never stored: they carry payment details
    /// and personal data (plan section 2.13).
    ///
    /// The cancellation token is skipped. It is not request data - it is
    /// plumbing ASP.NET Core binds alongside the real arguments - and it holds
    /// a wait handle, which cannot be serialized at all.
    /// </remarks>
    private string HashArguments(ActionExecutingContext context)
    {
        var builder = new StringBuilder();

        foreach (var (name, value) in context.ActionArguments
                     .Where(argument => argument.Value is not CancellationToken)
                     .OrderBy(argument => argument.Key, StringComparer.Ordinal))
        {
            builder.Append(name).Append('=');
            builder.Append(value is null ? "null" : JsonSerializer.Serialize(value, _json));
            builder.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private (string? Body, string? ContentType) Serialize(IActionResult? result)
        => result switch
        {
            ObjectResult { Value: not null } objectResult =>
                (JsonSerializer.Serialize(objectResult.Value, _json), "application/json"),

            // A 204 and the like have nothing to replay but their status.
            _ => (null, null),
        };

    private IActionResult Replay(Domain.Idempotency.IdempotencyKey existing)
    {
        _logger.LogInformation(
            "Replaying the stored response for a repeated {Operation}.", existing.Operation);

        if (existing.ResponseBody is null)
        {
            return new StatusCodeResult(existing.ResponseStatusCode!.Value);
        }

        return new ContentResult
        {
            StatusCode = existing.ResponseStatusCode,
            Content = existing.ResponseBody,
            ContentType = existing.ResponseContentType ?? "application/json",
        };
    }

    private IActionResult Problem(int status, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
        };

        problem.Extensions["correlationId"] = _correlation.CorrelationId;

        return new ObjectResult(problem) { StatusCode = status };
    }
}
