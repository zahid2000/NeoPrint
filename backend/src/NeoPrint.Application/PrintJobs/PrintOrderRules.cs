using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Pricing;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;
using NeoPrint.Pricing;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// What every order at a kiosk is checked against and priced by, wherever it
/// is placed - the kiosk's own screen or the app in a session (plan sections
/// 2.2, 2.3, 2.15, 7.7).
/// </summary>
public sealed class PrintOrderRules
{
    /// <summary>Attempts at drawing a job code nobody holds before giving up.</summary>
    private const int CodeAttempts = 8;

    private readonly IPricingProfileRepository _profiles;
    private readonly IPrintJobRepository _jobs;
    private readonly ISettingsReader _settings;
    private readonly ILogger<PrintOrderRules> _logger;

    public PrintOrderRules(
        IPricingProfileRepository profiles, IPrintJobRepository jobs, ISettingsReader settings, ILogger<PrintOrderRules> logger)
    {
        _profiles = profiles;
        _jobs = jobs;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Within the kiosk's limits, something its printer can do, enough paper -
    /// and the price from its profile, taken now and never recomputed.
    /// </summary>
    public async Task<PriceSnapshot> CheckAndPriceAsync(
        Kiosk kiosk, int pageCount, PrintOptions options, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(options);

        await EnsureWithinLimitsAsync(kiosk, pageCount, cancellationToken).ConfigureAwait(false);
        EnsurePrinterCan(kiosk, options);

        var profile = await ProfileOfAsync(kiosk, cancellationToken).ConfigureAwait(false);

        PriceSnapshot price;

        try
        {
            price = PriceSnapshot.Take(profile, options.ToSpec(pageCount), now);
        }
        catch (PricingException exception)
        {
            throw new ValidationException([new ValidationError("options", UserMessage.Of(exception.Code, exception.Arguments))]);
        }

        await EnsurePaperAsync(kiosk, options.Paper, price.Price.Sheets, cancellationToken).ConfigureAwait(false);

        return price;
    }

    /// <summary>Adds the job <paramref name="make"/> builds around a fresh six-digit code, drawing again on a clash.</summary>
    public async Task<PrintJob> AddWithUniqueCodeAsync(Func<string, PrintJob> make, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(make);

        for (var attempt = 0; attempt < CodeAttempts; attempt++)
        {
            var job = make(NewJobCode());

            if (await _jobs.TryAddAsync(job, cancellationToken).ConfigureAwait(false))
            {
                return job;
            }
        }

        // A million codes and a unique index: eight collisions in a row mean
        // something other than bad luck.
        throw new InvalidOperationException("Could not draw an unused job code.");
    }

    /// <summary>Six random digits (plan section 5, <c>JobCode</c>).</summary>
    private static string NewJobCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private async Task EnsureWithinLimitsAsync(Kiosk kiosk, int pageCount, CancellationToken cancellationToken)
    {
        var maxPages = kiosk.MaxPages
                       ?? await _settings.GetIntAsync(SettingKeys.FilesDefaultMaxPages, 200, cancellationToken).ConfigureAwait(false);

        if (pageCount > maxPages)
        {
            throw Invalid("pageCount", "printJobs.tooManyPages", ("count", pageCount), ("max", maxPages));
        }
    }

    /// <summary>
    /// Refuses what the selected printer cannot do, as the options screen
    /// already should have (plan section 7.7: effective capabilities).
    /// </summary>
    private static void EnsurePrinterCan(Kiosk kiosk, PrintOptions options)
    {
        var can = kiosk.EffectiveCapabilities();

        if (options.Color && !can.Color)
        {
            throw Invalid("color", "printJobs.colorUnavailable");
        }

        if (options.Duplex && !can.Duplex)
        {
            throw Invalid("duplex", "printJobs.duplexUnavailable");
        }

        if (options.Paper == "A3" && !can.A3)
        {
            throw Invalid("paper", "printJobs.a3Unavailable");
        }
    }

    /// <summary>
    /// Plan section 2.15: an order the paper cannot cover is refused when
    /// <c>Paper.BlockWhenInsufficient</c> is on. Sheets already promised to
    /// jobs that have not printed yet are not available to this one.
    /// </summary>
    private async Task EnsurePaperAsync(Kiosk kiosk, string paper, int sheets, CancellationToken cancellationToken)
    {
        var block = await _settings
            .GetBoolAsync(SettingKeys.PaperBlockWhenInsufficient, true, cancellationToken)
            .ConfigureAwait(false);

        // A tray with no capacity set has no counter to trust yet: nothing to
        // compare with, so nothing is refused on its account.
        if (!block || !kiosk.PaperStock.TryGetValue(paper, out var stock) || stock.Capacity <= 0)
        {
            return;
        }

        var reserved = await _jobs.ReservedSheetsAsync(kiosk.Id, paper, cancellationToken).ConfigureAwait(false);
        var available = stock.Current - reserved;

        if (sheets > available)
        {
            _logger.LogWarning(
                "Order refused at kiosk {KioskId}: {Sheets} sheets of {Paper} needed, {Available} available.",
                kiosk.Id,
                sheets,
                paper,
                Math.Max(0, available));

            throw new DomainException("printJobs.notEnoughPaper");
        }
    }

    private async Task<PricingProfile> ProfileOfAsync(Kiosk kiosk, CancellationToken cancellationToken)
    {
        var profile = kiosk.PricingProfileId is { } id
            ? await _profiles.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

        return profile
               ?? await _profiles.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("pricingProfile");
    }

    private static ValidationException Invalid(string field, string key, params (string Name, object? Value)[] arguments)
        => new([new ValidationError(field, key, arguments)]);
}
