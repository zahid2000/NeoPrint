using System.Globalization;

namespace NeoPrint.Domain.Common;

/// <summary>
/// An amount of money (plan section 2.3, <c>docs/DOMAIN_RULES.md</c>).
/// </summary>
/// <remarks>
/// A raw decimal says nothing about which currency it is in and nothing about
/// how it should be rounded, so both get decided again at every call site -
/// which is how a total ends up a qəpik off what the customer was quoted. This
/// type makes the currency part of the value and puts rounding in exactly one
/// place.
///
/// Rounding is to two decimals, half to even - banker's rounding - because the
/// plan says so (section 2.3). Over many small prints it does not drift the
/// way half-away-from-zero does, since ties go up and down in equal measure.
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>The only currency the plan uses (section 2.3).</summary>
    public const string Azn = "AZN";

    /// <summary>Decimal places money is kept to.</summary>
    public const int Decimals = 2;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>The amount, already rounded.</summary>
    public decimal Amount { get; }

    /// <summary>ISO 4217 code. <c>AZN</c> throughout, for now.</summary>
    public string Currency { get; }

    public static Money Zero => new(0m, Azn);

    /// <summary>An amount in manat, rounded the one way this type rounds.</summary>
    public static Money FromAzn(decimal amount) => new(Round(amount), Azn);

    /// <summary>
    /// An amount in qəpik.
    /// </summary>
    /// <remarks>
    /// Payment providers and fiscal APIs quote minor units, and converting them
    /// by hand at every boundary is how a factor of a hundred gets lost.
    /// </remarks>
    public static Money FromQapik(long qapik) => new(qapik / 100m, Azn);

    /// <summary>The amount in qəpik, for a provider that wants minor units.</summary>
    public long ToQapik() => (long)Round(Amount * 100m, 0);

    /// <summary>
    /// The one rounding rule.
    /// </summary>
    /// <remarks>
    /// Public because a price calculation works in intermediate values - a
    /// per-side price times a paper multiplier - and those have to be rounded
    /// the same way as the total.
    /// </remarks>
    public static decimal Round(decimal amount, int decimals = Decimals)
        => Math.Round(amount, decimals, MidpointRounding.ToEven);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money left, Money right)
    {
        Check(left, right);

        return new Money(Round(left.Amount + right.Amount), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        Check(left, right);

        return new Money(Round(left.Amount - right.Amount), left.Currency);
    }

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    /// <summary>Multiplies by a count or a factor - pages, copies, a discount.</summary>
    public static Money operator *(Money value, decimal factor)
        => new(Round(value.Amount * factor), value.Currency);

    public static Money operator *(decimal factor, Money value) => value * factor;

    public static bool operator <(Money left, Money right)
    {
        Check(left, right);

        return left.Amount < right.Amount;
    }

    public static bool operator >(Money left, Money right)
    {
        Check(left, right);

        return left.Amount > right.Amount;
    }

    public static bool operator <=(Money left, Money right) => !(left > right);

    public static bool operator >=(Money left, Money right) => !(left < right);

    /// <summary>The larger of two amounts, for a minimum order value.</summary>
    public static Money Max(Money left, Money right)
    {
        Check(left, right);

        return left.Amount >= right.Amount ? left : right;
    }

    public static Money Min(Money left, Money right)
    {
        Check(left, right);

        return left.Amount <= right.Amount ? left : right;
    }

    public int CompareTo(Money other)
    {
        Check(this, other);

        return Amount.CompareTo(other.Amount);
    }

    /// <summary>
    /// Refuses to mix currencies.
    /// </summary>
    /// <remarks>
    /// Only AZN exists today, so this cannot fire yet. It is here because the
    /// day a second currency appears, the alternative is a silently wrong
    /// total.
    /// </remarks>
    /// <exception cref="DomainException">The two amounts are in different currencies.</exception>
    private static void Check(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new DomainException(UserMessage.Of(
                "money.currencyMismatch", ("left", left.Currency), ("right", right.Currency)));
        }
    }

    /// <summary>For a log line or a receipt: <c>1.50 AZN</c>.</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {Currency}");
}
