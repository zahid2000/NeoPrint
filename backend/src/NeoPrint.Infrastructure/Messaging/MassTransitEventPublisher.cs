using MassTransit;

using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Messaging;

/// <inheritdoc cref="IEventPublisher" />
/// <remarks>
/// <para>
/// Inside a transactional request the event goes through the bus outbox: it is
/// written to MongoDB when the transaction commits (MongoTransactionRunner),
/// together with the state change, and delivered from there.
/// </para>
/// <para>
/// Outside one - an upload, a background job - it is published straight to the
/// broker. The scoped publish endpoint cannot be used there: with the bus
/// outbox on, it buffers messages for a commit that never comes, and the event
/// is silently lost (docs/DECISIONS.md).
/// </para>
/// </remarks>
public sealed class MassTransitEventPublisher : IEventPublisher
{
    private readonly IPublishEndpoint _outbox;
    private readonly IBus _bus;
    private readonly MongoContext _context;
    private readonly ICorrelationContext _correlation;

    public MassTransitEventPublisher(
        IPublishEndpoint outbox, IBus bus, MongoContext context, ICorrelationContext correlation)
    {
        _outbox = outbox;
        _bus = bus;
        _context = context;
        _correlation = correlation;
    }

    /// <summary>Whether a transaction is open that the event can join.</summary>
    public bool InTransaction => _context.Session is { IsInTransaction: true };

    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        IPublishEndpoint endpoint = InTransaction ? _outbox : _bus;

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
