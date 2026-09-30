using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Files;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// A flash order paid with the NeoPrint app (plan section 9.2, step 5): the
/// kiosk uploads the file off the drive and it appears in the account's
/// "Fayllarım" with the USB source. Once per job; a retry after a lost
/// answer returns the file already kept.
/// </summary>
public sealed record FlashUploadCommand(Guid DeviceId, Guid JobId, string FileName, Stream Content, long Length) : IRequest<FlashUploadResult>;

/// <summary>Step two of <see cref="FlashUploadCommand"/>: the job remembers the file.</summary>
public sealed record KeepFlashFileCommand(Guid JobId, Guid FileId) : IRequest<Guid>, ITransactionalRequest;

public sealed record FlashUploadResult(Guid JobId, Guid FileId, bool AlreadyKept);

public sealed class FlashUploadHandlers :
    IRequestHandler<FlashUploadCommand, FlashUploadResult>,
    IRequestHandler<KeepFlashFileCommand, Guid>
{
    private readonly ISender _sender;
    private readonly IKioskDeviceRepository _devices;
    private readonly IPrintJobRepository _jobs;
    private readonly ILogger<FlashUploadHandlers> _logger;

    public FlashUploadHandlers(ISender sender, IKioskDeviceRepository devices, IPrintJobRepository jobs, ILogger<FlashUploadHandlers> logger)
    {
        _sender = sender;
        _devices = devices;
        _jobs = jobs;
        _logger = logger;
    }

    /// <inheritdoc cref="FlashUploadCommand" />
    public async Task<FlashUploadResult> Handle(FlashUploadCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (device?.KioskId is not { } kioskId || job is null || job.KioskId != kioskId)
        {
            throw new NotFoundException("printJob");
        }

        if (job.FileId is { } kept)
        {
            return new FlashUploadResult(job.Id, kept, AlreadyKept: true);
        }

        if (job.Source != PrintJobSource.Flash || job.UserId is not { } owner)
        {
            // Only a paid flash order with an account behind it has anywhere to go.
            throw new DomainException("printJobs.flashUploadNotForThis");
        }

        var file = await _sender
            .Send(new UploadFileCommand(owner, request.FileName, request.Content, request.Length, kioskId, FileSource.Usb, IsSensitive: false), cancellationToken)
            .ConfigureAwait(false);
        var fileId = await _sender.Send(new KeepFlashFileCommand(job.Id, file.Id), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Flash file of job {JobId} kept as file {FileId} for its owner.", job.Id, fileId);

        return new FlashUploadResult(job.Id, fileId, AlreadyKept: fileId != file.Id);
    }

    /// <inheritdoc cref="KeepFlashFileCommand" />
    public async Task<Guid> Handle(KeepFlashFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("printJob");

        job.KeepUploadedFile(request.FileId);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        return job.FileId!.Value;
    }
}
