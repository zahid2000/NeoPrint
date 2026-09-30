using MediatR;

using NeoPrint.Application.Behaviors;
using NeoPrint.Contracts;

namespace NeoPrint.Application.Refunds;

/// <summary>
/// Turns a refund decision into refunds, one per part of the payment (plan
/// section 2.5: "hər hissə öz mənbəyinə qaytarılır"). The same decision twice
/// makes no new refund.
/// </summary>
public sealed record CreateRefundsCommand(RefundRequested Request) : IRequest<IReadOnlyList<Guid>>, ITransactionalRequest;

/// <summary>Carries a refund as far as it can go now: done, waiting for its next try, or failed for an admin.</summary>
public sealed record ProcessRefundCommand(Guid RefundId) : IRequest<string>;

/// <summary>Step one of <see cref="ProcessRefundCommand"/>: internal destinations finish here; a provider refund is counted and handed back.</summary>
public sealed record StartRefundCommand(Guid RefundId) : IRequest<RefundStep>, ITransactionalRequest;

/// <summary>Step two: what the provider answered.</summary>
public sealed record RecordProviderRefundCommand(Guid RefundId, string State, string? RefundRef, string? FailureCode)
    : IRequest<string>, ITransactionalRequest;

/// <summary>
/// Scenario A (plan section 2.5): a job stopped with the kiosk still talking
/// to us. An anonymous bank payment comes back automatically for what did
/// not print; the job is cancelled.
/// </summary>
public sealed record HandleInterruptedJobCommand(Guid JobId) : IRequest<Unit>, ITransactionalRequest;

/// <summary>A job was cancelled: its open intents close, what was paid goes back.</summary>
public sealed record HandleCancelledJobCommand(PrintJobCancelled Cancelled) : IRequest<Unit>, ITransactionalRequest;

/// <summary>What to do after <see cref="StartRefundCommand"/>.</summary>
/// <param name="Provider">Set when the provider must be asked: its name, the payment's reference, the refund key.</param>
public sealed record RefundStep(bool Done, string? Provider, string? ProviderRef, string? RefundKey, decimal Amount);
