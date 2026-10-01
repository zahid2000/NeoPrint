using MassTransit;

namespace NeoPrint.Infrastructure.Messaging;

/// <summary>
/// The bus outbox of the transaction running in this asynchronous flow: where
/// an event published inside it goes (docs/adr/ADR-0006-transaction-outbox-scope.md).
/// </summary>
/// <remarks>
/// Set by <see cref="Persistence.MongoTransactionRunner"/> for the length of
/// one attempt, next to the ambient session (ADR-0004). It comes from a scope
/// of its own with no message being consumed in it, so it is always the bus
/// outbox bound to the transaction's session - never a consumer's own
/// <c>ConsumeContext</c>, which would hand the event to the broker before the
/// transaction commits, or even when it aborts (docs/TASKS.md T-119).
/// </remarks>
public static class TransactionOutbox
{
    private static readonly AsyncLocal<IPublishEndpoint?> Ambient = new();

    public static IPublishEndpoint? Current => Ambient.Value;

    internal static IDisposable Use(IPublishEndpoint outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        if (Ambient.Value is not null)
        {
            throw new InvalidOperationException("A transaction's outbox is already in use in this flow.");
        }

        Ambient.Value = outbox;
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}
