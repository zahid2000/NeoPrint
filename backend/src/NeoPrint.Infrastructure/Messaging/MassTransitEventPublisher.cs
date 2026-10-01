using MassTransit;

using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Messaging;

/// <inheritdoc cref="IEventPublisher" />
/// <remarks>
/// <para>
/// Inside a transactional request the event goes through the transaction's own
/// bus outbox (<see cref="TransactionOutbox"/>): it is written to MongoDB when
/// the transaction commits (MongoTransactionRunner), together with the state
/// change, and delivered from there - in a consumer as much as in a request.
/// </para>
/// <para>
/// Outside one - an upload, a background job - it is published straight to the
/// broker. A scoped publish endpoint cannot be used there: with the bus outbox
/// on, it buffers messages for a commit that never comes, and the event is
/// silently lost (docs/DECISIONS.md).
/// </para>
/// </remarks>
public sealed class MassTransitEventPublisher : IEventPublisher
{
    private readonly IBus _bus;
    private readonly MongoContext _context;
    private readonly ICorrelationContext _correlation;

    public MassTransitEventPublisher(IBus bus, MongoContext context, ICorrelationContext correlation)
    {
        _bus = bus;
        _context = context;
        _correlation = correlation;
    }

    /// <summary>Whether a transaction is open that the event can join.</summary>
    public bool InTransaction => _context.Session is { IsInTransaction: true };

    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        IPublishEndpoint endpoint = InTransaction
            ? TransactionOutbox.Current
              ?? throw new InvalidOperationException("A transaction is open but its outbox is not: publish through MongoTransactionRunner.")
            : _bus;

        return endpoint.Publish(
            message,
            context =>
            {
                // So the consumer's log lines join the request's
                // (docs/OBSERVABILITY.md).
                if (_correlation.CorrelationId is { Length: > 0 } id)
                {
                    context.Headers.Set("X-Correlation-Id", id);
                }
            },
            cancellationToken);
    }
}
