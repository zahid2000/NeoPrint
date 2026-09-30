using MediatR;

using NeoPrint.Application.Behaviors;
using NeoPrint.Domain.Pricing;
using NeoPrint.Pricing;

namespace NeoPrint.Application.Pricing;

/// <summary>Reading and writing pricing profiles.</summary>
public interface IPricingProfileRepository
{
    Task<IReadOnlyList<PricingProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<PricingProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The profile every kiosk without one of its own falls back to.</summary>
    Task<PricingProfile?> FindDefaultAsync(CancellationToken cancellationToken = default);

    Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default);

    Task AddAsync(PricingProfile profile, CancellationToken cancellationToken = default);

    Task UpdateAsync(PricingProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>How many kiosks point at each profile, by profile id.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountKiosksAsync(CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Shapes
// ---------------------------------------------------------------------------

public sealed record VolumeTierDto(int MinSides, decimal DiscountPercent);

/// <summary>A profile as the panel edits it.</summary>
public sealed record PricingProfileInput(
    string Name,
    decimal BwPricePerSide,
    decimal ColorPricePerSide,
    decimal MinOrder,
    IReadOnlyDictionary<string, decimal> PaperMultipliers,
    IReadOnlyList<VolumeTierDto> VolumeTiers);

public sealed record PricingProfileResponse(
    Guid Id,
    string Name,
    bool IsDefault,
    decimal BwPricePerSide,
    decimal ColorPricePerSide,
    decimal MinOrder,
    IReadOnlyDictionary<string, decimal> PaperMultipliers,
    IReadOnlyList<VolumeTierDto> VolumeTiers,
    int KioskCount,
    DateTime UpdatedAt,
    int Revision);

/// <summary>A kiosk as the profile page lists it.</summary>
public sealed record PricingKioskDto(Guid Id, string Code, string Name, Guid? PricingProfileId);

public sealed record PricingProfileDetail(PricingProfileResponse Profile, IReadOnlyList<PricingKioskDto> Kiosks);

/// <summary>What is being printed, as a request carries it.</summary>
/// <param name="PageCount">Pages selected for printing.</param>
public sealed record PrintOptionsDto(
    int PageCount,
    int Copies = 1,
    bool Duplex = false,
    bool Color = false,
    string Paper = "A4",
    int PagesPerSheet = 1)
{
    public PrintSpec ToSpec() => new(PageCount, PagesPerSheet, Copies, Duplex, Color, Paper);
}

/// <summary>A quote: the price, how it was arrived at, and which profile produced it.</summary>
/// <param name="AmountDue">What is paid: the price less the promo code, before any bonus pages.</param>
/// <param name="PromoRefusal">Why the code does not apply, in the caller's language.</param>
public sealed record QuoteResponse(
    Guid ProfileId,
    string ProfileName,
    PriceBreakdown Price,
    string? PromoCode = null,
    decimal PromoDiscount = 0,
    decimal AmountDue = 0,
    string? PromoRefusal = null);

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/// <summary>
/// Prices an order at a kiosk (plan section 6, <c>POST pricing/quote</c>).
/// </summary>
/// <param name="KioskId">
/// The kiosk: its profile decides the rates. Null prices against the default
/// profile, which is what the mobile app shows before a kiosk is chosen.
/// </param>
/// <param name="PromoCode">Accepted now; applied from step 4, where promo codes arrive.</param>
/// <param name="UseBonus">Accepted now; applied from step 4, where bonus balances arrive.</param>
public sealed record QuoteQuery(Guid? KioskId, PrintOptionsDto Options, string? PromoCode, bool UseBonus)
    : IRequest<QuoteResponse>;

public sealed record ListPricingProfilesQuery : IRequest<IReadOnlyList<PricingProfileResponse>>;

public sealed record GetPricingProfileQuery(Guid Id) : IRequest<PricingProfileDetail>;

public sealed record CreatePricingProfileCommand(PricingProfileInput Input, Guid? AdminUserId, string? Ip)
    : IRequest<PricingProfileResponse>, ITransactionalRequest;

public sealed record UpdatePricingProfileCommand(Guid Id, PricingProfileInput Input, Guid? AdminUserId, string? Ip)
    : IRequest<PricingProfileResponse>, ITransactionalRequest;

public sealed record DeletePricingProfileCommand(Guid Id, Guid? AdminUserId, string? Ip)
    : IRequest, ITransactionalRequest;

public sealed record MakeDefaultPricingProfileCommand(Guid Id, Guid? AdminUserId, string? Ip)
    : IRequest<PricingProfileResponse>, ITransactionalRequest;

/// <summary>Points these kiosks at the profile (plan section 2.3: several at once).</summary>
public sealed record AssignKiosksToProfileCommand(
    Guid Id, IReadOnlyList<Guid> KioskIds, Guid? AdminUserId, string? Ip)
    : IRequest<PricingProfileDetail>, ITransactionalRequest;

/// <summary>
/// The calculator on the profile page: a saved profile, or unsaved values
/// still on the form.
/// </summary>
public sealed record CalculatePriceQuery(Guid? ProfileId, PricingProfileInput? Draft, PrintOptionsDto Options)
    : IRequest<PriceBreakdown>;
