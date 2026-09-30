using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Payments;

/// <summary>A refund as the mock bank or card acquirer holds it, keyed by our refund's id.</summary>
public sealed class MockRefund
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string ProviderRef { get; set; } = string.Empty;

    public long AmountQapik { get; set; }

    public ProviderRefundState State { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Refunds for both mock providers (plan section 2.5; docs/PAYMENT_FLOWS.md
/// "Mock provider"): keyed by our refund, so asking again never pays twice;
/// they fail on request (<c>POST /api/dev/refunds/{id}/simulate-fail</c>,
/// <c>POST /api/dev/mock-bank/refunds-fail</c>), and they refuse to give back
/// more than the transaction took.
/// </summary>
public sealed class MockRefunds
{
    private readonly MongoSet<MockRefund> _refunds;
    private readonly MongoSet<MockBankBehaviour> _behaviour;
    private readonly IClock _clock;

    public MockRefunds(MongoContext context, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);

        _refunds = context.MockRefunds;
        _behaviour = context.MockBankSettings;
        _clock = clock;
    }

    public async Task<ProviderRefundResult> RefundAsync(
        string provider, string providerRef, string refundKey, Money amount, long takenQapik, CancellationToken cancellationToken)
    {
        var existing = await _refunds.Find(r => r.Id == refundKey).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing is { State: ProviderRefundState.Succeeded })
        {
            // The same refund again: already paid out, not paid twice.
            return new ProviderRefundResult(ProviderRefundState.Succeeded, $"MR-{refundKey}");
        }

        var behaviour = await _behaviour.Find(b => b.Id == MockBankBehaviour.SingletonId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var refundedBefore = await _refunds
            .Find(r => r.Provider == provider && r.ProviderRef == providerRef && r.State == ProviderRefundState.Succeeded && r.Id != refundKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var failCode = behaviour?.RefundsFail == true || behaviour?.FailingRefunds.Contains(refundKey) == true
            ? "bankRefused"
            : takenQapik <= 0
                ? "notPaid"
                : refundedBefore.Sum(r => r.AmountQapik) + amount.ToQapik() > takenQapik
                    ? "moreThanTaken"
                    : null;

        var state = failCode is null ? ProviderRefundState.Succeeded : ProviderRefundState.Failed;

        await _refunds
            .UpdateOneAsync(
                Builders<MockRefund>.Filter.Eq(r => r.Id, refundKey),
                Builders<MockRefund>.Update
                    .Set(r => r.Provider, provider)
                    .Set(r => r.ProviderRef, providerRef)
                    .Set(r => r.AmountQapik, amount.ToQapik())
                    .Set(r => r.State, state)
                    .Set(r => r.UpdatedAt, _clock.UtcNow),
                new UpdateOptions { IsUpsert = true },
                cancellationToken)
            .ConfigureAwait(false);

        return new ProviderRefundResult(state, state == ProviderRefundState.Succeeded ? $"MR-{refundKey}" : null, failCode);
    }

    public async Task<ProviderRefundResult> GetRefundStatusAsync(string refundKey, CancellationToken cancellationToken)
    {
        var refund = await _refunds.Find(r => r.Id == refundKey).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return refund is null
            ? new ProviderRefundResult(ProviderRefundState.Failed, FailureCode: "unknownRefund")
            : new ProviderRefundResult(refund.State, refund.State == ProviderRefundState.Succeeded ? $"MR-{refundKey}" : null);
    }

    /// <summary>Makes every refund fail until turned off (a bank that refuses refunds), or not.</summary>
    public Task SetAllFailAsync(bool fails, CancellationToken cancellationToken)
        => _behaviour.UpdateOneAsync(
            Builders<MockBankBehaviour>.Filter.Eq(b => b.Id, MockBankBehaviour.SingletonId),
            Builders<MockBankBehaviour>.Update.Set(b => b.RefundsFail, fails),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    /// <summary>Makes this one refund fail on every try (plan section 6: <c>dev/refunds/{id}/simulate-fail</c>).</summary>
    public Task FailAsync(string refundKey, CancellationToken cancellationToken)
        => _behaviour.UpdateOneAsync(
            Builders<MockBankBehaviour>.Filter.Eq(b => b.Id, MockBankBehaviour.SingletonId),
            Builders<MockBankBehaviour>.Update.AddToSet(b => b.FailingRefunds, refundKey),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
}
