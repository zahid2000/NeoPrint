using System.Globalization;

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Fiscal;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Fiscal;

/// <summary>A document as the mock e-kassa holds it, keyed by our receipt's id.</summary>
public sealed class MockFiscalDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public FiscalReceiptType Type { get; set; }

    public string TerminalId { get; set; } = string.Empty;

    public long AmountQapik { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    public string FiscalId { get; set; } = string.Empty;

    public string? OriginalFiscalId { get; set; }

    public DateTime IssuedAt { get; set; }
}

/// <summary>How the mock e-kassa behaves (Development and tests).</summary>
public enum MockFiscalMode
{
    Ok = 0,

    /// <summary>Does not answer: every try is a timeout, retried later.</summary>
    Down = 1,

    /// <summary>Refuses every document for good.</summary>
    Reject = 2,
}

public sealed class MockFiscalBehaviour
{
    public const string SingletonId = "behaviour";

    [BsonId]
    public string Id { get; set; } = SingletonId;

    public MockFiscalMode Mode { get; set; }

    /// <summary>These receipts (by key) time out on every try (<c>POST /api/dev/fiscal/{id}/simulate-fail</c>).</summary>
    public List<string> FailingKeys { get; set; } = [];
}

/// <summary>
/// <c>MockFiscalProvider</c> (plan section 2.12): makes a fiscal number, a
/// fiscal id and a verification link. Keyed by our receipt, so asking again
/// returns the document it already made; it can be down, refuse, or fail
/// one receipt on request.
/// </summary>
public sealed class MockFiscalProvider : IFiscalProvider
{
    public const string ProviderName = "mock";

    private readonly MongoSet<MockFiscalDocument> _documents;
    private readonly MongoSet<MockFiscalBehaviour> _behaviour;
    private readonly IClock _clock;

    public MockFiscalProvider(MongoContext context, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);

        _documents = context.MockFiscalDocuments;
        _behaviour = context.MockFiscalSettings;
        _clock = clock;
    }

    public string Name => ProviderName;

    public Task<FiscalProviderResult> IssueSaleAsync(FiscalRequest request, CancellationToken cancellationToken = default)
        => IssueAsync(request, cancellationToken);

    public Task<FiscalProviderResult> IssueRefundAsync(FiscalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(request.OriginalFiscalId)
            ? Task.FromResult(new FiscalProviderResult(FiscalOutcome.Rejected, FailureCode: "noOriginal"))
            : IssueAsync(request, cancellationToken);
    }

    public async Task<FiscalProviderResult> GetStatusAsync(string key, CancellationToken cancellationToken = default)
    {
        var document = await _documents.Find(d => d.Id == key).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return document is null
            ? new FiscalProviderResult(FiscalOutcome.Unavailable, FailureCode: "unknownDocument")
            : Issued(document);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        => (await BehaviourAsync(cancellationToken).ConfigureAwait(false))?.Mode != MockFiscalMode.Down;

    /// <summary>Down, refusing or answering (Development: <c>POST /api/dev/mock-fiscal/mode</c>).</summary>
    public Task SetModeAsync(MockFiscalMode mode, CancellationToken cancellationToken)
        => _behaviour.UpdateOneAsync(
            Builders<MockFiscalBehaviour>.Filter.Eq(b => b.Id, MockFiscalBehaviour.SingletonId),
            Builders<MockFiscalBehaviour>.Update.Set(b => b.Mode, mode),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    /// <summary>This receipt times out on every try until it is taken off the list.</summary>
    public Task FailAsync(string key, bool fails, CancellationToken cancellationToken)
        => _behaviour.UpdateOneAsync(
            Builders<MockFiscalBehaviour>.Filter.Eq(b => b.Id, MockFiscalBehaviour.SingletonId),
            fails
                ? Builders<MockFiscalBehaviour>.Update.AddToSet(b => b.FailingKeys, key)
                : Builders<MockFiscalBehaviour>.Update.Pull(b => b.FailingKeys, key),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    private async Task<FiscalProviderResult> IssueAsync(FiscalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _documents.Find(d => d.Id == request.Key).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            // The same receipt asked for again: the document already made, not a second one.
            return Issued(existing);
        }

        var behaviour = await BehaviourAsync(cancellationToken).ConfigureAwait(false);

        if (behaviour?.Mode == MockFiscalMode.Down || behaviour?.FailingKeys.Contains(request.Key) == true)
        {
            throw new TimeoutException("The mock e-kassa did not answer.");
        }

        if (behaviour?.Mode == MockFiscalMode.Reject)
        {
            return new FiscalProviderResult(FiscalOutcome.Rejected, FailureCode: "rejected");
        }

        var now = _clock.UtcNow;
        var document = new MockFiscalDocument
        {
            Id = request.Key,
            Type = request.Type,
            TerminalId = request.TerminalId,
            AmountQapik = (long)(request.Amount * 100m),
            // Derived from the key: the same receipt always has the same number.
            DocumentNumber = $"{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{request.Key[..8].ToUpperInvariant()}",
            FiscalId = $"MF{request.Key[..16].ToUpperInvariant()}",
            OriginalFiscalId = request.OriginalFiscalId,
            IssuedAt = now,
        };

        try
        {
            await _documents.InsertOneAsync(document, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two tries at once: the one that got there first made the document.
            return Issued(await _documents.Find(d => d.Id == request.Key).FirstAsync(cancellationToken).ConfigureAwait(false));
        }

        return Issued(document);
    }

    private Task<MockFiscalBehaviour?> BehaviourAsync(CancellationToken cancellationToken)
        => _behaviour.Find(b => b.Id == MockFiscalBehaviour.SingletonId).FirstOrDefaultAsync(cancellationToken)!;

    private static FiscalProviderResult Issued(MockFiscalDocument document)
        => new(FiscalOutcome.Issued, document.DocumentNumber, document.FiscalId, $"https://e-kassa.mock/check/{document.FiscalId}");
}
