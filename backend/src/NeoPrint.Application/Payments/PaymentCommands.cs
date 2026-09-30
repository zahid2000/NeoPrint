using MediatR;

using NeoPrint.Application.Behaviors;
using NeoPrint.Domain.Payments;

namespace NeoPrint.Application.Payments;

/// <summary>
/// Opens a payment for a job waiting for one (plan section 9.1, step 2):
/// a bank QR, or the kiosk's "pay with the NeoPrint app" QR.
/// </summary>
/// <remarks>
/// A second request for the same job and method while the first is still
/// payable returns the first - the kiosk asking again after a lost response
/// must not leave two QR codes that can both be paid. Switching method closes
/// the earlier intent at the bank first.
/// </remarks>
public sealed record CreateKioskPaymentIntentCommand(Guid DeviceId, Guid JobId, PaymentMethod Method, string? IdempotencyKey)
    : IRequest<PaymentResponse>, ITransactionalRequest;

/// <summary>A payment as the kiosk that opened it sees it (polling fallback to <c>PaymentConfirmed</c>).</summary>
public sealed record GetKioskPaymentQuery(Guid DeviceId, Guid PaymentId) : IRequest<PaymentResponse>;

/// <summary>
/// A callback from a payment provider (plan section 6:
/// <c>POST payments/callback/{provider}</c>). The body is kept as bytes: the
/// signature is over exactly what was sent.
/// </summary>
public sealed record HandlePaymentCallbackCommand(string Provider, byte[] Body, string? Signature)
    : IRequest<PaymentCallbackOutcome>, ITransactionalRequest;

public enum PaymentCallbackOutcome
{
    /// <summary>Acted on.</summary>
    Applied = 0,

    /// <summary>The same event again: acknowledged, nothing done.</summary>
    Duplicate = 1,

    /// <summary>Signed correctly but about nothing we know: acknowledged and logged.</summary>
    Ignored = 2,
}

/// <summary>One overdue intent: the bank is asked once more, then it expires (<c>PaymentExpirationJob</c>).</summary>
public sealed record ExpirePaymentCommand(Guid PaymentId) : IRequest<Unit>, ITransactionalRequest;

/// <summary>A job left waiting for a payment nobody started (the customer walked away from the options screen).</summary>
public sealed record ExpireUnpaidJobCommand(Guid JobId) : IRequest<Unit>, ITransactionalRequest;

/// <summary>One payment checked against the provider's own status (<c>PaymentReconciliationJob</c>).</summary>
public sealed record ReconcilePaymentCommand(Guid PaymentId) : IRequest<Unit>, ITransactionalRequest;

/// <summary>A payment as the kiosk and the app see it.</summary>
/// <param name="SecondsLeft">Until the intent expires, from the server's clock; the kiosk counts down from it.</param>
/// <param name="QrContent">What to show: the bank's QR, or <c>neoprint://pay/{id}</c> for the app.</param>
public sealed record PaymentResponse(
    Guid Id,
    Guid JobId,
    string JobCode,
    string Method,
    string Status,
    decimal Amount,
    string Currency,
    string? QrContent,
    DateTime ExpiresAt,
    int SecondsLeft,
    DateTime? PaidAt);
