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
        var pending = await db.Deliveries
            .AsNoTracking()
            .Where(e => e.Status == DeliveryEntityStatus.Pending)
            .OrderBy(e => e.CreatedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        return pending.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Delivery>> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var matches = await db.Deliveries
            .AsNoTracking()
            .Where(e => e.TurnId == turnId.Value)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        return matches.Select(ToDomain).ToList();
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
