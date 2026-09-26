namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Publishes a domain event for something else to pick up.
/// </summary>
/// <remarks>
/// <para>
/// An abstraction rather than MassTransit's own <c>IPublishEndpoint</c>,
/// because the Application layer does not depend on the transport
/// (<c>docs/ARCHITECTURE_RULES.md</c>). The implementation in Infrastructure
/// publishes through MassTransit, which routes it through the MongoDB outbox
/// when the call is inside a transaction - so an event published alongside a
/// state change cannot be lost, and cannot be sent for a change that was
/// rolled back.
/// </para>
/// <para>
/// The correlation id is attached by the implementation, so a handler does
/// not have to remember to pass it.
/// </para>
/// </remarks>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class;
}
