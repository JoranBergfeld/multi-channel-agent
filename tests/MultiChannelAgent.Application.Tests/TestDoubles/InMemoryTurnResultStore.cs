using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="ITurnResultStore"/> that models the SQL-backed store's atomicity: a Turn
/// listed in <see cref="FailForTurnIds"/> fails before touching any of the three collaborating
/// in-memory stores, so - exactly like a rolled-back SQL transaction - no partial Outcome, Delivery,
/// or inbox-completion state is ever left behind for it.
/// </summary>
public sealed class InMemoryTurnResultStore(
    InMemoryInboxStore inboxStore,
    InMemoryOutcomeStore outcomeStore,
    InMemoryDeliveryStore deliveryStore) : ITurnResultStore
{
    public HashSet<Guid> FailForTurnIds { get; } = [];

    public bool FailNextRecord { get; set; }

    public async Task RecordAsync(Outcome outcome, IReadOnlyList<Delivery> deliveries, CancellationToken cancellationToken)
    {
        if (FailNextRecord)
        {
            FailNextRecord = false;
            throw new InvalidOperationException($"Simulated one-time atomic-write failure for Turn {outcome.TurnId}.");
        }

        if (FailForTurnIds.Contains(outcome.TurnId.Value))
        {
            throw new InvalidOperationException($"Simulated atomic-write failure for Turn {outcome.TurnId}.");
        }

        await outcomeStore.SaveAsync(outcome, cancellationToken);

        foreach (var delivery in deliveries)
        {
            await deliveryStore.SaveAsync(delivery, cancellationToken);
        }

        await inboxStore.MarkCompletedAsync(outcome.TurnId, cancellationToken);
    }
}
