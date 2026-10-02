using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Pricing;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Kiosks;

/// <summary>A place on the map, in degrees.</summary>
public sealed record GeoPoint(double Lat, double Lng);

/// <param name="Text">Part of the name, the code or the address, any case.</param>
/// <param name="Id">The kiosk's own id: an exact match instead of the text.</param>
/// <param name="Near">Nearest first, with the distance, when given.</param>
/// <param name="RadiusKm">No farther than this from <paramref name="Near"/>.</param>
/// <param name="Statuses">Only kiosks in one of these.</param>
public sealed record KioskSearchCriteria(
    string? Text, Guid? Id, GeoPoint? Near, double? RadiusKm, IReadOnlyCollection<KioskStatus> Statuses, int Limit);

/// <param name="DistanceMeters">From the point searched near; null without one.</param>
public sealed record KioskHit(Kiosk Kiosk, double? DistanceMeters);

/// <summary>The kiosk search over the source of truth (plan step 5: text + <c>2dsphere</c>).</summary>
public interface IKioskSearch
{
    Task<IReadOnlyList<KioskHit>> SearchAsync(KioskSearchCriteria criteria, CancellationToken cancellationToken = default);
}

/// <summary>
/// The kiosks tab of the app (plan sections 6 and 8.2, <c>GET kiosks?search=&amp;lat=&amp;lng=&amp;radiusKm=</c>):
/// by name, code or id, and near the user.
/// </summary>
public sealed record SearchKiosksQuery(string? Search, double? Lat, double? Lng, double? RadiusKm) : IRequest<KioskSearchResult>;

/// <summary>One kiosk for a customer (<c>GET kiosks/{idOrCode}</c>): what it can do, what it costs, how busy it is.</summary>
public sealed record GetPublicKioskQuery(string IdOrCode) : IRequest<PublicKioskDetail>;

/// <summary>How long the queue is (<c>GET kiosks/{id}/queue</c>) - a count, never whose jobs they are.</summary>
public sealed record GetPublicKioskQueueQuery(Guid KioskId) : IRequest<PublicKioskQueue>;

public sealed record KioskSearchResult(IReadOnlyList<PublicKioskListItem> Items);

/// <param name="Status">
/// What the customer can expect: <c>Online</c>, <c>Paused</c>, <c>Offline</c>
/// or <c>Maintenance</c>. A kiosk that has stopped calling in is
/// <c>Offline</c> whatever its stored status says.
/// </param>
/// <param name="DistanceKm">From the point searched near, rounded to 10 m; null without one.</param>
public sealed record PublicKioskListItem(
    Guid Id, string Code, string Name, string Address, double Lat, double Lng, string Status, double? DistanceKm);

/// <param name="Paper">Each paper size the kiosk has a tray for.</param>
/// <param name="QueueLength">Jobs queued or printing now.</param>
/// <param name="Prices">The rates of the kiosk's pricing profile; null when no profile exists yet.</param>
public sealed record PublicKioskDetail(
    Guid Id,
    string Code,
    string Name,
    string Address,
    double Lat,
    double Lng,
    string Status,
    PrinterCapabilitiesDto Capabilities,
    IReadOnlyList<PublicPaperView> Paper,
    int QueueLength,
    KioskPricesDto? Prices);

/// <param name="Size">A4 or A3.</param>
/// <param name="AvailableSheets">
/// Sheets not yet promised to a job; null when the tray has no capacity set
/// and so no counter to trust.
/// </param>
/// <param name="State"><c>Ok</c>, <c>Low</c> ("kağız azdır") or <c>Out</c>; <c>Unknown</c> without a counter.</param>
public sealed record PublicPaperView(string Size, int? AvailableSheets, string State);

public sealed record PublicKioskQueue(Guid KioskId, int Length);

/// <inheritdoc cref="SearchKiosksQuery" />
public sealed class SearchKiosksValidator : IValidator<SearchKiosksQuery>
{
    public IEnumerable<ValidationError> Validate(SearchKiosksQuery request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Search is { Length: > KioskSearchHandlers.MaxSearchLength })
        {
            yield return new ValidationError("search", "kiosks.searchTooLong");
        }

        if (request.Lat.HasValue != request.Lng.HasValue)
        {
            yield return new ValidationError("lat", "kiosks.searchPointIncomplete");
        }

        if (request.Lat is { } lat && (lat is < -90 or > 90 || double.IsNaN(lat)))
        {
            yield return new ValidationError("lat", "kiosks.latitudeRange");
        }

        if (request.Lng is { } lng && (lng is < -180 or > 180 || double.IsNaN(lng)))
        {
            yield return new ValidationError("lng", "kiosks.longitudeRange");
        }

        if (request.RadiusKm is { } radius)
        {
            if (request.Lat is null)
            {
                yield return new ValidationError("radiusKm", "kiosks.searchRadiusWithoutPoint");
            }
            else if (radius is < KioskSearchHandlers.MinRadiusKm or > KioskSearchHandlers.MaxRadiusKm || double.IsNaN(radius))
            {
                yield return new ValidationError(
                    "radiusKm",
                    UserMessage.Of("kiosks.searchRadiusRange", ("min", KioskSearchHandlers.MinRadiusKm), ("max", KioskSearchHandlers.MaxRadiusKm)));
            }
        }
    }
}

/// <summary>The customer's view of kiosks: search, detail, queue length.</summary>
public sealed class KioskSearchHandlers :
    IRequestHandler<SearchKiosksQuery, KioskSearchResult>,
    IRequestHandler<GetPublicKioskQuery, PublicKioskDetail>,
    IRequestHandler<GetPublicKioskQueueQuery, PublicKioskQueue>
{
    public const int MaxSearchLength = 100;
    public const double MinRadiusKm = 0.1;
    public const double MaxRadiusKm = 100;

    /// <summary>The most kiosks one search returns: a city's worth, nearest or by code first.</summary>
    public const int MaxResults = 100;

    /// <summary>
    /// Fewer sheets than this left for new orders is "kağız azdır" - the
    /// plan's seed Warning level for A4 (plan section 2.15). The admin-made
    /// supply alert rules that replace it per kiosk are plan step 7.
    /// </summary>
    public const int LowPaperSheets = 200;

    /// <summary>
    /// What a customer may see. A kiosk not yet activated, not yet given a
    /// printer or switched off is nowhere to go (plan section 8.2).
    /// </summary>
    private static readonly KioskStatus[] Listed =
        [KioskStatus.Online, KioskStatus.Paused, KioskStatus.Offline, KioskStatus.Maintenance];

    private readonly IKioskSearch _search;
    private readonly IKioskRepository _kiosks;
    private readonly IPrintJobRepository _jobs;
    private readonly IPricingProfileRepository _profiles;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;

    public KioskSearchHandlers(
        IKioskSearch search,
        IKioskRepository kiosks,
        IPrintJobRepository jobs,
        IPricingProfileRepository profiles,
        ISettingsReader settings,
        IClock clock)
    {
        _search = search;
        _kiosks = kiosks;
        _jobs = jobs;
        _profiles = profiles;
        _settings = settings;
        _clock = clock;
    }

    /// <inheritdoc cref="SearchKiosksQuery" />
    public async Task<KioskSearchResult> Handle(SearchKiosksQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var text = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();
        var id = text is not null && Guid.TryParse(text, out var parsed) ? parsed : (Guid?)null;
        var near = request.Lat is { } lat && request.Lng is { } lng ? new GeoPoint(lat, lng) : null;

        var hits = await _search
            .SearchAsync(new KioskSearchCriteria(id is null ? text : null, id, near, request.RadiusKm, Listed, MaxResults), cancellationToken)
            .ConfigureAwait(false);

        var silentAfter = await SilentAfterAsync(cancellationToken).ConfigureAwait(false);

        return new KioskSearchResult(
        [
            .. hits.Select(hit => new PublicKioskListItem(
                hit.Kiosk.Id,
                hit.Kiosk.Code,
                hit.Kiosk.Name,
                hit.Kiosk.Location.Address,
                hit.Kiosk.Location.Lat,
                hit.Kiosk.Location.Lng,
                ShownStatus(hit.Kiosk, silentAfter),
                hit.DistanceMeters is { } meters ? Math.Round(meters / 1000, 2) : null)),
        ]);
    }

    /// <inheritdoc cref="GetPublicKioskQuery" />
    public async Task<PublicKioskDetail> Handle(GetPublicKioskQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await ListedAsync(request.IdOrCode, cancellationToken).ConfigureAwait(false);
        var capabilities = kiosk.EffectiveCapabilities();
        var queue = await _jobs.ListQueueAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        var paper = new List<PublicPaperView>();

        foreach (var size in new[] { "A4", "A3" })
        {
            if (size == "A3" && !capabilities.A3)
            {
                continue;
            }

            paper.Add(await PaperAsync(kiosk, size, cancellationToken).ConfigureAwait(false));
        }

        var profile = (kiosk.PricingProfileId is { } profileId
                          ? await _profiles.FindByIdAsync(profileId, cancellationToken).ConfigureAwait(false)
                          : null)
                      ?? await _profiles.FindDefaultAsync(cancellationToken).ConfigureAwait(false);
        var table = profile?.ToPriceTable();

        return new PublicKioskDetail(
            kiosk.Id,
            kiosk.Code,
            kiosk.Name,
            kiosk.Location.Address,
            kiosk.Location.Lat,
            kiosk.Location.Lng,
            ShownStatus(kiosk, await SilentAfterAsync(cancellationToken).ConfigureAwait(false)),
            new PrinterCapabilitiesDto(capabilities.Color, capabilities.Duplex, capabilities.A3),
            paper,
            queue.Count,
            table is null
                ? null
                : new KioskPricesDto(
                    profile!.Name,
                    table.BwPerSide,
                    table.ColorPerSide,
                    table.PaperMultipliers,
                    table.MinOrder,
                    [.. table.Tiers.Select(tier => new KioskPriceTierDto(tier.MinSides, tier.DiscountPercent))],
                    table.Currency));
    }

    /// <inheritdoc cref="GetPublicKioskQueueQuery" />
    public async Task<PublicKioskQueue> Handle(GetPublicKioskQueueQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await ListedAsync(request.KioskId.ToString(), cancellationToken).ConfigureAwait(false);
        var queue = await _jobs.ListQueueAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return new PublicKioskQueue(kiosk.Id, queue.Count);
    }

    /// <summary>A kiosk by id or code that a customer may see; any other is not found, not "hidden".</summary>
    private async Task<Kiosk> ListedAsync(string idOrCode, CancellationToken cancellationToken)
    {
        var value = idOrCode?.Trim() ?? string.Empty;
        var kiosk = Guid.TryParse(value, out var id)
            ? await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : value.Length is > 0 and <= 32
                ? await _kiosks.FindByCodeAsync(value.ToUpperInvariant(), cancellationToken).ConfigureAwait(false)
                : null;

        return kiosk is not null && Listed.Contains(kiosk.Status) ? kiosk : throw new NotFoundException("kiosk");
    }

    private async Task<PublicPaperView> PaperAsync(Kiosk kiosk, string size, CancellationToken cancellationToken)
    {
        // A tray with no capacity set has no counter to trust yet - the same
        // rule the order check follows (PrintJobHandlers.EnsurePaperAsync).
        if (!kiosk.PaperStock.TryGetValue(size, out var stock) || stock.Capacity <= 0)
        {
            return new PublicPaperView(size, null, "Unknown");
        }

        var reserved = await _jobs.ReservedSheetsAsync(kiosk.Id, size, cancellationToken).ConfigureAwait(false);
        var available = Math.Max(0, stock.Current - reserved);

        return new PublicPaperView(size, available, available == 0 ? "Out" : available < LowPaperSheets ? "Low" : "Ok");
    }

    private async Task<TimeSpan> SilentAfterAsync(CancellationToken cancellationToken)
        => TimeSpan.FromSeconds(Math.Max(
            1, await _settings.GetIntAsync(SettingKeys.KioskOfflineAfterSeconds, 60, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// The stored status, except that a kiosk that stopped calling in is
    /// Offline: the monitor that stores that is plan step 7 (T-083), and a
    /// customer must not walk to a kiosk that only looks open.
    /// </summary>
    private string ShownStatus(Kiosk kiosk, TimeSpan silentAfter)
        => kiosk.LastHeartbeatAt is { } heartbeat && _clock.UtcNow - heartbeat > silentAfter
            ? nameof(KioskStatus.Offline)
            : kiosk.Status.ToString();
}
