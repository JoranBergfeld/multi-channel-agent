using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>Minimal in-memory <see cref="IDeliveryStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryDeliveryStore : IDeliveryStore
{
    private readonly List<Delivery> _deliveries = [];

    public IReadOnlyList<Delivery> Deliveries => _deliveries;

    public Task SaveAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        var index = _deliveries.FindIndex(d => d.DeliveryId == delivery.DeliveryId);
        if (index >= 0)
        {
            _deliveries[index] = delivery;
        }
        else
        {
            _deliveries.Add(delivery);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Delivery>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken)
    {
        var pending = _deliveries.Where(d => d.Status == DeliveryStatus.Pending).Take(maxCount).ToList();
        return Task.FromResult<IReadOnlyList<Delivery>>(pending);
    }

    public Task<IReadOnlyList<Delivery>> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var matches = _deliveries.Where(d => d.TurnId == turnId).ToList();
        return Task.FromResult<IReadOnlyList<Delivery>>(matches);
    }
}
