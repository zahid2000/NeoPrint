namespace NeoPrint.Application.Common;

/// <summary>One thing wrong with a request.</summary>
/// <param name="Field">The field it applies to, or an empty string for the request as a whole.</param>
/// <param name="Message">Message shown to the caller.</param>
public sealed record ValidationError(string Field, string Message);

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

    /// <summary>The errors grouped by field, ready for a ProblemDetails payload.</summary>
    public IDictionary<string, string[]> ToDictionary()
        => Errors
            .GroupBy(error => error.Field)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).ToArray(),
                StringComparer.Ordinal);
}
