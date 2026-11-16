using System.Text.Json;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Pricing;
using NeoPrint.Pricing;

namespace NeoPrint.Application.Pricing;

/// <summary>Reading and writing pricing campaigns (T-436).</summary>
public interface IPricingCampaignRepository
{
    Task<IReadOnlyList<PricingCampaign>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The ones switched on - those that may price a kiosk now or later.</summary>
    Task<IReadOnlyList<PricingCampaign>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Switched on, and starting or ending inside (<paramref name="after"/>, <paramref name="until"/>].</summary>
    Task<bool> AnyBoundaryAsync(DateTime after, DateTime until, CancellationToken cancellationToken = default);

    Task<PricingCampaign?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(PricingCampaign campaign, CancellationToken cancellationToken = default);

    Task UpdateAsync(PricingCampaign campaign, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The prices a kiosk charges now: its profile, or the default one (plan
/// section 2.3), and the campaign running there, if any (T-436). The quote,
/// the order, the kiosk's configuration and the app's kiosk page all ask here,
/// so none of them can disagree.
/// </summary>
public sealed class KioskPricing
{
    private readonly IPricingProfileRepository _profiles;
    private readonly IPricingCampaignRepository _campaigns;

    public KioskPricing(IPricingProfileRepository profiles, IPricingCampaignRepository campaigns)
    {
        _profiles = profiles;
        _campaigns = campaigns;
    }

    /// <summary>The kiosk's profile and campaign; for no kiosk, the default profile alone.</summary>
    public async Task<(PricingProfile Profile, PricingCampaign? Campaign)> ForAsync(
        Kiosk? kiosk, DateTime now, CancellationToken cancellationToken)
    {
        var profile = (kiosk?.PricingProfileId is { } id
                          ? await _profiles.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
                          : null)
                      ?? await _profiles.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("pricingProfile");

        if (kiosk is null)
        {
            return (profile, null);
        }

        var campaigns = await _campaigns.ListActiveAsync(cancellationToken).ConfigureAwait(false);

        return (profile, PricingCampaign.Pick(campaigns, kiosk.Id, now));
    }

    /// <summary>The kiosk's rates now, the campaign applied; null when there is no profile at all.</summary>
    public async Task<PriceTable?> TableForAsync(Kiosk kiosk, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var (profile, campaign) = await ForAsync(kiosk, now, cancellationToken).ConfigureAwait(false);
            return campaign is null ? profile.ToPriceTable() : campaign.Apply(profile.ToPriceTable());
        }
        catch (NotFoundException)
        {
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// Shapes
// ---------------------------------------------------------------------------

/// <summary>A campaign as the panel edits it.</summary>
/// <param name="StartsAt">Null for "from now".</param>
/// <param name="EndsAt">Null for "until switched off".</param>
/// <param name="KioskIds">Empty for every kiosk.</param>
public sealed record PricingCampaignInput(
    string Name,
    CampaignKind Kind,
    decimal Percent,
    decimal? BwPricePerSide,
    decimal? ColorPricePerSide,
    IReadOnlyList<VolumeTierDto>? VolumeTiers,
    TierMode TierMode,
    DateTime? StartsAt,
    DateTime? EndsAt,
    IReadOnlyList<Guid>? KioskIds,
    bool IsActive = true);

/// <param name="State"><c>Running</c>, <c>Scheduled</c>, <c>Ended</c> or <c>Off</c>, as of the request.</param>
public sealed record PricingCampaignResponse(
    Guid Id,
    string Name,
    CampaignKind Kind,
    decimal Percent,
    decimal? BwPricePerSide,
    decimal? ColorPricePerSide,
    IReadOnlyList<VolumeTierDto> VolumeTiers,
    TierMode TierMode,
    DateTime? StartsAt,
    DateTime? EndsAt,
    IReadOnlyList<Guid> KioskIds,
    bool IsActive,
    string State,
    DateTime UpdatedAt,
    int Revision);

public sealed record ListPricingCampaignsQuery : IRequest<IReadOnlyList<PricingCampaignResponse>>;

public sealed record GetPricingCampaignQuery(Guid Id) : IRequest<PricingCampaignResponse>;

public sealed record CreatePricingCampaignCommand(PricingCampaignInput Input, Guid? AdminUserId, string? Ip)
    : IRequest<PricingCampaignResponse>, ITransactionalRequest;

public sealed record UpdatePricingCampaignCommand(Guid Id, PricingCampaignInput Input, Guid? AdminUserId, string? Ip)
    : IRequest<PricingCampaignResponse>, ITransactionalRequest;

public sealed record DeletePricingCampaignCommand(Guid Id, Guid? AdminUserId, string? Ip)
    : IRequest, ITransactionalRequest;

/// <summary>
/// Kiosks fetch their prices again when a campaign starts or ends (T-436):
/// nothing else tells them the hour came.
/// </summary>
/// <param name="After">The start of the window looked at; boundaries after it and up to now count.</param>
public sealed record AnnounceCampaignBoundariesCommand(DateTime After) : IRequest<bool>;

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

/// <summary>The "Kampaniyalar" tab of the pricing page (T-436).</summary>
public sealed class PricingCampaignHandlers :
    IRequestHandler<ListPricingCampaignsQuery, IReadOnlyList<PricingCampaignResponse>>,
    IRequestHandler<GetPricingCampaignQuery, PricingCampaignResponse>,
    IRequestHandler<CreatePricingCampaignCommand, PricingCampaignResponse>,
    IRequestHandler<UpdatePricingCampaignCommand, PricingCampaignResponse>,
    IRequestHandler<DeletePricingCampaignCommand>,
    IRequestHandler<AnnounceCampaignBoundariesCommand, bool>
{
    private readonly IPricingCampaignRepository _campaigns;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskNotifier _notifier;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public PricingCampaignHandlers(
        IPricingCampaignRepository campaigns, IKioskRepository kiosks, IKioskNotifier notifier, IAuditWriter audit, IClock clock)
    {
        _campaigns = campaigns;
        _kiosks = kiosks;
        _notifier = notifier;
        _audit = audit;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PricingCampaignResponse>> Handle(
        ListPricingCampaignsQuery request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var campaigns = await _campaigns.ListAsync(cancellationToken).ConfigureAwait(false);

        // Running first, then those to come, then the rest; the newest first within each.
        return [.. campaigns
            .Select(c => Map(c, now))
            .OrderBy(c => c.State switch { "Running" => 0, "Scheduled" => 1, "Off" => 2, _ => 3 })
            .ThenByDescending(c => c.StartsAt ?? c.UpdatedAt)];
    }

    public async Task<PricingCampaignResponse> Handle(GetPricingCampaignQuery request, CancellationToken cancellationToken)
        => Map(await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false), _clock.UtcNow);

    public async Task<PricingCampaignResponse> Handle(CreatePricingCampaignCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var campaign = new PricingCampaign { CreatedAt = now };
        Apply(campaign, request.Input, request.AdminUserId, now);

        await ValidateAsync(campaign, cancellationToken).ConfigureAwait(false);
        await _campaigns.AddAsync(campaign, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "pricing.campaign.create", campaign, null, request.Ip, cancellationToken).ConfigureAwait(false);
        await _notifier.AllKiosksConfigChangedAsync(cancellationToken).ConfigureAwait(false);

        return Map(campaign, now);
    }

    public async Task<PricingCampaignResponse> Handle(UpdatePricingCampaignCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var campaign = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var before = Snapshot(campaign);

        Apply(campaign, request.Input, request.AdminUserId, now);

        await ValidateAsync(campaign, cancellationToken).ConfigureAwait(false);
        await _campaigns.UpdateAsync(campaign, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "pricing.campaign.update", campaign, before, request.Ip, cancellationToken).ConfigureAwait(false);
        await _notifier.AllKiosksConfigChangedAsync(cancellationToken).ConfigureAwait(false);

        return Map(campaign, now);
    }

    public async Task Handle(DeletePricingCampaignCommand request, CancellationToken cancellationToken)
    {
        var campaign = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);

        // Orders already priced by it keep their copy of its rates.
        await _campaigns.DeleteAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "pricing.campaign.delete", campaign, Snapshot(campaign), request.Ip, cancellationToken).ConfigureAwait(false);
        await _notifier.AllKiosksConfigChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> Handle(AnnounceCampaignBoundariesCommand request, CancellationToken cancellationToken)
    {
        if (!await _campaigns.AnyBoundaryAsync(request.After, _clock.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _notifier.AllKiosksConfigChangedAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<PricingCampaign> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await _campaigns.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("pricingCampaign");

    private async Task ValidateAsync(PricingCampaign campaign, CancellationToken cancellationToken)
    {
        var problems = campaign.Validate().Select(p => new ValidationError(p.Field, p.Message)).ToList();

        foreach (var kioskId in campaign.KioskIds)
        {
            if (await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) is null)
            {
                problems.Add(new ValidationError("kioskIds", "pricing.kioskUnknown"));
                break;
            }
        }

        if (problems.Count > 0)
        {
            throw new ValidationException(problems);
        }
    }

    private static void Apply(PricingCampaign campaign, PricingCampaignInput input, Guid? adminUserId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(input);

        campaign.Name = input.Name?.Trim() ?? string.Empty;
        campaign.Kind = input.Kind;
        campaign.IsActive = input.IsActive;
        campaign.StartsAt = input.StartsAt?.ToUniversalTime();
        campaign.EndsAt = input.EndsAt?.ToUniversalTime();
        campaign.KioskIds = [.. (input.KioskIds ?? []).Distinct()];
        campaign.UpdatedBy = adminUserId;
        campaign.UpdatedAt = now;

        // Only what the kind uses is kept, so a switched kind leaves nothing behind.
        if (input.Kind == CampaignKind.Percent)
        {
            campaign.Percent = input.Percent;
            campaign.BwPricePerSide = null;
            campaign.ColorPricePerSide = null;
            campaign.VolumeTiers = [];
            campaign.TierMode = TierMode.WholeOrder;
        }
        else
        {
            campaign.Percent = 0m;
            campaign.BwPricePerSide = input.BwPricePerSide;
            campaign.ColorPricePerSide = input.ColorPricePerSide;
            campaign.VolumeTiers = [.. (input.VolumeTiers ?? []).Select(t => t.ToTier()).OrderBy(t => t.MinSides)];
            campaign.TierMode = input.TierMode;
        }
    }

    private static string StateOf(PricingCampaign campaign, DateTime now) => campaign switch
    {
        { IsActive: false } => "Off",
        { EndsAt: { } until } when until <= now => "Ended",
        { StartsAt: { } from } when from > now => "Scheduled",
        _ => "Running",
    };

    private static PricingCampaignResponse Map(PricingCampaign campaign, DateTime now) => new(
        campaign.Id,
        campaign.Name,
        campaign.Kind,
        campaign.Percent,
        campaign.BwPricePerSide,
        campaign.ColorPricePerSide,
        [.. campaign.VolumeTiers.OrderBy(t => t.MinSides).Select(VolumeTierDto.From)],
        campaign.TierMode,
        campaign.StartsAt,
        campaign.EndsAt,
        [.. campaign.KioskIds],
        campaign.IsActive,
        StateOf(campaign, now),
        campaign.UpdatedAt,
        campaign.RowVersion);

    private static string Snapshot(PricingCampaign campaign) => JsonSerializer.Serialize(new
    {
        campaign.Name,
        campaign.Kind,
        campaign.Percent,
        campaign.BwPricePerSide,
        campaign.ColorPricePerSide,
        VolumeTiers = campaign.VolumeTiers.Select(t => new { t.MinSides, t.DiscountPercent, t.BwPricePerSide, t.ColorPricePerSide }),
        campaign.TierMode,
        campaign.StartsAt,
        campaign.EndsAt,
        campaign.KioskIds,
        campaign.IsActive,
    });

    private Task AuditAsync(
        Guid? adminUserId, string action, PricingCampaign campaign, string? before, string? ip, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminUserId, action, "PricingCampaign", campaign.Id.ToString(), before, Snapshot(campaign), ip, Reason: null),
            cancellationToken);
}
