using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>Minimal in-memory <see cref="IStockEmptyStateReader"/>: an Inventory holds Stock only when a test says so.</summary>
public sealed class InMemoryStockEmptyStateReader : IStockEmptyStateReader
{
    private readonly HashSet<InventoryId> _withStock = [];

    public void SetAnyStock(InventoryId inventoryId, bool anyStock)
    {
        if (anyStock)
        {
            _withStock.Add(inventoryId);
        }
        else
        {
            _withStock.Remove(inventoryId);
        }
    }

    public Task<bool> AnyStockAsync(InventoryId inventoryId, CancellationToken cancellationToken) =>
        Task.FromResult(_withStock.Contains(inventoryId));
}
