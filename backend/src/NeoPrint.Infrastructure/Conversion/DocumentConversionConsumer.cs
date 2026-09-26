using MassTransit;

using Microsoft.Extensions.Logging;

using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// Converts an uploaded file to PDF, off the request thread
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// <para>
/// The work itself is in <see cref="DocumentConversionService"/>; this is the
/// message plumbing around it. Keeping them apart is what lets the conversion
/// be driven by a recovery job as well as by a message, without either of
/// them knowing about the other.
/// </para>
/// <para>
/// Failures are recorded on the file rather than thrown: a document that
/// cannot be converted is a normal outcome - a corrupt upload, a password on
/// a PDF - and retrying it five times and then parking it in the error queue
/// would help nobody. Only an unexpected failure propagates, and MassTransit's
/// retry policy handles that.
/// </para>
/// </remarks>
public sealed class DocumentConversionConsumer : IConsumer<FileUploaded>
{
    private readonly DocumentConversionService _conversion;
    private readonly ILogger<DocumentConversionConsumer> _logger;

    public DocumentConversionConsumer(
        DocumentConversionService conversion, ILogger<DocumentConversionConsumer> logger)
    {
        _conversion = conversion;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<FileUploaded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["FileId"] = context.Message.FileId,
            ["CorrelationId"] = context.Message.CorrelationId,
        });

        // Idempotent in the service: a file already Ready is left alone, so a
        // redelivery does not convert it twice (docs/IDEMPOTENCY.md).
        await _conversion.ConvertAsync(context.Message.FileId, context.CancellationToken)
            .ConfigureAwait(false);
    }
}
