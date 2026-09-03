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
        // Two things have to hold at once here, and neither an ordering nor a filter alone gives both.
        //
        // Within one ChannelConversation, one Turn's answer must never be sent before an earlier
        // Turn's. That cannot rest on when each Turn was received: an adapter supplies its channel's
        // own received time, so a later Turn can legitimately carry an earlier instant (a delayed
        // message, a replica whose clock lags), and ordering by it would send that conversation's
        // answers out of order. So a conversation only ever offers its head - the still-undeliverable
        // response of its earliest undelivered Turn - which makes that guarantee hold no matter what
        // any clock says. A part that keeps failing therefore keeps its place at the front of its own
        // conversation, which is precisely the point: the next answer must not overtake an answer
        // that has not been sent.
        //
        // The guarantee is between Turns, not inside one. Every producer today records exactly one
        // response part per answered Turn, so there is nothing to order within a Turn; the trailing
        // Delivery id is only an arbitrary but stable total order that keeps one backlog yielding the
        // same batch. Should a Turn ever need several ordered parts, they would need an explicit
        // ordinal to be dispatched in their authored order - a random identifier cannot express one.
        //
        // Across conversations, which heads fit inside maxCount is purely a fairness question, so it
        // follows how long each has waited - the acceptance instant as UTC ticks (a DateTimeOffset is
        // not orderable on every provider). Ordering by the conversation sequence instead would rank
        // a long-running conversation's answer behind every brand-new conversation's first one and
        // let a trickle of new conversations starve it.
        var pending = await (
            from delivery in db.Deliveries.AsNoTracking()
            where delivery.Status == DeliveryEntityStatus.Pending
            join inboxEntry in db.InboxEntries.AsNoTracking() on delivery.TurnId equals inboxEntry.TurnId
            where !db.Deliveries.Any(earlier =>
                earlier.Status == DeliveryEntityStatus.Pending
                && db.InboxEntries.Any(earlierTurn =>
                    earlierTurn.TurnId == earlier.TurnId
                    && earlierTurn.ChannelConversationId == inboxEntry.ChannelConversationId
                    && earlierTurn.ConversationSequence < inboxEntry.ConversationSequence))
            orderby inboxEntry.ReceivedAtTicks, inboxEntry.ConversationSequence, delivery.DeliveryId
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
