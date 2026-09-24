using NeoPrint.Application.Abstractions;

namespace NeoPrint.Infrastructure.Observability;

/// <summary>
/// Holds the correlation id for the current unit of work
/// (<c>docs/OBSERVABILITY.md</c>).
/// </summary>
/// <remarks>
/// Registered scoped, so an HTTP request, a consumed message and a background
/// job run each get their own. An id is generated on first read when nothing
/// set one, which keeps a log line correlated to something rather than to
/// nothing.
/// </remarks>
public sealed class CorrelationContext : ICorrelationContext
{
    private string? _correlationId;

    /// <inheritdoc />
    public string CorrelationId => _correlationId ??= NewId();

    /// <inheritdoc />
    public void Set(string correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            _correlationId = Sanitize(correlationId);
        }
    }

    /// <summary>A fresh id, in the same shape as the ones we accept.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// Trims an incoming id to something safe to log and to echo.
    /// </summary>
    /// <remarks>
    /// The value arrives in a request header, so it is caller-controlled. It
    /// ends up in log lines and in a response header, which is exactly where a
    /// newline or a control character could forge a log entry or split a
    /// response, so anything but plain identifier characters is dropped and an
    /// unusable value is replaced outright.
    /// </remarks>
    public static string Sanitize(string value)
    {
        const int maxLength = 128;

        var safe = new string([.. value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            .Take(maxLength)]);

        return safe.Length == 0 ? NewId() : safe;
    }
}
