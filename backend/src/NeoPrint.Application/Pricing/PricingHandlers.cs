using System.Text.Json;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Pricing;

namespace NeoPrint.Application.Pricing;

/// <summary>
/// Pricing: the quote everyone asks for, and the profile administration behind
/// it (plan sections 2.3, 8A.3).
/// </summary>
public sealed class PricingHandlers :
    IRequestHandler<QuoteQuery, QuoteResponse>,
    IRequestHandler<ListPricingProfilesQuery, IReadOnlyList<PricingProfileResponse>>,
    IRequestHandler<GetPricingProfileQuery, PricingProfileDetail>,
    IRequestHandler<CreatePricingProfileCommand, PricingProfileResponse>,
    IRequestHandler<UpdatePricingProfileCommand, PricingProfileResponse>,
    IRequestHandler<DeletePricingProfileCommand>,
    IRequestHandler<MakeDefaultPricingProfileCommand, PricingProfileResponse>,
    IRequestHandler<AssignKiosksToProfileCommand, PricingProfileDetail>,
    IRequestHandler<CalculatePriceQuery, PriceBreakdown>
{
    private readonly IPricingProfileRepository _profiles;
    private readonly IKioskRepository _kiosks;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly Promos.PromoService _promos;

    public PricingHandlers(
        IPricingProfileRepository profiles, IKioskRepository kiosks, IAuditWriter audit, IClock clock, Promos.PromoService promos)
    {
        _profiles = profiles;
        _kiosks = kiosks;
        _audit = audit;
        _clock = clock;
        _promos = promos;
    }

    // -----------------------------------------------------------------------
    // Quote
    // -----------------------------------------------------------------------

    public async Task<QuoteResponse> Handle(QuoteQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await ProfileForKioskAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        // The same code the snapshot uses, so a quote and the price taken when
        // the order is placed a minute later cannot disagree.
        var snapshot = Price(() => PriceSnapshot.Take(profile, request.Options.ToSpec(), _clock.UtcNow));

        if (string.IsNullOrWhiteSpace(request.PromoCode))
        {
            return new QuoteResponse(profile.Id, profile.Name, snapshot.Price, AmountDue: snapshot.Price.FinalPrice);
        }

        // Plan section 2.7: the code applies last. Bonus pages are chosen when
        // paying in the app, which works the code out again on what is left.
        var promo = await _promos
            .CheckAsync(request.PromoCode, request.KioskId, profile.Id, snapshot.Total, userId: null, cancellationToken)
            .ConfigureAwait(false);

        return new QuoteResponse(
            profile.Id,
            profile.Name,
            snapshot.Price,
            promo.Promo?.Code,
            promo.Discount.Amount,
            (snapshot.Total - promo.Discount).Amount,
            promo.Refusal is { } refusal ? Localization.Messages.Write(refusal) : null);
    }

    /// <summary>The kiosk's own profile, or the default one (plan section 2.3).</summary>
    private async Task<PricingProfile> ProfileForKioskAsync(Guid? kioskId, CancellationToken cancellationToken)
    {
        if (kioskId is { } id)
        {
            var kiosk = await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("kiosk");

            if (kiosk.PricingProfileId is { } profileId
                && await _profiles.FindByIdAsync(profileId, cancellationToken).ConfigureAwait(false) is { } own)
            {
                return own;
            }
        }

        return await _profiles.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "There is no default pricing profile. The seeder creates one; it has been removed from the database.");
    }

    // -----------------------------------------------------------------------
    // Administration
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<PricingProfileResponse>> Handle(
        ListPricingProfilesQuery request, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await _profiles.CountKiosksAsync(cancellationToken).ConfigureAwait(false);

        return [.. profiles
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p => Map(p, counts))];
    }

    public async Task<PricingProfileDetail> Handle(GetPricingProfileQuery request, CancellationToken cancellationToken)
        => await DetailAsync(await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    public async Task<PricingProfileResponse> Handle(CreatePricingProfileCommand request, CancellationToken cancellationToken)
    {
        var profile = new PricingProfile { UpdatedBy = request.AdminUserId, UpdatedAt = _clock.UtcNow };
        Apply(profile, request.Input);

        await ValidateAsync(profile, exceptId: null, cancellationToken).ConfigureAwait(false);
        await _profiles.AddAsync(profile, cancellationToken).ConfigureAwait(false);

        await AuditAsync(request.AdminUserId, "pricing.profile.create", profile, null, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return Map(profile, new Dictionary<Guid, int>());
    }

    public async Task<PricingProfileResponse> Handle(UpdatePricingProfileCommand request, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var before = Snapshot(profile);

        Apply(profile, request.Input);
        await ValidateAsync(profile, profile.Id, cancellationToken).ConfigureAwait(false);

        // The history is what makes "why did this kiosk charge that on the
        // 12th" answerable. The orders themselves hold their own copy of the
        // rates, so nothing already priced moves (plan section 2.3).
        profile.History.Add(new PricingProfileChange { At = _clock.UtcNow, By = request.AdminUserId, Before = before });
        profile.UpdatedBy = request.AdminUserId;
        profile.UpdatedAt = _clock.UtcNow;

        await _profiles.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);

        await AuditAsync(request.AdminUserId, "pricing.profile.update", profile, before, request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return Map(profile, await _profiles.CountKiosksAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task Handle(DeletePricingProfileCommand request, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);

        if (profile.IsDefault)
        {
            // There is always exactly one default (plan section 2.3).
            throw Invalid("id", "pricing.defaultUndeletable");
        }

        var counts = await _profiles.CountKiosksAsync(cancellationToken).ConfigureAwait(false);

        if (counts.TryGetValue(profile.Id, out var kiosks) && kiosks > 0)
        {
            // Deleting it would silently move those kiosks to the default
            // prices. That has to be a decision somebody makes, not a side
            // effect of a delete.
            throw Invalid("id", "pricing.profileInUse", ("count", kiosks));
        }

        await _profiles.DeleteAsync(profile.Id, cancellationToken).ConfigureAwait(false);

        await AuditAsync(request.AdminUserId, "pricing.profile.delete", profile, Snapshot(profile), request.Ip, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PricingProfileResponse> Handle(MakeDefaultPricingProfileCommand request, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);

        if (!profile.IsDefault)
        {
            foreach (var current in (await _profiles.ListAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.IsDefault))
            {
                current.IsDefault = false;
                await _profiles.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
            }

            profile.IsDefault = true;
            profile.UpdatedBy = request.AdminUserId;
            profile.UpdatedAt = _clock.UtcNow;
            await _profiles.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);

            await AuditAsync(request.AdminUserId, "pricing.profile.default", profile, null, request.Ip, cancellationToken)
                .ConfigureAwait(false);
        }

        return Map(profile, await _profiles.CountKiosksAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<PricingProfileDetail> Handle(AssignKiosksToProfileCommand request, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);

        foreach (var kioskId in request.KioskIds.Distinct())
        {
            var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid("kioskIds", "pricing.kioskUnknown");

            if (kiosk.PricingProfileId == profile.Id)
            {
                continue;
            }

            var previous = kiosk.PricingProfileId;
            kiosk.PricingProfileId = profile.Id;
            await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

            await _audit.WriteAsync(
                    new AuditEntry(
                        request.AdminUserId,
                        "pricing.profile.assign",
                        "Kiosk",
                        kiosk.Id.ToString(),
                        JsonSerializer.Serialize(new { PricingProfileId = previous }),
                        JsonSerializer.Serialize(new { PricingProfileId = profile.Id }),
                        request.Ip,
                        Reason: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await DetailAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PriceBreakdown> Handle(CalculatePriceQuery request, CancellationToken cancellationToken)
    {
        PricingProfile profile;

        if (request.Draft is { } draft)
        {
            // Values still on the form: priced without saving, so the
            // administrator sees the effect before committing to it.
            profile = new PricingProfile();
            Apply(profile, draft);

            if (profile.Validate() is { Count: > 0 } problems)
            {
                throw new ValidationException([.. problems.Select(p => new ValidationError(p.Field, p.Message))]);
            }
        }
        else if (request.ProfileId is { } id)
        {
            profile = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw Invalid("profileId", "pricing.quoteInputRequired");
        }

        return Price(() => PriceCalculator.Quote(profile.ToPriceTable(), request.Options.ToSpec()));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<PricingProfile> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await _profiles.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("pricingProfile");

    private async Task<PricingProfileDetail> DetailAsync(PricingProfile profile, CancellationToken cancellationToken)
    {
        var counts = await _profiles.CountKiosksAsync(cancellationToken).ConfigureAwait(false);
        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);

        // Kiosks on this profile, plus - for the default profile - those with
        // none of their own, since they are priced by it too.
        var priced = kiosks
            .Where(k => k.PricingProfileId == profile.Id || (profile.IsDefault && k.PricingProfileId is null))
            .OrderBy(k => k.Code, StringComparer.Ordinal)
            .Select(k => new PricingKioskDto(k.Id, k.Code, k.Name, k.PricingProfileId))
            .ToList();

        return new PricingProfileDetail(Map(profile, counts), priced);
    }

    private async Task ValidateAsync(PricingProfile profile, Guid? exceptId, CancellationToken cancellationToken)
    {
        var problems = profile.Validate().Select(p => new ValidationError(p.Field, p.Message)).ToList();

        if (!string.IsNullOrWhiteSpace(profile.Name)
            && await _profiles.NameTakenAsync(profile.Name, exceptId, cancellationToken).ConfigureAwait(false))
        {
            problems.Add(new ValidationError("name", "pricing.nameTaken"));
        }

        if (problems.Count > 0)
        {
            throw new ValidationException(problems);
        }
    }

    private static void Apply(PricingProfile profile, PricingProfileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        profile.Name = input.Name?.Trim() ?? string.Empty;
        profile.BwPricePerSide = Money.FromAzn(input.BwPricePerSide);
        profile.ColorPricePerSide = Money.FromAzn(input.ColorPricePerSide);
        profile.MinOrder = Money.FromAzn(input.MinOrder);
        profile.PaperMultipliers = new Dictionary<string, decimal>(
            input.PaperMultipliers ?? new Dictionary<string, decimal>(), StringComparer.Ordinal);
        profile.VolumeTiers = [.. (input.VolumeTiers ?? [])
            .Select(t => new VolumeTier { MinSides = t.MinSides, DiscountPercent = t.DiscountPercent })
            .OrderBy(t => t.MinSides)];
    }

    private static T Price<T>(Func<T> compute)
    {
        try
        {
            return compute();
        }
        catch (PricingException exception)
        {
            throw new ValidationException([new ValidationError("options", UserMessage.Of(exception.Code, exception.Arguments))]);
        }
    }

    private static PricingProfileResponse Map(PricingProfile profile, IReadOnlyDictionary<Guid, int> counts) => new(
        profile.Id,
        profile.Name,
        profile.IsDefault,
        profile.BwPricePerSide.Amount,
        profile.ColorPricePerSide.Amount,
        profile.MinOrder.Amount,
        new Dictionary<string, decimal>(profile.PaperMultipliers, StringComparer.Ordinal),
        [.. profile.VolumeTiers.OrderBy(t => t.MinSides).Select(t => new VolumeTierDto(t.MinSides, t.DiscountPercent))],
        counts.GetValueOrDefault(profile.Id),
        profile.UpdatedAt,
        profile.RowVersion);

    private static string Snapshot(PricingProfile profile) => JsonSerializer.Serialize(new
    {
        profile.Name,
        BwPricePerSide = profile.BwPricePerSide.Amount,
        ColorPricePerSide = profile.ColorPricePerSide.Amount,
        MinOrder = profile.MinOrder.Amount,
        profile.PaperMultipliers,
        VolumeTiers = profile.VolumeTiers.Select(t => new { t.MinSides, t.DiscountPercent }),
    });

    private Task AuditAsync(
        Guid? adminUserId, string action, PricingProfile profile, string? before, string? ip,
        CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminUserId, action, "PricingProfile", profile.Id.ToString(), before, Snapshot(profile), ip, Reason: null),
            cancellationToken);

    private static ValidationException Invalid(string field, string key, params (string Name, object? Value)[] arguments)
        => new([new ValidationError(field, key, arguments)]);
}
