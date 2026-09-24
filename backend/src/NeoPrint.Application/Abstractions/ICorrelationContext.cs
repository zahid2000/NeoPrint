namespace NeoPrint.Application.Abstractions;

/// <summary>
/// The correlation id of the work in hand (<c>docs/OBSERVABILITY.md</c>).
/// </summary>
/// <remarks>
/// One print transaction has to be traceable from the API through the queue and
/// the kiosk to the printer, and on to the refund and the fiscal receipt. That
/// only works if every step carries the same id, so the id is set once at the
/// edge - by the HTTP middleware, by a consumer for a message, or by a
/// background job for its run - and read from here everywhere else.
/// </remarks>
public interface ICorrelationContext
{
    /// <summary>
    /// The current correlation id. Never empty: a step that arrives without one
    /// gets a fresh id rather than logging nothing.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    /// Sets the id for work that did not come in over HTTP - a consumer
    /// handling a message, or a background job starting a run.
    /// </summary>
    void Set(string correlationId);
}

/// <summary>Where a correlation id travels.</summary>
public static class CorrelationHeaders
{
    /// <summary>
    /// The header the API accepts and echoes. Chosen over <c>traceparent</c>,
    /// which belongs to W3C trace context and is left to OpenTelemetry in
    /// step 8.
    /// </summary>
    public const string CorrelationId = "X-Correlation-Id";

    /// <summary>The message header used on the bus, so a consumer inherits it.</summary>
    public const string MessageCorrelationId = "neoprint-correlation-id";
}
