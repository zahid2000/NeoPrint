using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Payments;

/// <summary>What the mock bank holds about one QR payment - its own books, not ours.</summary>
public sealed class MockBankTransaction
{
    /// <summary>The bank's reference, <c>MB-…</c>; our <c>ProviderRef</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public Guid PaymentId { get; set; }

    public long AmountQapik { get; set; }

    public ProviderPaymentState State { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }
}

/// <summary>Failures the mock bank is told to produce (Development only; plan section 6, "Dev").</summary>
public sealed class MockBankBehaviour
{
    public const string SingletonId = "behaviour";

    [BsonId]
    public string Id { get; set; } = SingletonId;

    /// <summary>Creating a QR times out, as a bank that is down does.</summary>
    public bool CreateFails { get; set; }
}

/// <summary>A callback as the mock bank sends it: the shape a real bank's webhook has.</summary>
public sealed record MockBankCallbackBody(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("amountQapik")] long? AmountQapik,
    [property: JsonPropertyName("occurredAt")] DateTime OccurredAt,
    [property: JsonPropertyName("failureCode")] string? FailureCode);

/// <summary>
/// A bank QR provider that behaves like a real one (plan sections 2.9, 6;
/// docs/PAYMENT_FLOWS.md "Mock provider"): it keeps its own transactions,
/// makes <c>MOCKBANK:{paymentIntentId}</c> QR codes, refuses to be paid once
/// cancelled, and tells us what happened through signed callbacks.
/// </summary>
/// <remarks>
/// The callback signature is HMAC-SHA256 over the exact body, keyed with
/// <c>NEOPRINT_BANK_CALLBACK_SECRET</c> from OpenBao (plan section 10: "Mock-da
/// shared secret"). Without the secret no callback is accepted - failing closed.
/// </remarks>
public sealed class MockBankProvider : IBankQrProvider
{
    public const string ProviderName = "MockBank";

    public const string SignatureHeaderName = "X-MockBank-Signature";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MongoSet<MockBankTransaction> _transactions;
    private readonly MongoSet<MockBankBehaviour> _behaviour;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ILogger<MockBankProvider> _logger;

    public MockBankProvider(MongoContext context, IConfiguration configuration, IClock clock, ILogger<MockBankProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        _transactions = context.MockBankTransactions;
        _behaviour = context.MockBankSettings;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    public string Name => ProviderName;

    public string SignatureHeader => SignatureHeaderName;

    public async Task<BankQrIntent> CreateQrAsync(Guid paymentId, Money amount, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var behaviour = await _behaviour.Find(b => b.Id == MockBankBehaviour.SingletonId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (behaviour?.CreateFails == true)
        {
            _logger.LogWarning("The mock bank is set to time out; no QR for payment {PaymentId}.", paymentId);
            throw new PaymentProviderUnavailableException(ProviderName);
        }

        var transaction = new MockBankTransaction
        {
            Id = "MB-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
            PaymentId = paymentId,
            AmountQapik = amount.ToQapik(),
            State = ProviderPaymentState.Pending,
            ExpiresAt = expiresAt,
            CreatedAt = _clock.UtcNow,
        };

        await _transactions.InsertOneAsync(transaction, cancellationToken).ConfigureAwait(false);

        return new BankQrIntent(transaction.Id, $"MOCKBANK:{paymentId}");
    }

    public async Task<ProviderPaymentStatus> GetStatusAsync(string providerRef, CancellationToken cancellationToken = default)
    {
        var transaction = await _transactions.Find(t => t.Id == providerRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return transaction is null
            ? new ProviderPaymentStatus(ProviderPaymentState.Failed, FailureCode: "unknownTransaction")
            : StatusOf(transaction);
    }

    public async Task<ProviderPaymentStatus> CancelAsync(string providerRef, CancellationToken cancellationToken = default)
    {
        // Only an unpaid QR is cancelled; a paid one answers that it was paid.
        await _transactions
            .UpdateOneAsync(
                t => t.Id == providerRef && t.State == ProviderPaymentState.Pending,
                Builders<MockBankTransaction>.Update.Set(t => t.State, ProviderPaymentState.Cancelled),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return await GetStatusAsync(providerRef, cancellationToken).ConfigureAwait(false);
    }

    public ProviderCallback? ReadCallback(ReadOnlySpan<byte> body, string? signature)
    {
        if (Secret() is not { } secret || string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        var expected = Sign(secret, body);

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant())))
        {
            return null;
        }

        MockBankCallbackBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<MockBankCallbackBody>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.EventId) || string.IsNullOrWhiteSpace(parsed.TransactionId))
        {
            return null;
        }

        return new ProviderCallback(
            parsed.EventId,
            parsed.Type,
            parsed.TransactionId,
            parsed.AmountQapik is { } qapik ? Money.FromQapik(qapik) : null,
            DateTime.SpecifyKind(parsed.OccurredAt, DateTimeKind.Utc),
            parsed.FailureCode,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
    }

    /// <summary>The key callbacks are signed with; null when OpenBao does not hold it.</summary>
    internal string? Secret() => _configuration["NEOPRINT_BANK_CALLBACK_SECRET"] is { Length: > 0 } secret ? secret : null;

    /// <summary>HMAC-SHA256 over the exact body, hex: how the mock bank signs a callback.</summary>
    public static string Sign(string secret, ReadOnlySpan<byte> body)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    internal static ProviderPaymentStatus StatusOf(MockBankTransaction transaction)
        => new(transaction.State, Money.FromQapik(transaction.AmountQapik), transaction.PaidAt);

    internal MongoSet<MockBankTransaction> Transactions => _transactions;

    internal MongoSet<MockBankBehaviour> Behaviour => _behaviour;

}
