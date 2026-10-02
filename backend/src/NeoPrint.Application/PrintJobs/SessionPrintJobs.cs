using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Application.Payments;
using NeoPrint.Application.Sessions;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// An order for one of the user's files at the kiosk of their session (plan
/// section 9.3, step 2: <c>Source=MobileSession</c>). It waits for payment,
/// which the app makes through <c>payments/{id}/pay-with-app</c> - the same
/// wallet, card, bonus and promo rules as paying a kiosk order with the app.
/// </summary>
public sealed record CreateSessionPrintJobCommand(Guid UserId, Guid SessionId, Guid FileId, PrintOptions Options)
    : IRequest<SessionPrintJobResponse>;

/// <param name="PaymentId">What the app pays it with.</param>
/// <param name="ExpiresAt">Unpaid by then, the order lapses (<c>Payment.TimeoutMinutes</c>).</param>
public sealed record SessionPrintJobResponse(
    Guid JobId, string JobCode, Guid PaymentId, decimal Amount, string Currency, DateTime ExpiresAt, int Pages, int Sheets);

public sealed class SessionPrintJobHandlers : IRequestHandler<CreateSessionPrintJobCommand, SessionPrintJobResponse>
{
    private readonly IKioskSessionAccess _sessions;
    private readonly IKioskRepository _kiosks;
    private readonly IFileRepository _files;
    private readonly IPrintJobRepository _jobs;
    private readonly IPaymentRepository _payments;
    private readonly PrintOrderRules _rules;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<SessionPrintJobHandlers> _logger;

    public SessionPrintJobHandlers(
        IKioskSessionAccess sessions,
        IKioskRepository kiosks,
        IFileRepository files,
        IPrintJobRepository jobs,
        IPaymentRepository payments,
        PrintOrderRules rules,
        ISettingsReader settings,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<SessionPrintJobHandlers> logger)
    {
        _sessions = sessions;
        _kiosks = kiosks;
        _files = files;
        _jobs = jobs;
        _payments = payments;
        _rules = rules;
        _settings = settings;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SessionPrintJobResponse> Handle(CreateSessionPrintJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.UseAsync(request.UserId, request.SessionId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(session.KioskId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("kiosk");

        if (kiosk.Status != KioskStatus.Online)
        {
            throw new DomainException("sessions.kioskNotTakingOrders");
        }

        var file = await _files.FindByIdAsync(request.FileId, cancellationToken).ConfigureAwait(false);

        // Another user's file reads as no file.
        if (file is null || file.UserId != request.UserId || file.DeletedAt is not null)
        {
            throw new NotFoundException("file");
        }

        if (file is not { Status: FileStatus.Ready, Pdf: { } pdf })
        {
            throw new DomainException(UserMessage.Of("files.notReadyNamed", ("name", file.FileName)));
        }

        var now = _clock.UtcNow;
        var price = await _rules.CheckAndPriceAsync(kiosk, file.PageCount, request.Options, now, cancellationToken).ConfigureAwait(false);
        var actor = ActorContext.User(request.UserId, correlationId: _correlation.CorrelationId);

        var job = await _rules
            .AddWithUniqueCodeAsync(
                code => PrintJob.Create(
                    code,
                    kiosk.Id,
                    PrintJobSource.MobileSession,
                    file.PageCount,
                    request.Options,
                    price,
                    now,
                    userId: request.UserId,
                    fileId: file.Id,
                    localFileName: file.FileName,
                    printFile: pdf),
                cancellationToken)
            .ConfigureAwait(false);

        job.AwaitPayment(actor, now);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        var minutes = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.PaymentTimeoutMinutes, 5, cancellationToken).ConfigureAwait(false), 1, 60);

        // The app's own payment, already the user's: what pay-with-app takes.
        var payment = Payment.Open(job.Id, kiosk.Id, PaymentMethod.MobileApp, PaymentChannel.Mobile, job.Price.Total, now, now.AddMinutes(minutes));
        payment.ShowAppQr(now);
        payment.AssignUser(request.UserId, now);
        await _payments.AddAsync(payment, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Session order {JobId} placed at kiosk {KioskId} for file {FileId}: {Sheets} sheets, payment {PaymentId}.",
            job.Id,
            kiosk.Id,
            file.Id,
            job.RequiredSheets,
            payment.Id);

        return new SessionPrintJobResponse(
            job.Id,
            job.JobCode,
            payment.Id,
            payment.Amount.Amount,
            payment.Amount.Currency,
            payment.ExpiresAt,
            job.Price.Price.Pages,
            job.RequiredSheets);
    }
}

/// <summary>
/// The PDF of a paid order made from the app, for the kiosk that prints it
/// (plan section 7.4: "Mobil job: backend → şifrəli spool → printer").
/// </summary>
/// <remarks>
/// Only the kiosk the order is at, and only once it is paid and not finished:
/// before that it is nobody's to print, after it there is nothing to print.
/// The object is the one the order was priced on (<see cref="PrintJob.PrintFile"/>).
/// </remarks>
public sealed record GetKioskPrintFileQuery(Guid DeviceId, Guid JobId) : IRequest<KioskPrintFile>;

/// <param name="Content">Decrypted; the caller disposes of it.</param>
public sealed record KioskPrintFile(Stream Content, long Length, string FileName);

public sealed class KioskPrintFileHandler : IRequestHandler<GetKioskPrintFileQuery, KioskPrintFile>
{
    private static readonly PrintJobStatus[] Printable = [PrintJobStatus.Paid, PrintJobStatus.Queued, PrintJobStatus.Printing];

    private readonly IKioskDeviceRepository _devices;
    private readonly IPrintJobRepository _jobs;
    private readonly IFileStorage _storage;
    private readonly ILogger<KioskPrintFileHandler> _logger;

    public KioskPrintFileHandler(
        IKioskDeviceRepository devices, IPrintJobRepository jobs, IFileStorage storage, ILogger<KioskPrintFileHandler> logger)
    {
        _devices = devices;
        _jobs = jobs;
        _storage = storage;
        _logger = logger;
    }

    public async Task<KioskPrintFile> Handle(GetKioskPrintFileQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        // Another kiosk's order reads as no order.
        if (device?.KioskId is not { } kioskId || job is null || job.KioskId != kioskId || job.PrintFile is not { } print)
        {
            throw new NotFoundException("printJob");
        }

        if (!Printable.Contains(job.Status))
        {
            throw new DomainException("printJobs.fileNotPrintable");
        }

        var stored = new StoredObject(print.ProviderId, print.Key, print.Sha256, print.EncryptedLength, print.PlaintextLength, print.KekId);
        var content = await _storage.OpenReadAsync(stored, StorageBucket.Files, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Kiosk {KioskId} fetched the print file of job {JobId}.", kioskId, job.Id);

        return new KioskPrintFile(content, print.PlaintextLength, $"{job.JobCode}.pdf");
    }
}
