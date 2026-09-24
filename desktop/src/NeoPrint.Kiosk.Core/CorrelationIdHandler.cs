using System.Diagnostics.CodeAnalysis;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// Puts a correlation id on every request the kiosk makes
/// (<c>docs/OBSERVABILITY.md</c>).
/// </summary>
/// <remarks>
/// A print transaction has to be traceable from the kiosk through the backend
/// to the printer and on to the refund. The kiosk is where several of those
/// start, so it sends the id rather than waiting for the backend to invent one:
/// that way a failure on the kiosk screen and the server-side log line carry
/// the same id, and an operator can quote it.
///
/// One id per attempt, not per session: a session lasts as long as somebody
/// stands at the kiosk, and correlating everything they did into one id would
/// defeat the purpose.
/// </remarks>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    /// <summary>The header the backend reads and echoes.</summary>
    public const string HeaderName = "X-Correlation-Id";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Never overwritten: a caller that already set one is correlating this
        // request with work that started earlier.
        if (!request.Headers.Contains(HeaderName))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, NewId());
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// A fresh id, in the same shape the backend generates, so logs from both
    /// sides look alike.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "A correlation id is a log key, not a secret: it authorises nothing.")]
    public static string NewId() => Guid.NewGuid().ToString("n");
}
