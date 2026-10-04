using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Documents;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Pricing;

namespace NeoPrint.Kiosk.Printing;

/// <summary>A quote as the options screen shows it.</summary>
public sealed record OrderQuote(PriceBreakdown Price, int SelectedPages, int Sheets, bool PaperCovers);

/// <summary>
/// The flash order from the kiosk's side (plan sections 7.3 screens 2–5, 7.5,
/// 9.1): open the file, quote it, place it, and hand it to the queue.
/// </summary>
/// <remarks>
/// <para>
/// The quote is computed here from the kiosk's price table so it moves as the
/// customer changes the options; the price that counts is the one the backend
/// computes when the order is placed and keeps as a snapshot (T-047).
/// </para>
/// <para>
/// Placing is four steps: the backend makes the job; the print PDF is built
/// with the chosen pages and N-up; it is encrypted into the spool; the job
/// goes into the journal and the worker is woken. If anything after the first
/// step fails, the job is cancelled so it does not sit in the queue without a
/// file to print.
/// </para>
/// </remarks>
public sealed class KioskOrderService
{
    private readonly KioskApiClient _api;
    private readonly PrintQueueWorker _worker;
    private readonly SpoolStore _spool;
    private readonly DocumentPreparer _preparer;
    private readonly KioskRuntime _runtime;
    private readonly IPrinterFormatSupport _formats;
    private readonly KioskSettings _settings;
    private readonly FlashUploads _uploads;
    private readonly ILogger<KioskOrderService> _logger;

    public KioskOrderService(
        KioskApiClient api,
        PrintQueueWorker worker,
        SpoolStore spool,
        DocumentPreparer preparer,
        KioskRuntime runtime,
        IPrinterFormatSupport formats,
        IOptions<KioskSettings> settings,
        FlashUploads uploads,
        ILogger<KioskOrderService> logger)
    {
        _api = api;
        _worker = worker;
        _spool = spool;
        _preparer = preparer;
        _runtime = runtime;
        _formats = formats;
        _settings = settings.Value;
        _uploads = uploads;
        _logger = logger;
    }

    /// <summary>
    /// Whether this kiosk can take a flash order at all: since step 4 every
    /// kiosk can, and the order is paid before it prints (T-060 hid the flow
    /// until payments existed).
    /// </summary>
    public bool CanTakeOrders => true;

    /// <summary>A Development kiosk queues orders without a payment (plan step 3).</summary>
    public bool SkipsPayment => _settings.SkipPayment;

    public KioskConfiguration? Configuration => _runtime.Configuration;

    public string NewWorkFolder() => _spool.NewWorkFolder();

    public void Discard(string? folder) => _spool.Discard(folder);

    /// <summary>Copies the file off the drive and makes a PDF of it.</summary>
    /// <exception cref="PrintingException">It cannot be printed; the key says why.</exception>
    public Task<PreparedDocument> OpenAsync(string path, string workFolder, CancellationToken cancellationToken)
        => _preparer.PrepareAsync(path, workFolder, cancellationToken);

    /// <summary>The price and the paper for these options, or null when the range does not fit the document.</summary>
    public OrderQuote? Quote(JobOptions options, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuration = _runtime.Configuration;

        if (configuration?.Prices is not { } prices
            || !PageRange.TryParse(options.PageRange, pageCount, out var pages))
        {
            return null;
        }

        var table = new PriceTable(
            prices.BwPerSide,
            prices.ColorPerSide,
            prices.PaperMultipliers,
            prices.MinOrder,
            [.. prices.Tiers.Select(tier => new PriceTier(tier.MinSides, tier.DiscountPercent))],
            prices.Currency);

        PriceBreakdown price;

        try
        {
            price = PriceCalculator.Quote(
                table, new PrintSpec(pages.Count, options.PagesPerSheet, options.Copies, options.Duplex, options.Color, options.Paper));
        }
        catch (PricingException)
        {
            return null;
        }

        var sheets = PrintSheets.Physical(pages.Count, options.PagesPerSheet, options.Copies, options.Duplex);

        // The backend checks against what queued jobs already hold; the screen
        // can only see the tray, which is enough to stop an order that plainly
        // does not fit (plan section 2.15).
        var covers = !configuration.PaperBlockWhenInsufficient
                     || !configuration.PaperStock.TryGetValue(options.Paper, out var stock)
                     || stock.Capacity <= 0
                     || sheets <= stock.Current;

        return new OrderQuote(price, pages.Count, sheets, covers);
    }

    /// <summary>Places the order and hands it to the printer queue.</summary>
    /// <param name="idempotencyKey">
    /// One per order attempt: the same key again (after a lost response) returns
    /// the job already made instead of a second one.
    /// </param>
    /// <exception cref="KioskApiException">The backend refused it; the message says why, in the screen's language.</exception>
    /// <exception cref="PrintingException">The print file could not be made.</exception>
    /// <param name="promoCode">Checked by the backend with the order (plan section 2.7); a code that does not apply refuses the order with the reason.</param>
    public async Task<PrintJobInfo> PlaceAsync(
        PreparedDocument document, string fileName, JobOptions options, string idempotencyKey, CancellationToken cancellationToken, string? promoCode = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        if (!PageRange.TryParse(options.PageRange, document.PageCount, out var pages))
        {
            throw new PrintingException("printJobs.pageRangeInvalid");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // Asked before the job exists: the printer's answer can take a moment.
        var original = await OriginalFormatAsync(document, options, cancellationToken).ConfigureAwait(false);

        var job = await _api
            .CreatePrintJobAsync(fileName, document.PageCount, options, _settings.SkipPayment, idempotencyKey, cancellationToken, promoCode)
            .ConfigureAwait(false);

        var folder = _spool.NewWorkFolder();

        try
        {
            var printFile = document.SourcePath;

            if (original is null)
            {
                printFile = Path.Combine(folder, "print.pdf");
                PdfTools.BuildForPrint(document.PdfPath, printFile, pages, options.PagesPerSheet, options.Paper, options.Scaling == "Actual");
            }

            await using (var stream = File.OpenRead(printFile!))
            {
                await _spool.PutAsync(JournalEntry.SpoolNameFor(job.Id, original), stream, cancellationToken).ConfigureAwait(false);
            }

            // An order still to be paid waits in the journal: nothing prints it until it is paid.
            _worker.Accept(job.Id, job.JobCode, options, job.RequiredSheets, DateTime.UtcNow, original, job.Price.Sides, paid: job.Status == "Queued");

            _logger.LogInformation(
                "Order {JobCode} placed: {Pages} pages, {Sheets} sheets, sent as {ContentType}.",
                job.JobCode,
                job.SelectedPages,
                job.RequiredSheets,
                original ?? "the prepared PDF");

            return job;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Order {JobCode} could not be spooled; cancelling it.", job.JobCode);

            try
            {
                await _api.CancelPrintJobAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cancel) when (cancel is not OperationCanceledException)
            {
                // The worker drops it too: a queued job without a file is never printed.
                _logger.LogWarning(cancel, "Order {JobCode} could not be cancelled either.", job.JobCode);
            }

            throw;
        }
        finally
        {
            _spool.Discard(folder);
        }
    }

    /// <summary>
    /// The rule of plan section 2.2: the customer's own file goes to the
    /// printer when the printer takes its format natively and nothing was
    /// done to it here - no edit, no page range, one page to a side. Anything
    /// else, or a printer that cannot say, gets the prepared PDF.
    /// </summary>
    /// <returns>The original's MIME type when it is sent as it is; null for the PDF.</returns>
    public async Task<string?> OriginalFormatAsync(PreparedDocument document, JobOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var contentType = PrintContentTypes.ForExtension(document.Format.Extension);

        if (contentType is null
            || document.Edited
            || document.SourcePath is null
            || !string.IsNullOrWhiteSpace(options.PageRange)
            || options.PagesPerSheet > 1
            || options.Scaling == "Actual"
            || _runtime.Configuration?.SelectedPrinter is not { } printer)
        {
            return null;
        }

        try
        {
            var native = await _formats.GetNativeFormatsAsync(printer, cancellationToken).ConfigureAwait(false);
            return native.Contains(contentType) ? contentType : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The printer's native formats could not be read; the PDF is sent.");
            return null;
        }
    }

    public Task<PrintJobInfo> FindAsync(string code, CancellationToken cancellationToken) => _api.FindPrintJobByCodeAsync(code, cancellationToken);

    public Task<PrintJobInfo> CancelAsync(Guid jobId, CancellationToken cancellationToken) => _api.CancelPrintJobAsync(jobId, cancellationToken);

    // ---- the personal QR (plan section 9.4)

    /// <summary>The user's files for this kiosk after their QR was read, and the grant to act on them.</summary>
    public Task<UserQrFiles> ScanUserQrAsync(string code, CancellationToken cancellationToken) => _api.ScanUserQrAsync(code, cancellationToken);

    /// <summary>
    /// A file prepared in the app, ordered here. Nothing goes to the spool now:
    /// once paid, the queue worker fetches the PDF from the backend (plan section 7.4).
    /// </summary>
    public Task<PrintJobInfo> OrderPreparedAsync(
        Guid grant, Guid preparedId, JobOptions options, string idempotencyKey, string? promoCode, CancellationToken cancellationToken)
        => _api.OrderPreparedAsync(grant, preparedId, options, idempotencyKey, promoCode, cancellationToken);

    /// <summary>An order paid in the app for this kiosk, into the queue.</summary>
    public Task<PrintJobInfo> ReleaseHeldAsync(Guid grant, Guid jobId, CancellationToken cancellationToken)
        => _api.ReleaseHeldAsync(grant, jobId, cancellationToken);

    // ---- payment (plan sections 9.1, 9.2)

    /// <summary>What a promo code takes off <paramref name="amount"/> here, or why not.</summary>
    public Task<PromoValidation> ValidatePromoAsync(string code, decimal amount, CancellationToken cancellationToken)
        => _api.ValidatePromoAsync(code, _runtime.Configuration?.KioskId, amount, cancellationToken);

    /// <summary>Opens (or returns the open) payment for the job: <c>BankQr</c> or <c>MobileApp</c>.</summary>
    public Task<PaymentInfo> OpenPaymentAsync(Guid jobId, string method, CancellationToken cancellationToken)
        => _api.CreatePaymentIntentAsync(jobId, method, $"intent-{jobId:N}-{method}-{Guid.NewGuid():N}", cancellationToken);

    public Task<PaymentInfo> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken) => _api.GetPaymentAsync(paymentId, cancellationToken);

    /// <summary>The payment went through: the order may print now, without waiting for the next look at the queue.</summary>
    public void MarkPaid(Guid jobId) => _worker.MarkPaid(jobId);

    public Task<FiscalInfo?> GetFiscalAsync(Guid paymentId, CancellationToken cancellationToken) => _api.GetPaymentFiscalAsync(paymentId, cancellationToken);

    public Task EmailReceiptAsync(Guid paymentId, string email, string idempotencyKey, CancellationToken cancellationToken)
        => _api.EmailReceiptAsync(paymentId, email, idempotencyKey, cancellationToken);

    public Task<IReadOnlyList<RefundInfo>> GetRefundsAsync(Guid jobId, CancellationToken cancellationToken) => _api.GetJobRefundsAsync(jobId, cancellationToken);

    /// <summary>
    /// A flash order paid with the app: its file joins the account (plan
    /// section 9.2, step 5), sent now or, failing that, later from the spool.
    /// </summary>
    public Task KeepFlashFileAsync(Guid jobId, string sourcePath, string fileName, CancellationToken cancellationToken)
        => _uploads.KeepAndSendAsync(jobId, sourcePath, fileName, cancellationToken);

    /// <summary>Uploads that failed earlier, again.</summary>
    public Task<int> SendPendingUploadsAsync(CancellationToken cancellationToken) => _uploads.SendPendingAsync(cancellationToken);
}
