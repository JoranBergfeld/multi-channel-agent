using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL Server-backed <see cref="ITurnResultStore"/>. Stages the Outcome insert, Delivery inserts, and
/// the inbox completion update against the same <see cref="MultiChannelAgentDbContext"/> and commits
/// them with a single <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call, which SQL
/// Server executes as one transaction: either all three durable effects are recorded together, or
/// none of them are, and a Turn stays claimable by <see cref="SqlInboxStore.ClaimPendingAsync"/> until
/// that happens.
/// </summary>
public sealed class SqlTurnResultStore(MultiChannelAgentDbContext db) : ITurnResultStore
{
    public async Task RecordAsync(Outcome outcome, IReadOnlyList<Delivery> deliveries, CancellationToken cancellationToken)
    {
        var inboxEntry = await db.InboxEntries.FirstAsync(e => e.TurnId == outcome.TurnId.Value, cancellationToken);
        inboxEntry.Status = InboxEntryStatus.Completed;

        db.Outcomes.Add(new OutcomeEntity
        {
            TurnId = outcome.TurnId.Value,
            Status = outcome.Status == OutcomeStatus.Completed ? OutcomeEntityStatus.Completed : OutcomeEntityStatus.Failed,
            Code = outcome.Code,
            Summary = outcome.Summary,
            CreatedAt = outcome.CreatedAt,
        });

        foreach (var delivery in deliveries)
        {
            db.Deliveries.Add(new DeliveryEntity
            {
                DeliveryId = delivery.DeliveryId,
                TurnId = delivery.TurnId.Value,
                Channel = delivery.Channel,
                Payload = delivery.Payload,
                Status = delivery.Status == DeliveryStatus.Delivered ? DeliveryEntityStatus.Delivered : DeliveryEntityStatus.Pending,
                Attempts = delivery.Attempts,
                CreatedAt = delivery.CreatedAt,
                DeliveredAt = delivery.DeliveredAt,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
