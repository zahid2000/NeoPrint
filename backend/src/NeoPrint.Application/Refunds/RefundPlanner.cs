using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Application.Refunds;

/// <summary>
/// Plans refunds for a decision (plan section 2.5): what each part of the
/// payment - bank or card, wallet, bonus pages - can still give back, where it
/// goes, and one refund per part with its own dedup key. Runs inside the
/// caller's transaction, so an admin's refund is counted before the next one
/// is asked for.
/// </summary>
public sealed class RefundPlanner
{
    private readonly IRefundRepository _refunds;
    private readonly IPaymentRepository _payments;
    private readonly IClock _clock;
    private readonly ILogger<RefundPlanner> _logger;

    public RefundPlanner(IRefundRepository refunds, IPaymentRepository payments, IClock clock, ILogger<RefundPlanner> logger)
    {
        _refunds = refunds;
        _payments = payments;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Turns a decision into refunds, one per part; the same decision twice makes none the second time.</summary>
    public async Task<IReadOnlyList<Guid>> PlanAsync(RefundRequested decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var payment = await _payments.FindByIdAsync(decision.PaymentId, cancellationToken).ConfigureAwait(false);

        if (payment is not { WasPaid: true, Parts: { } parts })
        {
            _logger.LogWarning("Refund asked for payment {PaymentId}, which was never paid; nothing to refund.", decision.PaymentId);
            return [];
        }

        if (payment.Purpose == PaymentPurpose.WalletTopUp)
        {
            // Money put on a wallet is never paid back (plan section 2.14).
            _logger.LogWarning("Refund asked for wallet top-up {PaymentId}; top-ups are not refunded.", payment.Id);
            return [];
        }

        var reason = Enum.TryParse<RefundReason>(decision.Reason, out var parsed) ? parsed : RefundReason.SystemError;
        var existing = await _refunds.ListForPaymentAsync(payment.Id, cancellationToken).ConfigureAwait(false);

        // What each part can still give back: all refunds count, whatever their
        // state - a failed one is still owed and may be retried.
        var external = parts.External - Sum(existing, RefundSource.External);
        var wallet = parts.Wallet - Sum(existing, RefundSource.Wallet);
        var bonus = parts.BonusPages - existing.Where(r => r.Source == RefundSource.Bonus).Sum(r => r.BonusPages);

        var available = external + wallet;
        var asked = decision.Amount is { } amount ? Money.FromAzn(amount) : available;

        if (asked > available)
        {
            asked = available;
        }

        var fromExternal = asked > external ? external : asked;
        var fromWallet = asked - fromExternal;
        var bonusBack = decision.Amount is null
            ? bonus
            : decision.Evidence is { OrderedSides: > 0 } evidence
                ? (int)Math.Ceiling(bonus * (decimal)(evidence.OrderedSides - evidence.PrintedSides) / evidence.OrderedSides)
                : 0;

        var destination = ExternalDestination(payment, reason, decision.Destination);
        var initiatedBy = Enum.TryParse<ActorType>(decision.InitiatedBy, out var actorType) ? actorType : ActorType.System;
        var evidenceValue = decision.Evidence is { } e && Enum.TryParse<EvidenceSource>(e.Source, out var source)
            ? new RefundEvidence(source, e.PrintedSides, e.OrderedSides, e.Detail)
            : null;
        var now = _clock.UtcNow;
        var created = new List<Guid>();

        async Task AddAsync(RefundSource from, Money money, int pages, RefundDestination to, string suffix)
        {
            if (!money.IsPositive && pages <= 0)
            {
                return;
            }

            var key = $"{decision.DedupKey}:{suffix}";

            if (await _refunds.FindByDedupKeyAsync(key, cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }

            var refund = Refund.Create(
                payment.Id, payment.JobId, payment.UserId, money, pages, from, to, reason, initiatedBy, decision.InitiatorId, key, now, decision.Note, evidenceValue);

            await _refunds.AddAsync(refund, cancellationToken).ConfigureAwait(false);
            created.Add(refund.Id);
        }

        await AddAsync(RefundSource.External, fromExternal, 0, destination, "external").ConfigureAwait(false);
        await AddAsync(RefundSource.Wallet, fromWallet, 0, RefundDestination.Wallet, "wallet").ConfigureAwait(false);
        await AddAsync(RefundSource.Bonus, Money.Zero, bonusBack, RefundDestination.Bonus, "bonus").ConfigureAwait(false);

        if (created.Count > 0)
        {
            payment.Note(now, "refundPlanned", $"{reason}: {asked.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Refund decision {DedupKey} for payment {PaymentId}: {Count} refund(s), {Amount} {Currency} ({Reason}).",
            decision.DedupKey,
            payment.Id,
            created.Count,
            asked.Amount,
            asked.Currency,
            reason);

        return created;
    }

    /// <summary>
    /// Where the bank or card part goes (plan section 2.5, direction matrix):
    /// an anonymous bank payment back to the card; money taken for nothing
    /// (late or duplicate) back to where it came from; a registered user's
    /// card part to the wallet, unless an admin chose the card.
    /// </summary>
    private static RefundDestination ExternalDestination(Payment payment, RefundReason reason, string? chosen)
    {
        if (chosen is not null)
        {
            var destination = Enum.TryParse<RefundDestination>(chosen, out var parsed)
                ? parsed
                : throw new DomainException("refunds.destinationInvalid");

            return AllowedExternal(payment).Contains(destination) ? destination : throw new DomainException("refunds.destinationNotAllowed");
        }

        if (payment.UserId is null || reason is RefundReason.LatePayment or RefundReason.DuplicatePayment)
        {
            return RefundDestination.OriginalMethod;
        }

        return RefundDestination.Wallet;
    }

    /// <summary>
    /// Where an admin may send the bank or card part (plan section 2.5): a
    /// registered user's to their wallet or the card; an anonymous one's to
    /// the card, and to a voucher only when the card cannot take it (a retry
    /// after failure).
    /// </summary>
    public static IReadOnlyList<RefundDestination> AllowedExternal(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return payment.UserId is null
            ? [RefundDestination.OriginalMethod]
            : [RefundDestination.Wallet, RefundDestination.OriginalMethod];
    }

    private static Money Sum(IEnumerable<Refund> refunds, RefundSource source)
        => refunds.Where(r => r.Source == source).Aggregate(Money.Zero, (sum, r) => sum + r.Amount);
}
