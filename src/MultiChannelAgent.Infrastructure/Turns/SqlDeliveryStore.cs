using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

public sealed class SqlDeliveryStore(MultiChannelAgentDbContext db) : IDeliveryStore
{
    public async Task SaveAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        var entity = await db.Deliveries.FirstOrDefaultAsync(e => e.DeliveryId == delivery.DeliveryId, cancellationToken);

        if (entity is null)
        {
            db.Deliveries.Add(new DeliveryEntity
            {
                DeliveryId = delivery.DeliveryId,
                TurnId = delivery.TurnId.Value,
                Channel = delivery.Channel,
                Payload = delivery.Payload,
                Status = ToEntityStatus(delivery.Status),
                Attempts = delivery.Attempts,
                CreatedAt = delivery.CreatedAt,
                DeliveredAt = delivery.DeliveredAt,
            });
        }
        else
        {
            entity.Status = ToEntityStatus(delivery.Status);
            entity.Attempts = delivery.Attempts;
            entity.DeliveredAt = delivery.DeliveredAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Delivery>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken)
    {
        // Ordered by the Turn's durable per-conversation acceptance sequence, so one conversation's
        // response parts are always dispatched in the order their Turns were answered. (Cross-
        // conversation order is deliberately unconstrained - conversations are independent - and the
        // Delivery id breaks ties.) The Turn's own sequence is used rather than the Delivery's
        // creation instant because a DateTimeOffset is not orderable on every relational provider,
        // and this claim must behave identically wherever it runs.
        var pending = await (
            from delivery in db.Deliveries.AsNoTracking()
            where delivery.Status == DeliveryEntityStatus.Pending
            join inboxEntry in db.InboxEntries.AsNoTracking() on delivery.TurnId equals inboxEntry.TurnId
            orderby inboxEntry.ConversationSequence, delivery.DeliveryId
            select delivery)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        return pending.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Delivery>> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var matches = await db.Deliveries
            .AsNoTracking()
            .Where(e => e.TurnId == turnId.Value)
            .ToListAsync(cancellationToken);

        // One Turn's Deliveries are a handful of rows at most, so they are ordered here rather than
        // in SQL: a DateTimeOffset is not orderable by every relational provider (SQLite rejects it
        // outright), and this read must behave identically wherever it runs.
        return matches.OrderBy(e => e.CreatedAt).ThenBy(e => e.DeliveryId).Select(ToDomain).ToList();
    }

    private static DeliveryEntityStatus ToEntityStatus(DeliveryStatus status) =>
        status == DeliveryStatus.Delivered ? DeliveryEntityStatus.Delivered : DeliveryEntityStatus.Pending;

    private static Delivery ToDomain(DeliveryEntity entity) => new()
    {
        DeliveryId = entity.DeliveryId,
        TurnId = new TurnId(entity.TurnId),
        Channel = entity.Channel,
        Payload = entity.Payload,
        Status = entity.Status == DeliveryEntityStatus.Delivered ? DeliveryStatus.Delivered : DeliveryStatus.Pending,
        Attempts = entity.Attempts,
        CreatedAt = entity.CreatedAt,
        DeliveredAt = entity.DeliveredAt,
    };
}
