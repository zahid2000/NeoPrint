using Microsoft.Extensions.Logging;

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Payments;

/// <summary>What the mock card acquirer holds about one charge.</summary>
public sealed class MockCardCharge
{
    /// <summary>Our reference, which is also the acquirer's key for the charge.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long AmountQapik { get; set; }

    public ProviderPaymentState State { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>How the next card charges end (Development: <c>POST /api/dev/mock-card/outcome</c>).</summary>
public enum MockCardOutcome
{
    Success = 0,

    /// <summary>The issuer declines: nothing is taken.</summary>
    Decline = 1,

    /// <summary>The money is taken but the answer is lost on the way back.</summary>
    Timeout = 2,
}

/// <summary>
/// A card acquirer for the mobile app that behaves like a real one (plan
/// section 8.2, "Kart (mock)"; docs/PAYMENT_FLOWS.md "Mock provider"): a
/// charge is keyed by our reference, so asking again never takes the money
/// twice, and it can decline or lose its answer on request.
/// </summary>
public sealed class MockCardProvider : ICardPaymentProvider
{
    public const string ProviderName = "MockCard";

    private readonly MongoSet<MockCardCharge> _charges;
    private readonly MongoSet<MockBankBehaviour> _behaviour;
    private readonly IClock _clock;
    private readonly ILogger<MockCardProvider> _logger;

    public MockCardProvider(MongoContext context, IClock clock, ILogger<MockCardProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        _charges = context.MockCardCharges;
        _behaviour = context.MockBankSettings;
        _clock = clock;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<ProviderPaymentStatus> ChargeAsync(string providerRef, Money amount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRef);

        if (await _charges.Find(c => c.Id == providerRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } earlier)
        {
            // The same reference again: the first charge's answer, nothing new taken.
            return StatusOf(earlier);
        }

        var behaviour = await _behaviour.Find(b => b.Id == MockBankBehaviour.SingletonId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var outcome = behaviour?.CardOutcome ?? MockCardOutcome.Success;

        var charge = new MockCardCharge
        {
            Id = providerRef,
            AmountQapik = amount.ToQapik(),
            State = outcome == MockCardOutcome.Decline ? ProviderPaymentState.Failed : ProviderPaymentState.Succeeded,
            CreatedAt = _clock.UtcNow,
        };

        await _charges.InsertOneAsync(charge, cancellationToken).ConfigureAwait(false);

        if (outcome == MockCardOutcome.Timeout)
        {
            _logger.LogWarning("Mock card: charge {Reference} taken but the answer is lost, as asked.", providerRef);
            throw new PaymentOutcomeUnknownException(ProviderName);
        }

        return StatusOf(charge);
    }

    public async Task<ProviderPaymentStatus> GetStatusAsync(string providerRef, CancellationToken cancellationToken = default)
    {
        var charge = await _charges.Find(c => c.Id == providerRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // A charge the acquirer never saw was never taken.
        return charge is null ? new ProviderPaymentStatus(ProviderPaymentState.Failed, FailureCode: "unknownCharge") : StatusOf(charge);
    }

    /// <summary>Sets how the next charges end.</summary>
    public Task SetOutcomeAsync(MockCardOutcome outcome, CancellationToken cancellationToken = default)
        => _behaviour.UpdateOneAsync(
            Builders<MockBankBehaviour>.Filter.Eq(b => b.Id, MockBankBehaviour.SingletonId),
            Builders<MockBankBehaviour>.Update.Set(b => b.CardOutcome, outcome),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    private static ProviderPaymentStatus StatusOf(MockCardCharge charge)
        => new(charge.State, Money.FromQapik(charge.AmountQapik), charge.State == ProviderPaymentState.Succeeded ? charge.CreatedAt : null);
}
