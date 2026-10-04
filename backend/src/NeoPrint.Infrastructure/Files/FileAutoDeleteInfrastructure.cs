using MassTransit;

using MediatR;

using NeoPrint.Application.Files;
using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Files;

/// <summary>
/// Every five minutes (plan section 6, <c>FileAutoDeleteJob</c>): files whose own
/// rule says they should be gone - a number of days after the upload, or after a
/// print that was held back by another order or a scheduled print.
/// </summary>
/// <remarks>Safe to run twice: a deleted file is no longer listed, and erasing is retry-safe.</remarks>
public sealed class FileAutoDeleteJob
{
    public const string RecurringId = "file-auto-delete";

    private readonly ISender _sender;

    public FileAutoDeleteJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new RunFileAutoDeleteCommand(), cancellationToken);
}

/// <summary>
/// An order printed (plan section 6, <c>FileAutoDeleteJob</c> "+ PrintJobCompleted"):
/// its file notes the print and, set to go after a print, goes now.
/// </summary>
/// <remarks>A redelivered message finds the file already gone; a lost one is caught by the five-minute run.</remarks>
public sealed class FilePrintedConsumer : IConsumer<PrintJobCompleted>
{
    private readonly ISender _sender;

    public FilePrintedConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<PrintJobCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new NoteFilePrintedCommand(context.Message.JobId), context.CancellationToken);
    }
}
