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

    /// <summary>The admin list, newest first (plan section 8A.3, "Ödənişlər").</summary>
    Task<(IReadOnlyList<Payment> Items, long Total)> SearchAsync(
        PaymentFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Succeeded money in a period, for the revenue figures (plan section 8A.3, Dashboard).</summary>
    Task<IReadOnlyList<Payment>> ListPaidAsync(
        DateTime from, DateTime to, IReadOnlyCollection<Guid>? kioskIds, CancellationToken cancellationToken = default);

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

/// <summary>
/// The provider did not answer after the money may already have been taken:
/// the outcome is unknown until reconciliation asks again. Unlike
/// <see cref="PaymentProviderUnavailableException"/>, the customer must not be
/// told that nothing was charged.
/// </summary>
public sealed class PaymentOutcomeUnknownException : Exception
{
    public PaymentOutcomeUnknownException(string provider)
        : base($"The payment provider {provider} did not say whether the charge went through.")
        => Provider = provider;

    public string Provider { get; }
}

/// <summary>The provider could not be reached or did not answer in time.</summary>
public sealed class PaymentProviderUnavailableException : Exception
{
    public PaymentProviderUnavailableException(string provider, Exception? inner = null)
        : base($"The payment provider {provider} did not answer.", inner)
        => Provider = provider;

    public string Provider { get; }
}

public enum ProviderRefundState
{
    Succeeded = 0,
    Failed = 1,

    /// <summary>Accepted, not finished; <c>RefundStatusPollingJob</c> asks again.</summary>
    Pending = 2,
}

/// <param name="RefundRef">The provider's reference for the refund.</param>
public sealed record ProviderRefundResult(ProviderRefundState State, string? RefundRef = null, string? FailureCode = null);

/// <summary>
/// A provider that can give money back to where it came from (plan section
/// 2.5: <c>RefundAsync</c>). Keyed by our refund's id: asking again after a
/// lost answer returns the first result and never refunds twice.
/// </summary>
public interface IRefundingProvider
{
    string Name { get; }

    /// <exception cref="PaymentProviderUnavailableException">No answer: the refund stays with us and is tried again.</exception>
    Task<ProviderRefundResult> RefundAsync(string providerRef, string refundKey, Money amount, CancellationToken cancellationToken = default);

    Task<ProviderRefundResult> GetRefundStatusAsync(string refundKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// A bank that takes payment by QR from its own app (plan section 2.9,
/// <c>IBankQrProvider</c>). External, so always behind this interface.
/// </summary>
public interface IBankQrProvider : IRefundingProvider
{
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

/// <summary>
/// A card acquirer for payments in the mobile app: top-ups and card payments
/// (plan section 8.2, "Kart (mock)"). External, so always behind this interface.
/// </summary>
/// <remarks>
/// A charge is keyed by our reference: charging the same reference again
/// returns the first charge rather than taking the money twice - which is
/// what makes a retry after a lost answer safe.
/// </remarks>
public interface ICardPaymentProvider : IRefundingProvider
{
    /// <summary>Takes the amount; the state says whether it went through.</summary>
    /// <exception cref="PaymentProviderUnavailableException">No answer: the charge may or may not have happened; reconciliation finds out.</exception>
    Task<ProviderPaymentStatus> ChargeAsync(string providerRef, Money amount, CancellationToken cancellationToken = default);

    /// <exception cref="PaymentProviderUnavailableException">The acquirer did not answer.</exception>
    Task<ProviderPaymentStatus> GetStatusAsync(string providerRef, CancellationToken cancellationToken = default);
}

/// <summary>The payment providers the host has, by name.</summary>
public interface IPaymentProviders
{
    IBankQrProvider BankQr { get; }

    ICardPaymentProvider Card { get; }

    IBankQrProvider? Find(string name);

    /// <summary>The provider that took a payment, to give it back.</summary>
    IRefundingProvider? FindRefunding(string name);
}

/// <summary>Real-time payment messages to a kiosk (plan section 6, <c>/hubs/kiosk</c>).</summary>
public interface IPaymentNotifier
{
    /// <summary>The kiosk's customer paid; the kiosk moves on to the receipt screen.</summary>
    Task PaymentConfirmedAsync(Guid kioskId, Guid paymentId, Guid jobId, CancellationToken cancellationToken = default);
}
