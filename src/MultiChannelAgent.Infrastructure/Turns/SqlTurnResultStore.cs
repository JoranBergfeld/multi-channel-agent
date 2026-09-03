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
/// that happens. On failure, the <see cref="DbContext.ChangeTracker"/> is cleared before rethrowing so
/// this Turn's failed attempt cannot leave stale tracked entities behind to contaminate a later Turn's
/// <c>RecordAsync</c> call against the same shared <see cref="MultiChannelAgentDbContext"/> - the
/// situation one <see cref="Application.Turns.TurnProcessingCoordinator"/> pass creates when it
/// processes a whole batch of Turns through one DI scope.
/// </summary>
public sealed class SqlTurnResultStore(MultiChannelAgentDbContext db) : ITurnResultStore
{
    public async Task RecordAsync(Outcome outcome, IReadOnlyList<Delivery> deliveries, CancellationToken cancellationToken)
    {
        var inboxEntry = await db.InboxEntries.FirstAsync(e => e.TurnId == outcome.TurnId.Value, cancellationToken);
        inboxEntry.Status = InboxEntryStatus.Completed;

        db.Outcomes.Add(OutcomeEntityMapping.ToEntity(outcome));

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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // EF Core does not roll back change tracking when SaveChangesAsync fails - only the
            // database transaction. Left alone, the Outcome/Delivery Added entries and the InboxEntry
            // Modified entry staged above for this Turn would remain tracked in this DbContext and be
            // resent (and can fail again) on the very next SaveChangesAsync call. Because one
            // TurnProcessingCoordinator pass shares a single scoped DbContext across a whole batch of
            // Turns, that would let one Turn's failure contaminate every later Turn's record attempt
            // in the same batch. Clearing the tracker confines the failure to this Turn only, so the
            // next RecordAsync call in the same scope starts from a clean tracker.
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
