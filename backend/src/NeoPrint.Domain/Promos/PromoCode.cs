using System.Text.RegularExpressions;

using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Promos;

public enum PromoKind
{
    /// <summary>A percentage of what is left to pay.</summary>
    Percent = 0,

    /// <summary>A fixed amount off, never more than what is left.</summary>
    Fixed = 1,
}

/// <summary>
/// A promo code (plan section 2.7). One per order; applied last - after the
/// profile price, the volume discount and the bonus pages - to what is left.
/// </summary>
/// <remarks>
/// <see cref="UsedCount"/> counts usages held by payments, open or paid; it
/// goes up when a payment claims the code and down when that payment expires,
/// fails or is refunded, so the total limit is never passed - not even by two
/// payments at once (the claim is a conditional increment).
/// </remarks>
public sealed partial class PromoCode : Entity
{
    public const int MaxCodeLength = 32;

    /// <summary>Upper case, as typed on the kiosk's keyboard: letters, digits and dashes.</summary>
    public string Code { get; private set; } = string.Empty;

    public PromoKind Kind { get; private set; }

    /// <summary>Percent (0-100] or AZN, by <see cref="Kind"/>.</summary>
    public decimal Value { get; private set; }

    public DateTime? StartsAt { get; private set; }

    public DateTime? EndsAt { get; private set; }

    /// <summary>Usages in all; null for no limit.</summary>
    public int? TotalLimit { get; private set; }

    /// <summary>Usages per account; anonymous kiosk customers are not counted (plan section 2.7).</summary>
    public int? PerUserLimit { get; private set; }

    /// <summary>The least an order must cost (after bonus pages) for the code to apply.</summary>
    public decimal MinAmount { get; private set; }

    /// <summary>Kiosks it works at; empty for all.</summary>
    public List<Guid> KioskIds { get; private set; } = [];

    /// <summary>Pricing profiles it works with; empty for all.</summary>
    public List<Guid> ProfileIds { get; private set; } = [];

    public bool IsActive { get; private set; } = true;

    public int UsedCount { get; private set; }

    public string? Description { get; private set; }

    public static PromoCode Create(
        string code,
        PromoKind kind,
        decimal value,
        DateTime? startsAt,
        DateTime? endsAt,
        int? totalLimit,
        int? perUserLimit,
        decimal minAmount,
        IEnumerable<Guid>? kioskIds,
        IEnumerable<Guid>? profileIds,
        string? description,
        DateTime now)
    {
        var promo = new PromoCode { Code = Normalize(code), CreatedAt = now };
        promo.Change(kind, value, startsAt, endsAt, totalLimit, perUserLimit, minAmount, kioskIds, profileIds, description);

        return promo;
    }

    public static string Normalize(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException(UserMessage.Of("promos.codeFormat", ("max", MaxCodeLength)));
        }

        return normalized;
    }

    /// <summary>The terms; the code itself never changes, and usages already made keep their discount.</summary>
    public void Change(
        PromoKind kind,
        decimal value,
        DateTime? startsAt,
        DateTime? endsAt,
        int? totalLimit,
        int? perUserLimit,
        decimal minAmount,
        IEnumerable<Guid>? kioskIds,
        IEnumerable<Guid>? profileIds,
        string? description)
    {
        if (kind == PromoKind.Percent ? value is <= 0 or > 100 : value <= 0 || decimal.Round(value, 2) != value)
        {
            throw new DomainException(kind == PromoKind.Percent ? "promos.percentRange" : "promos.amountInvalid");
        }

        if (startsAt is { } start && endsAt is { } end && end <= start)
        {
            throw new DomainException("promos.periodInvalid");
        }

        if (totalLimit is < 1 || perUserLimit is < 1 || minAmount < 0)
        {
            throw new DomainException("promos.limitInvalid");
        }

        Kind = kind;
        Value = value;
        StartsAt = startsAt;
        EndsAt = endsAt;
        TotalLimit = totalLimit;
        PerUserLimit = perUserLimit;
        MinAmount = minAmount;
        KioskIds = kioskIds?.Distinct().ToList() ?? [];
        ProfileIds = profileIds?.Distinct().ToList() ?? [];
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    /// <summary>Why the code does not apply here and now, or null when it does. The total limit is checked when a usage is claimed.</summary>
    public UserMessage? Refusal(DateTime now, Guid? kioskId, Guid? profileId, Money amount)
    {
        if (!IsActive)
        {
            return new UserMessage("promos.inactive");
        }

        if (StartsAt is { } start && now < start)
        {
            return new UserMessage("promos.notStarted");
        }

        if (EndsAt is { } end && now >= end)
        {
            return new UserMessage("promos.expired");
        }

        if (KioskIds.Count > 0 && (kioskId is null || !KioskIds.Contains(kioskId.Value))
            || ProfileIds.Count > 0 && (profileId is null || !ProfileIds.Contains(profileId.Value)))
        {
            return new UserMessage("promos.notHere");
        }

        if (amount.Amount < MinAmount)
        {
            return UserMessage.Of("promos.belowMinimum", ("min", MinAmount));
        }

        if (TotalLimit is { } limit && UsedCount >= limit)
        {
            return new UserMessage("promos.exhausted");
        }

        return null;
    }

    /// <summary>What comes off <paramref name="amount"/>: a percentage of it, or the fixed sum, never more than all of it.</summary>
    public Money Discount(Money amount)
    {
        if (!amount.IsPositive)
        {
            return Money.Zero;
        }

        var discount = Kind == PromoKind.Percent ? Money.FromAzn(amount.Amount * Value / 100m) : Money.FromAzn(Value);

        return discount > amount ? amount : discount;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{2,31}$")]
    private static partial Regex CodePattern();
}

public enum PromoUsageStatus
{
    /// <summary>Held by a payment that is not paid yet.</summary>
    Reserved = 0,

    /// <summary>The payment went through.</summary>
    Confirmed = 1,

    /// <summary>The payment expired or failed: the code is free again.</summary>
    Released = 2,

    /// <summary>The payment was refunded: the usage is cancelled, the discount is not given back (plan section 2.5).</summary>
    Cancelled = 3,
}

/// <summary>One payment's use of a promo code (plan section 5, <c>promo_usages</c>). One per payment.</summary>
public sealed class PromoUsage : Entity
{
    public Guid PromoCodeId { get; set; }

    public string Code { get; set; } = string.Empty;

    public Guid PaymentId { get; set; }

    public Guid? UserId { get; set; }

    public Money Discount { get; set; }

    public PromoUsageStatus Status { get; private set; } = PromoUsageStatus.Reserved;

    public DateTime? ClosedAt { get; private set; }

    /// <summary>Whether it still takes a place in the code's limits.</summary>
    public bool Holds => Status is PromoUsageStatus.Reserved or PromoUsageStatus.Confirmed;

    /// <summary>The payment went through; also after the usage was given back, when the payment was paid late.</summary>
    public void Confirm()
    {
        if (Status is PromoUsageStatus.Reserved or PromoUsageStatus.Released)
        {
            Status = PromoUsageStatus.Confirmed;
            ClosedAt = null;
        }
    }

    /// <summary>Holds a place again for the same payment's new try, with what it takes off now.</summary>
    public void Hold(Guid promoCodeId, string code, Guid? userId, Money discount)
    {
        PromoCodeId = promoCodeId;
        Code = code;
        UserId = userId;
        Discount = discount;
        Status = PromoUsageStatus.Reserved;
        ClosedAt = null;
    }

    /// <returns>True when the usage gave its place back now.</returns>
    public bool Release(bool refunded, DateTime now)
    {
        if (!Holds)
        {
            return false;
        }

        Status = refunded ? PromoUsageStatus.Cancelled : PromoUsageStatus.Released;
        ClosedAt = now;
        return true;
    }
}
