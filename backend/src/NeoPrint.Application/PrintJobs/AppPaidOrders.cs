using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// An order at a chosen kiosk, paid in the app and held for its owner until
/// later: a paid preparation (plan section 9.4, <c>docs/DECISIONS.md</c>
/// T-144) or a scheduled print (plan section 9.5, T-145).
/// </summary>
/// <remarks>
/// Priced by the kiosk's current profile. <c>expectedAmount</c> is the price
/// the app showed; when the profile changed since, the order is refused with
/// <c>printJobs.priceChanged</c> and the app asks again with the new one.
/// Paper is not checked or set aside: it is counted when the order is printed.
/// </remarks>
public sealed class AppPaidOrders
{
    /// <summary>Kiosks a customer may choose (the public kiosk search's rule).</summary>
    private static readonly KioskStatus[] Choosable =
        [KioskStatus.Online, KioskStatus.Paused, KioskStatus.Offline, KioskStatus.Maintenance];

    private readonly IFileRepository _files;
    private readonly IKioskRepository _kiosks;
    private readonly IPrintJobRepository _jobs;
    private readonly IPaymentRepository _payments;
    private readonly PrintOrderRules _rules;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<AppPaidOrders> _logger;

    public AppPaidOrders(
        IFileRepository files,
        IKioskRepository kiosks,
        IPrintJobRepository jobs,
        IPaymentRepository payments,
        PrintOrderRules rules,
        ISettingsReader settings,
        ICorrelationContext correlation,
        ILogger<AppPaidOrders> logger)
    {
        _files = files;
        _kiosks = kiosks;
        _jobs = jobs;
        _payments = payments;
        _rules = rules;
        _settings = settings;
        _correlation = correlation;
        _logger = logger;
    }

    /// <summary>A kiosk by id or code that customers may choose; any other reads as missing.</summary>
    public async Task<Kiosk> ChoosableKioskAsync(string idOrCode, CancellationToken cancellationToken)
    {
        var text = idOrCode.Trim();
        var kiosk = Guid.TryParse(text, out var id)
            ? await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : await _kiosks.FindByCodeAsync(text.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);

        return kiosk is not null && Choosable.Contains(kiosk.Status) ? kiosk : throw new NotFoundException("kiosk");
    }

    /// <summary>The user's own file, ready to print; another user's reads as missing.</summary>
    public async Task<StoredFile> ReadyFileAsync(Guid userId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await _files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false);

        if (file is null || file.UserId != userId || file.DeletedAt is not null)
        {
            throw new NotFoundException("file");
        }

        return file is { Status: FileStatus.Ready, Pdf: not null }
            ? file
            : throw new DomainException(UserMessage.Of("files.notReadyNamed", ("name", file.FileName)));
    }

    /// <summary>Makes the held order and its open payment, to be paid with <c>payments/{id}/pay-with-app</c>.</summary>
    /// <exception cref="DomainException">
    /// The kiosk cannot print these options, or its price is no longer <paramref name="expectedAmount"/>.
    /// </exception>
    public async Task<(PrintJob Job, Payment Payment)> PlaceAsync(
        Guid userId,
        Kiosk kiosk,
        StoredFile file,
        PrintOptions options,
        decimal expectedAmount,
        PrintJobSource source,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(file);

        // Paper is counted when it is printed: that may be days away.
        var price = await _rules
            .CheckAndPriceAsync(kiosk, file.PageCount, options, now, cancellationToken, checkPaper: false)
            .ConfigureAwait(false);

        if (price.Total.Amount != decimal.Round(expectedAmount, 2, MidpointRounding.AwayFromZero))
        {
            _logger.LogInformation(
                "Order paid in the app at kiosk {KioskId} refused: the app showed {Expected}, the profile now says {Amount}.",
                kiosk.Id,
                expectedAmount,
                price.Total.Amount);
            throw new DomainException(UserMessage.Of("printJobs.priceChanged", ("amount", price.Total.Amount)));
        }

        var actor = ActorContext.User(userId, correlationId: _correlation.CorrelationId);
        var job = await _rules
            .AddWithUniqueCodeAsync(
                code => PrintJob.Create(
                    code,
                    kiosk.Id,
                    source,
                    file.PageCount,
                    options,
                    price,
                    now,
                    userId: userId,
                    fileId: file.Id,
                    localFileName: file.FileName,
                    printFile: file.Pdf,
                    heldForUser: true),
                cancellationToken)
            .ConfigureAwait(false);

        job.AwaitPayment(actor, now);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        var minutes = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.PaymentTimeoutMinutes, 5, cancellationToken).ConfigureAwait(false), 1, 60);

        var payment = Payment.Open(job.Id, kiosk.Id, PaymentMethod.MobileApp, PaymentChannel.Mobile, job.Price.Total, now, now.AddMinutes(minutes));
        payment.ShowAppQr(now);
        payment.AssignUser(userId, now);
        await _payments.AddAsync(payment, cancellationToken).ConfigureAwait(false);

        return (job, payment);
    }

    public static SessionPrintJobResponse Response(PrintJob job, Payment payment)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payment);

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
