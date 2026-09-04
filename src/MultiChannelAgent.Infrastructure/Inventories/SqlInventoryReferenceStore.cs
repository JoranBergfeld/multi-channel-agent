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

    /// <summary>
    /// One query for every distinct term Initial Import names, joining active <c>UnitTerms</c> to
    /// their active owning <c>Units</c>. The <c>Contains</c> call is wrapped in <see cref="EF.Parameter{T}"/>
    /// so it always translates to <see cref="ParameterTranslationMode.Parameter"/> - one array-like
    /// query parameter unnested by the database (<c>OPENJSON</c> on SQL Server, <c>json_each</c> on
    /// SQLite) - regardless of whatever <see cref="ParameterTranslationMode"/> the host configures as
    /// its default. EF Core 10 defaults that global setting to
    /// <see cref="ParameterTranslationMode.MultipleParameters"/> - one SQL parameter per term - which
    /// is exactly the per-term cost this batching exists to remove, and which SQL Server's roughly
    /// 2,100-parameter ceiling would outright reject for a 5,000-term file. Forcing
    /// <see cref="ParameterTranslationMode.Parameter"/> here keeps this one round trip, and one
    /// parameter, safe on SQL Server at the full 5,000-row bound, independent of any global default.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ResolvedUnitReference>> ResolveUnitsAsync(
        InventoryId inventoryId, IReadOnlyCollection<string> normalizedTerms, CancellationToken cancellationToken)
    {
        if (normalizedTerms.Count == 0)
        {
            return new Dictionary<string, ResolvedUnitReference>(StringComparer.Ordinal);
        }

        var ordinalCollation = db.Database.IsSqlServer()
            ? MultiChannelAgentDbContext.OrdinalSqlServerCollation
            : "BINARY";
        var rows = await db.UnitTerms
            .AsNoTracking()
            .Where(t =>
                t.InventoryId == inventoryId.Value
                && t.RetiredAt == null
                && EF.Parameter(normalizedTerms).Contains(
                    EF.Functions.Collate(t.NormalizedTerm, ordinalCollation)))
            .Join(
                db.Units.AsNoTracking().Where(u => u.InventoryId == inventoryId.Value && u.RetiredAt == null),
                t => t.UnitId,
                u => u.Id,
                (t, u) => new { t.NormalizedTerm, UnitId = u.Id, u.CanonicalName })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.NormalizedTerm,
            row => new ResolvedUnitReference(new UnitId(row.UnitId), row.CanonicalName),
            StringComparer.Ordinal);
    }

    /// <summary>One query for every distinct Location name Initial Import names, active only. See <see cref="ResolveUnitsAsync"/> for why the collection is wrapped in <see cref="EF.Parameter{T}"/>.</summary>
    public async Task<IReadOnlyDictionary<string, ResolvedLocationReference>> ResolveLocationsAsync(
        InventoryId inventoryId, IReadOnlyCollection<string> normalizedNames, CancellationToken cancellationToken)
    {
        if (normalizedNames.Count == 0)
        {
            return new Dictionary<string, ResolvedLocationReference>(StringComparer.Ordinal);
        }

        var ordinalCollation = db.Database.IsSqlServer()
            ? MultiChannelAgentDbContext.OrdinalSqlServerCollation
            : "BINARY";
        var rows = await db.Locations
            .AsNoTracking()
            .Where(l =>
                l.InventoryId == inventoryId.Value
                && l.RetiredAt == null
                && EF.Parameter(normalizedNames).Contains(
                    EF.Functions.Collate(l.NormalizedName, ordinalCollation)))
            .Select(l => new { l.NormalizedName, l.Id, l.Name })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.NormalizedName,
            row => new ResolvedLocationReference(new LocationId(row.Id), row.Name),
            StringComparer.Ordinal);
    }
}
