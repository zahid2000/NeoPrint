using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;

namespace NeoPrint.Application.Payments;

/// <summary>The <c>payments</c> collection (plan section 5).</summary>
public interface IPaymentRepository
{
    Task<Payment?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Payment?> FindByProviderRefAsync(string provider, string providerRef, CancellationToken cancellationToken = default);

    /// <summary>Every payment made for a job, newest first.</summary>
    Task<IReadOnlyList<Payment>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task AddAsync(Payment payment, CancellationToken cancellationToken = default);

    /// <exception cref="ConcurrencyException">Somebody else saved it first.</exception>
    Task UpdateAsync(Payment payment, CancellationToken cancellationToken = default);

    /// <summary>Pending intents whose time ran out, oldest first.</summary>
    Task<IReadOnlyList<Payment>> ListOverdueAsync(DateTime now, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Payments whose outcome the provider may know better than we do:
    /// expired or still pending past <paramref name="silentBefore"/>, created
    /// after <paramref name="notBefore"/>, and not yet reconciled.
    /// </summary>
    Task<IReadOnlyList<Payment>> ListToReconcileAsync(
        DateTime silentBefore, DateTime notBefore, int take, CancellationToken cancellationToken = default);
}

/// <summary>What the bank says about a transaction.</summary>
public enum ProviderPaymentState
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,

    /// <summary>We cancelled the intent before anyone paid; it can no longer be paid.</summary>
    Cancelled = 3,
}

/// <param name="ProviderRef">The provider's id for the transaction.</param>
/// <param name="QrContent">What the kiosk shows (plan section 2.9: <c>MOCKBANK:{paymentIntentId}</c> for the mock).</param>
public sealed record BankQrIntent(string ProviderRef, string QrContent);

/// <param name="Amount">What the provider says it took; checked against the payment.</param>
public sealed record ProviderPaymentStatus(ProviderPaymentState State, Money? Amount = null, DateTime? PaidAt = null, string? FailureCode = null);

/// <summary>A callback the provider signed, read and verified.</summary>
/// <param name="EventId">The provider's id for this delivery; the same event resent has the same id.</param>
/// <param name="Type"><c>payment.succeeded</c>, <c>payment.failed</c>, <c>refund.succeeded</c>, <c>refund.failed</c>.</param>
public sealed record ProviderCallback(
    string EventId,
    string Type,
    string ProviderRef,
    Money? Amount,
    DateTime OccurredAt,
    string? FailureCode,
    string PayloadHash);

/// <summary>The provider could not be reached or did not answer in time.</summary>
public sealed class PaymentProviderUnavailableException : Exception
{
    public PaymentProviderUnavailableException(string provider, Exception? inner = null)
        : base($"The payment provider {provider} did not answer.", inner)
        => Provider = provider;

    public string Provider { get; }
}

/// <summary>
/// A bank that takes payment by QR from its own app (plan section 2.9,
/// <c>IBankQrProvider</c>). External, so always behind this interface.
/// </summary>
public interface IBankQrProvider
{
    /// <summary>The name stored with a payment and used in the callback URL, for example <c>MockBank</c>.</summary>
    string Name { get; }

    /// <summary>The HTTP header the provider puts its callback signature in.</summary>
    string SignatureHeader { get; }

    /// <exception cref="PaymentProviderUnavailableException">The bank did not answer.</exception>
    Task<BankQrIntent> CreateQrAsync(Guid paymentId, Money amount, DateTime expiresAt, CancellationToken cancellationToken = default);

    /// <exception cref="PaymentProviderUnavailableException">The bank did not answer.</exception>
    Task<ProviderPaymentStatus> GetStatusAsync(string providerRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes an unpaid QR unpayable. Answers what the transaction is
    /// afterwards: a customer may have paid a moment before.
    /// </summary>
    Task<ProviderPaymentStatus> CancelAsync(string providerRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a callback's signature and reads it.
    /// </summary>
    /// <returns>Null when the signature does not hold: the callback is not from the provider.</returns>
    ProviderCallback? ReadCallback(ReadOnlySpan<byte> body, string? signature);
}

/// <summary>The bank QR providers the host has, by name.</summary>
public interface IPaymentProviders
{
    IBankQrProvider BankQr { get; }

    IBankQrProvider? Find(string name);
}

/// <summary>Real-time payment messages to a kiosk (plan section 6, <c>/hubs/kiosk</c>).</summary>
public interface IPaymentNotifier
{
    /// <summary>The kiosk's customer paid; the kiosk moves on to the receipt screen.</summary>
    Task PaymentConfirmedAsync(Guid kioskId, Guid paymentId, Guid jobId, CancellationToken cancellationToken = default);
}
