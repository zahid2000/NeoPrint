using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Common;

/// <summary>One thing wrong with a request.</summary>
/// <param name="Field">The field it applies to, or an empty string for the request as a whole.</param>
/// <param name="Message">What to tell the caller; written out in the request's language by the API.</param>
public sealed record ValidationError(string Field, UserMessage Message)
{
    public ValidationError(string field, string key)
        : this(field, new UserMessage(key))
    {
    }

    public ValidationError(string field, string key, params (string Name, object? Value)[] arguments)
        : this(field, UserMessage.Of(key, arguments))
    {
    }
}

/// <summary>
/// A request failed validation.
/// </summary>
/// <remarks>
/// Surfaces as an RFC 7807 validation problem with status 400 and the errors
/// grouped by field, which is the shape the mobile and admin clients expect.
/// </remarks>
public sealed class ValidationException : Exception
{
    public ValidationException(IReadOnlyCollection<ValidationError> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyCollection<ValidationError> Errors { get; }

    /// <summary>The errors grouped by field and written out, ready for a ProblemDetails payload.</summary>
    public IDictionary<string, string[]> ToDictionary(Func<UserMessage, string> write)
        => Errors
            .GroupBy(error => error.Field)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => write(error.Message)).ToArray(),
                StringComparer.Ordinal);
}
