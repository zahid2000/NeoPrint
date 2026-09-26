using MassTransit;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Infrastructure.Messaging;

/// <inheritdoc cref="IEventPublisher" />
/// <remarks>
/// Inside a transactional request the bus outbox is active, so the event is
/// written to MongoDB in the same transaction as the state change and
/// delivered afterwards. Outside one it is published directly.
/// </remarks>
public sealed class MassTransitEventPublisher : IEventPublisher
{
    private readonly IPublishEndpoint _bus;
    private readonly ICorrelationContext _correlation;

    public MassTransitEventPublisher(IPublishEndpoint bus, ICorrelationContext correlation)
    {
        _bus = bus;
        _correlation = correlation;
    }

    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class
        => _bus.Publish(
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
