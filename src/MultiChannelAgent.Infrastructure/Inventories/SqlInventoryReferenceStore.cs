using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IInventoryReferenceStore"/>. A reference resolves by opaque
/// identifier or by exact normalized name - for a Unit, against its shared term namespace, so an
/// active alias resolves exactly as its canonical name does. Both lookups are scoped to the given
/// Inventory, so an identifier belonging to another Inventory resolves to nothing rather than
/// crossing the boundary.
/// </summary>
public sealed class SqlInventoryReferenceStore(MultiChannelAgentDbContext db) : IInventoryReferenceStore
{
    public async Task<UnitId?> ResolveUnitAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var unitId))
        {
            var byId = await db.Units
                .AsNoTracking()
                .AnyAsync(u => u.InventoryId == inventoryId.Value && u.Id == unitId && u.RetiredAt == null, cancellationToken);

            return byId ? new UnitId(unitId) : null;
        }

        var normalizedTerm = NameNormalization.Normalize(reference);
        var term = await db.UnitTerms
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.InventoryId == inventoryId.Value && t.NormalizedTerm == normalizedTerm && t.RetiredAt == null,
                cancellationToken);

        return term is null ? null : new UnitId(term.UnitId);
    }

    public async Task<LocationId?> ResolveLocationAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var locationId))
        {
            var byId = await db.Locations
                .AsNoTracking()
                .AnyAsync(l => l.InventoryId == inventoryId.Value && l.Id == locationId && l.RetiredAt == null, cancellationToken);

            return byId ? new LocationId(locationId) : null;
        }

        var normalizedName = NameNormalization.Normalize(reference);
        var location = await db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.InventoryId == inventoryId.Value && l.NormalizedName == normalizedName && l.RetiredAt == null,
                cancellationToken);

        return location is null ? null : new LocationId(location.Id);
    }

    public async Task<string?> FindUnitCanonicalNameAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken) =>
        await db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == inventoryId.Value && u.Id == unitId.Value && u.RetiredAt == null)
            .Select(u => u.CanonicalName)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> FindLocationNameAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        await db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == inventoryId.Value && l.Id == locationId.Value && l.RetiredAt == null)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(cancellationToken);
}
