using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Features;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Promos;

namespace NeoPrint.Application.Promos;

/// <summary>The <c>promo_codes</c> collection.</summary>
public interface IPromoCodeRepository
{
    Task<PromoCode?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PromoCode?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <returns>False when the code is taken.</returns>
    Task<bool> TryAddAsync(PromoCode promo, CancellationToken cancellationToken = default);

    Task UpdateAsync(PromoCode promo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a place: <c>UsedCount</c> up by one, only while under the total limit.
    /// A conditional update on the code's own document, so two payments at once
    /// cannot both take the last place.
    /// </summary>
    /// <returns>False when the limit is reached.</returns>
    Task<bool> TryTakeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Gives a place back.</summary>
    Task GiveBackAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Takes a place whatever the limit: a payment that went through after its usage was given back (a late callback).</summary>
    Task TakeAnywayAsync(Guid id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PromoCode> Items, long Total)> SearchAsync(string? code, bool? active, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>The <c>promo_usages</c> collection: one per payment.</summary>
public interface IPromoUsageRepository
{
    Task<PromoUsage?> FindByPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default);

    Task AddAsync(PromoUsage usage, CancellationToken cancellationToken = default);

    Task UpdateAsync(PromoUsage usage, CancellationToken cancellationToken = default);

    /// <summary>Usages of a code by an account that still hold a place.</summary>
    Task<long> CountHeldAsync(Guid promoCodeId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Confirmed usages and the discount they gave, for the admin's statistics.</summary>
    Task<(long Count, decimal Discount)> StatsAsync(Guid promoCodeId, CancellationToken cancellationToken = default);
}

/// <summary>What a code gives here and now, or why not.</summary>
public sealed record PromoCheck(PromoCode? Promo, Money Discount, UserMessage? Refusal);

/// <summary>
/// The promo module's operations for the payments module (plan section 2.7):
/// checking a code, claiming a usage for a payment, confirming it when the
/// payment goes through, and giving the place back when it does not or is
/// refunded. Payments never write the promo collections themselves.
/// </summary>
public sealed class PromoService
{
    private readonly IPromoCodeRepository _codes;
    private readonly IPromoUsageRepository _usages;
    private readonly IFeatureFlagEvaluator _features;
    private readonly IClock _clock;
    private readonly ILogger<PromoService> _logger;

    public PromoService(IPromoCodeRepository codes, IPromoUsageRepository usages, IFeatureFlagEvaluator features, IClock clock, ILogger<PromoService> logger)
    {
        _codes = codes;
        _usages = usages;
        _features = features;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The feature flag that switches promo codes off (plan section 2.11).</summary>
    public const string FeatureKey = "promo_codes";

    /// <summary>What the code takes off <paramref name="amount"/> (what is left after bonus pages).</summary>
    /// <param name="userId">Null for an anonymous kiosk customer: no per-account limit.</param>
    public async Task<PromoCheck> CheckAsync(
        string code, Guid? kioskId, Guid? profileId, Money amount, Guid? userId, CancellationToken cancellationToken)
    {
        string normalized;

        try
        {
            normalized = PromoCode.Normalize(code);
        }
        catch (DomainException)
        {
            return new PromoCheck(null, Money.Zero, new UserMessage("promos.notFound"));
        }

        var promo = await _codes.FindByCodeAsync(normalized, cancellationToken).ConfigureAwait(false);

        if (promo is null)
        {
            return new PromoCheck(null, Money.Zero, new UserMessage("promos.notFound"));
        }

        return await CheckAsync(promo, kioskId, profileId, amount, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromoCheck> CheckAsync(
        PromoCode promo, Guid? kioskId, Guid? profileId, Money amount, Guid? userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promo);

        if (!await _features.IsEnabledAsync(FeatureKey, kioskId, userId, cancellationToken).ConfigureAwait(false))
        {
            // Switched off in the panel (plan section 2.11): no code applies anywhere.
            return new PromoCheck(promo, Money.Zero, new UserMessage("promos.disabled"));
        }

        if (promo.Refusal(_clock.UtcNow, kioskId, profileId, amount) is { } refusal)
        {
            return new PromoCheck(promo, Money.Zero, refusal);
        }

        if (userId is { } user && promo.PerUserLimit is { } perUser
            && await _usages.CountHeldAsync(promo.Id, user, cancellationToken).ConfigureAwait(false) >= perUser)
        {
            return new PromoCheck(promo, Money.Zero, new UserMessage("promos.usedUp"));
        }

        return new PromoCheck(promo, promo.Discount(amount), null);
    }

    /// <summary>
    /// Holds a place for the payment, in the caller's transaction. A payment
    /// that already holds one keeps it (a retried request).
    /// </summary>
    /// <exception cref="DomainException">The code does not apply, or its last place went to someone else.</exception>
    public async Task<Money> ClaimAsync(
        Guid promoCodeId, Guid paymentId, Guid? kioskId, Guid? profileId, Money amount, Guid? userId, CancellationToken cancellationToken)
    {
        var existing = await _usages.FindByPaymentAsync(paymentId, cancellationToken).ConfigureAwait(false);

        if (existing is not null && existing.Release(refunded: false, _clock.UtcNow))
        {
            // A new try at the same payment - other bonus pages, another
            // method: its place is taken afresh for what is left to pay now.
            await _codes.GiveBackAsync(existing.PromoCodeId, cancellationToken).ConfigureAwait(false);
        }

        var promo = await _codes.FindByIdAsync(promoCodeId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("promos.notFound");
        var check = await CheckAsync(promo, kioskId, profileId, amount, userId, cancellationToken).ConfigureAwait(false);

        if (check.Refusal is { } refusal)
        {
            throw new DomainException(refusal);
        }

        if (!await _codes.TryTakeAsync(promo.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("promos.exhausted");
        }

        if (existing is null)
        {
            await _usages.AddAsync(
                    new PromoUsage
                    {
                        PromoCodeId = promo.Id,
                        Code = promo.Code,
                        PaymentId = paymentId,
                        UserId = userId,
                        Discount = check.Discount,
                        CreatedAt = _clock.UtcNow,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            existing.Hold(promo.Id, promo.Code, userId, check.Discount);
            await _usages.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Promo {Code} claimed for payment {PaymentId}: {Discount} off.", promo.Code, paymentId, check.Discount.Amount);
        return check.Discount;
    }

    /// <summary>
    /// The payment went through. A usage given back meanwhile (the payment had
    /// expired, then a late callback paid it) holds its place again: the
    /// customer already paid the discounted price.
    /// </summary>
    public async Task ConfirmAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        if (await _usages.FindByPaymentAsync(paymentId, cancellationToken).ConfigureAwait(false) is not { } usage
            || usage.Status is PromoUsageStatus.Confirmed or PromoUsageStatus.Cancelled)
        {
            return;
        }

        if (usage.Status == PromoUsageStatus.Released)
        {
            await _codes.TakeAnywayAsync(usage.PromoCodeId, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Promo {Code} taken again by late-paid payment {PaymentId}, whatever its limit.", usage.Code, paymentId);
        }

        usage.Confirm();
        await _usages.UpdateAsync(usage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The payment expired, failed or was refunded: the place goes back. Idempotent.</summary>
    public async Task ReleaseAsync(Guid paymentId, bool refunded, CancellationToken cancellationToken)
    {
        if (await _usages.FindByPaymentAsync(paymentId, cancellationToken).ConfigureAwait(false) is not { } usage
            || !usage.Release(refunded, _clock.UtcNow))
        {
            return;
        }

        await _usages.UpdateAsync(usage, cancellationToken).ConfigureAwait(false);
        await _codes.GiveBackAsync(usage.PromoCodeId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Promo {Code} given back by payment {PaymentId} ({Reason}).", usage.Code, paymentId, refunded ? "refunded" : "not paid");
    }
}
