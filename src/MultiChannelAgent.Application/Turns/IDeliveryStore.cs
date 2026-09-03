using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>Durable outbox for requested <see cref="Delivery"/> records, dispatched independently of processing.</summary>
public interface IDeliveryStore
{
    Task SaveAsync(Delivery delivery, CancellationToken cancellationToken);

    Task<IReadOnlyList<Delivery>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken);

    Task<IReadOnlyList<Delivery>> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken);
}
