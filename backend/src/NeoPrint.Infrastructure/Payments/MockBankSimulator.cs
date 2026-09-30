using System.Text;
using System.Text.Json;

using Hangfire;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;

namespace NeoPrint.Infrastructure.Payments;

/// <summary>How a simulated customer payment reaches us.</summary>
/// <param name="DelaySeconds">The callback comes this much later - after expiry, if long enough.</param>
/// <param name="Duplicate">The callback arrives twice, at the same moment.</param>
/// <param name="DropCallback">The callback is lost; only the reconciliation job can learn of the payment.</param>
public sealed record MockPaymentOptions(int DelaySeconds = 0, bool Duplicate = false, bool DropCallback = false);

/// <summary>What happened at the mock bank.</summary>
public enum MockBankOutcome
{
    /// <summary>The bank took the money (or refused it) and will tell us as asked.</summary>
    Done = 0,

    /// <summary>No such QR at the bank.</summary>
    Unknown = 1,

    /// <summary>The QR was cancelled when the intent expired; a bank app refuses to pay it.</summary>
    QrNoLongerValid = 2,

    /// <summary><c>NEOPRINT_BANK_CALLBACK_SECRET</c> is not in OpenBao: callbacks cannot be signed.</summary>
    NoCallbackSecret = 3,
}

/// <summary>
/// Plays the customer and the bank in Development (plan section 6, "Dev":
/// <c>simulate-success</c>, <c>simulate-fail</c>): the customer pays or is
/// declined in their bank app, and the bank sends its signed callback - on
/// time, late, twice, or never.
/// </summary>
/// <remarks>
/// The callback goes over HTTP to the API's own callback endpoint, so the
/// signature check, the <c>provider_events</c> guard and the handler are the
/// same path a real bank's webhook takes. Registered in Development only.
/// </remarks>
public sealed class MockBankSimulator
{
    public const string HttpClientName = "mock-bank-callback";

    private readonly MockBankProvider _bank;
    private readonly IHttpClientFactory _http;
    private readonly IBackgroundJobClient _jobs;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ILogger<MockBankSimulator> _logger;

    public MockBankSimulator(
        MockBankProvider bank,
        IHttpClientFactory http,
        IBackgroundJobClient jobs,
        IConfiguration configuration,
        IClock clock,
        ILogger<MockBankSimulator> logger)
    {
        _bank = bank;
        _http = http;
        _jobs = jobs;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The customer pays the QR of this payment in their bank app.</summary>
    public Task<MockBankOutcome> PayAsync(Guid paymentId, MockPaymentOptions options, CancellationToken cancellationToken = default)
        => SettleAsync(paymentId, ProviderPaymentState.Succeeded, ProviderCallbackTypes.PaymentSucceeded, options, cancellationToken);

    /// <summary>The customer's bank declines it (no funds, a blocked card).</summary>
    public Task<MockBankOutcome> DeclineAsync(Guid paymentId, CancellationToken cancellationToken = default)
        => SettleAsync(paymentId, ProviderPaymentState.Failed, ProviderCallbackTypes.PaymentFailed, new MockPaymentOptions(), cancellationToken);

    /// <summary>Makes creating a QR time out, as a bank that is down does, or stops it.</summary>
    public Task SetCreateFailsAsync(bool fails, CancellationToken cancellationToken = default)
        => _bank.Behaviour.UpdateOneAsync(
            Builders<MockBankBehaviour>.Filter.Eq(b => b.Id, MockBankBehaviour.SingletonId),
            Builders<MockBankBehaviour>.Update.Set(b => b.CreateFails, fails),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    /// <summary>Sends the callback for a settled transaction (a delayed callback runs this from Hangfire).</summary>
    public async Task SendCallbackAsync(string transactionId, string type, CancellationToken cancellationToken = default)
    {
        var transaction = await _bank.Transactions.Find(t => t.Id == transactionId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is null || _bank.Secret() is not { } secret)
        {
            return;
        }

        var body = BodyFor(transaction, type);
        await PostAsync(body, secret, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MockBankOutcome> SettleAsync(
        Guid paymentId, ProviderPaymentState state, string type, MockPaymentOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_bank.Secret() is not { } secret)
        {
            return MockBankOutcome.NoCallbackSecret;
        }

        var transaction = await _bank.Transactions.Find(t => t.PaymentId == paymentId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is null)
        {
            return MockBankOutcome.Unknown;
        }

        // Pending → paid or declined, once; a transaction already in that state
        // is sent again, which is what a bank that did not hear our 200 does.
        var update = Builders<MockBankTransaction>.Update.Set(t => t.State, state);

        if (state == ProviderPaymentState.Succeeded)
        {
            update = update.Set(t => t.PaidAt, _clock.UtcNow);
        }

        await _bank.Transactions
            .UpdateOneAsync(t => t.Id == transaction.Id && t.State == ProviderPaymentState.Pending, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        transaction = await _bank.Transactions.Find(t => t.Id == transaction.Id).FirstAsync(cancellationToken).ConfigureAwait(false);

        if (transaction.State == ProviderPaymentState.Cancelled)
        {
            return MockBankOutcome.QrNoLongerValid;
        }

        if (transaction.State != state)
        {
            return MockBankOutcome.Unknown;
        }

        if (options.DropCallback)
        {
            _logger.LogInformation("Mock bank: {Type} for {Transaction}, callback dropped on purpose.", type, transaction.Id);
            return MockBankOutcome.Done;
        }

        if (options.DelaySeconds > 0)
        {
            _jobs.Schedule<MockBankSimulator>(
                simulator => simulator.SendCallbackAsync(transaction.Id, type, CancellationToken.None),
                TimeSpan.FromSeconds(options.DelaySeconds));

            _logger.LogInformation("Mock bank: {Type} for {Transaction}, callback in {Delay} s.", type, transaction.Id, options.DelaySeconds);
            return MockBankOutcome.Done;
        }

        var body = BodyFor(transaction, type);

        if (options.Duplicate)
        {
            // The same delivery twice, at the same moment: one must win, one must be a no-op.
            await Task.WhenAll(PostAsync(body, secret, cancellationToken), PostAsync(body, secret, cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            await PostAsync(body, secret, cancellationToken).ConfigureAwait(false);
        }

        return MockBankOutcome.Done;
    }

    /// <summary>
    /// The event id is fixed per transaction and type, as a real bank's is:
    /// a resent webhook carries the id of the first.
    /// </summary>
    private static byte[] BodyFor(MockBankTransaction transaction, string type)
        => JsonSerializer.SerializeToUtf8Bytes(
            new MockBankCallbackBody(
                $"MBE-{transaction.Id}-{type}",
                type,
                transaction.Id,
                type == ProviderCallbackTypes.PaymentSucceeded ? transaction.AmountQapik : null,
                transaction.PaidAt ?? DateTime.UtcNow,
                type == ProviderCallbackTypes.PaymentFailed ? "insufficientFunds" : null),
            MockBankProvider.Json);

    private async Task PostAsync(byte[] body, string secret, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(HttpClientName);
        var baseUrl = _configuration["Payments:MockBank:CallbackBaseUrl"] is { Length: > 0 } configured ? configured : "http://localhost:8080";

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = Encoding.UTF8.WebName };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "api/payments/callback/mockbank")) { Content = content };
        request.Headers.Add(MockBankProvider.SignatureHeaderName, MockBankProvider.Sign(secret, body));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Mock bank callback answered {Status}.", (int)response.StatusCode);
    }
}
