using Microsoft.Extensions.Logging;

using NeoPrint.Documents;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;

namespace NeoPrint.Kiosk.Printing;

/// <summary>
/// Orders placed in the app (plan section 9.3) as this kiosk prints them:
/// the PDF they were priced on comes from the backend, gets the chosen pages
/// and N-up exactly as a flash order does, and is encrypted into the spool
/// (plan section 7.4). The plain download never stays on disk.
/// </summary>
public sealed class AppOrders : IRemoteOrders
{
    /// <summary>Where an order comes from when its file is fetched rather than placed here.</summary>
    private static readonly string[] Fetched = ["MobileSession", "UserQr", "Scheduled"];

    private readonly KioskApiClient _api;
    private readonly SpoolStore _spool;
    private readonly ILogger<AppOrders> _logger;

    public AppOrders(KioskApiClient api, SpoolStore spool, ILogger<AppOrders> logger)
    {
        _api = api;
        _spool = spool;
        _logger = logger;
    }

    public async Task<RemoteOrder?> FetchAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _api.GetPrintJobAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (!Fetched.Contains(job.Source) || job.Status != "Queued")
        {
            return null;
        }

        var folder = _spool.NewWorkFolder();

        try
        {
            var downloaded = Path.Combine(folder, "order.pdf");

            await using (var file = File.Create(downloaded))
            {
                await _api.DownloadPrintFileAsync(job.Id, file, cancellationToken).ConfigureAwait(false);
            }

            if (!PageRange.TryParse(job.Options.PageRange, job.PageCount, out var pages))
            {
                // The backend checked the range against the same page count;
                // a mismatch here is a bug, not a customer's mistake.
                throw new PrintingException("printJobs.pageRangeInvalid");
            }

            var printFile = Path.Combine(folder, "print.pdf");
            PdfTools.BuildForPrint(downloaded, printFile, pages, job.Options.PagesPerSheet, job.Options.Paper);

            await using (var stream = File.OpenRead(printFile))
            {
                await _spool.PutAsync(JournalEntry.SpoolNameFor(job.Id), stream, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Order {JobCode} from the app prepared: {Pages} page(s), {Sheets} sheet(s).", job.JobCode, pages.Count, job.RequiredSheets);

            return new RemoteOrder(job.Id, job.JobCode, job.Options, job.RequiredSheets, job.Price.Sides);
        }
        finally
        {
            // The download and the prepared copy are plain text: only the
            // encrypted spool keeps the order (plan section 2.13).
            _spool.Discard(folder);
        }
    }
}
