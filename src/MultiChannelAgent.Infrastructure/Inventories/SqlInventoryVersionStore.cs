using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed <see cref="IInventoryVersionStore"/>. One query for the whole requested set rather than
/// one per Inventory, because a Participant-level stream re-reads every Inventory they may see on
/// every poll.
/// </summary>
public sealed class SqlInventoryVersionStore(MultiChannelAgentDbContext db) : IInventoryVersionStore
{
    public async Task<IReadOnlyDictionary<Guid, long>> ReadAsync(
        IReadOnlyCollection<Guid> inventoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventoryIds);

        if (inventoryIds.Count == 0)
        {
            return new Dictionary<Guid, long>();
        }

        var ids = inventoryIds.Distinct().ToList();

        return await db.InventoryVersions
            .AsNoTracking()
            .Where(v => ids.Contains(v.InventoryId))
            .ToDictionaryAsync(v => v.InventoryId, v => v.Version, cancellationToken);
    }
}
