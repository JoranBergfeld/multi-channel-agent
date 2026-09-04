using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockEmptyStateReader"/>.
///
/// Deliberately unfiltered on Quantity: a zero-quantity Stock Entry is a Stock Entry, which is why
/// Forget exists to remove one, and #34 says the gate counts them.
/// </summary>
public sealed class SqlStockEmptyStateReader(MultiChannelAgentDbContext db) : IStockEmptyStateReader
{
    public Task<bool> AnyStockAsync(InventoryId inventoryId, CancellationToken cancellationToken) =>
        db.StockEntries.AsNoTracking().AnyAsync(e => e.InventoryId == inventoryId.Value, cancellationToken);
}
