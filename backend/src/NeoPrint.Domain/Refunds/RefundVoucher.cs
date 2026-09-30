using System.Security.Cryptography;

using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Refunds;

/// <summary>
/// A refund an anonymous customer can take away when the bank cannot return
/// it (plan section 2.5: <c>RF-XXXX-XXXX</c>, QR <c>neoprint://voucher/{code}</c>).
/// Redeemed into a wallet, where it follows the wallet's rules (plan 2.14).
/// </summary>
public sealed class RefundVoucher : Entity
{
    /// <summary>Letters that cannot be misread for one another on a screen or a receipt.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string Code { get; set; } = string.Empty;

    public Money Amount { get; set; }

    public Guid PaymentId { get; set; }

    public Guid RefundId { get; set; }

    public DateTime ExpiresAt { get; set; }

    public Guid? RedeemedBy { get; private set; }

    public DateTime? RedeemedAt { get; private set; }

    public static RefundVoucher Issue(Guid paymentId, Guid refundId, Money amount, DateTime now, int validDays)
        => new()
        {
            Code = NewCode(),
            Amount = amount,
            PaymentId = paymentId,
            RefundId = refundId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, validDays)),
        };

    /// <exception cref="DomainException">Already used or expired.</exception>
    public void Redeem(Guid userId, DateTime now)
    {
        if (RedeemedBy is not null)
        {
            throw new DomainException(RedeemedBy == userId ? "vouchers.alreadyYours" : "vouchers.alreadyUsed");
        }

        if (now > ExpiresAt)
        {
            throw new DomainException("vouchers.expired");
        }

        RedeemedBy = userId;
        RedeemedAt = now;
    }

    /// <summary><c>RF-XXXX-XXXX</c>, 32 symbols per place: about a trillion codes.</summary>
    public static string NewCode()
    {
        Span<char> code = stackalloc char[8];

        for (var i = 0; i < code.Length; i++)
        {
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"RF-{code[..4]}-{code[4..]}";
    }
}
