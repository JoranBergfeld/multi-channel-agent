using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IReferenceCatalogStore"/>. Every query filters on
/// <c>RetiredAt == null</c> and on the Inventory from trusted context, so a retired reference and a
/// reference belonging to another Inventory are both simply absent rather than filtered out later by
/// a caller who might forget.
///
/// Ordering and paging are done by the database against the normalized columns, which carry a binary
/// collation on SQL Server (see <see cref="MultiChannelAgentDbContext"/>), so the database's order is
/// the domain's ordinal order rather than a locale-dependent approximation of it.
/// </summary>
public sealed class SqlReferenceCatalogStore(MultiChannelAgentDbContext db) : IReferenceCatalogStore
{
    public async Task<IReadOnlyList<UnitCatalogRecord>> ListUnitsAsync(
        ReferenceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var units = db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == query.InventoryId.Value && u.RetiredAt == null);

        if (query.Cursor is { OrderKey: var key })
        {
            // Keyset resumption strictly after the cursor's order key. A normalized canonical name is
            // already unique among the active Units of one Inventory (the filtered unique term index
            // guarantees it), so this comparison alone never skips or repeats a row - exactly the
            // argument the shipped Stock keyset relies on.
            var name = key.NormalizedName;

            units = units.Where(u => string.Compare(u.NormalizedCanonicalName, name) > 0);
        }

        var page = await units
            .OrderBy(u => u.NormalizedCanonicalName)
            .ThenBy(u => u.Id)
            .Take(query.PageSize + 1)
            .Select(u => new { u.Id, u.CanonicalName, u.NormalizedCanonicalName, u.IsReserved, u.ConcurrencyStamp })
            .ToListAsync(cancellationToken);

        var unitIds = page.Select(row => row.Id).ToList();

        // Ordered after materializing rather than in SQL: a Unit's terms are ordered canonical-first
        // and then by when they were added, and SQLite cannot ORDER BY a DateTimeOffset. The set is
        // bounded by one page of Units, so ordering it here costs nothing and reads the same on both
        // providers.
        var terms = (await db.UnitTerms
                .AsNoTracking()
                .Where(t => t.InventoryId == query.InventoryId.Value && t.RetiredAt == null && unitIds.Contains(t.UnitId))
                .Select(t => new { t.UnitId, t.Term, t.NormalizedTerm, t.IsCanonical, t.IsReserved, t.CreatedAt, t.Id })
                .ToListAsync(cancellationToken))
            .OrderByDescending(t => t.IsCanonical)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToList();

        return page
            .Select(row => new UnitCatalogRecord(
                new UnitId(row.Id),
                row.CanonicalName,
                row.NormalizedCanonicalName,
                terms
                    .Where(term => term.UnitId == row.Id)
                    .Select(term => new UnitTerm
                    {
                        Term = term.Term,
                        NormalizedTerm = term.NormalizedTerm,
                        IsCanonical = term.IsCanonical,
                        IsReserved = term.IsReserved,
                    })
                    .ToList(),
                row.IsReserved,
                row.ConcurrencyStamp))
            .ToList();
    }

    public async Task<IReadOnlyList<LocationCatalogRecord>> ListLocationsAsync(
        ReferenceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var locations = db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == query.InventoryId.Value && l.RetiredAt == null);

        if (query.Cursor is { OrderKey: var key })
        {
            // Keyset resumption, on the same argument as ListUnitsAsync: a normalized Location name is
            // unique among the active Locations of one Inventory.
            var name = key.NormalizedName;

            locations = locations.Where(l => string.Compare(l.NormalizedName, name) > 0);
        }

        return await locations
            .OrderBy(l => l.NormalizedName)
            .ThenBy(l => l.Id)
            .Take(query.PageSize + 1)
            .Select(l => new LocationCatalogRecord(new LocationId(l.Id), l.Name, l.NormalizedName, l.ConcurrencyStamp))
            .ToListAsync(cancellationToken);
    }

    public async Task<UnitCatalogRecord?> FindUnitAsync(
        InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken)
    {
        var unit = await db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == inventoryId.Value && u.Id == unitId.Value && u.RetiredAt == null)
            .Select(u => new { u.CanonicalName, u.NormalizedCanonicalName, u.IsReserved, u.ConcurrencyStamp })
            .FirstOrDefaultAsync(cancellationToken);

        if (unit is null)
        {
            return null;
        }

        // Ordered after materializing, for the same reason as ListUnitsAsync: one Unit's term set is
        // small and bounded, and SQLite cannot ORDER BY a DateTimeOffset.
        var terms = (await db.UnitTerms
                .AsNoTracking()
                .Where(t => t.InventoryId == inventoryId.Value && t.UnitId == unitId.Value && t.RetiredAt == null)
                .Select(t => new { t.Term, t.NormalizedTerm, t.IsCanonical, t.IsReserved, t.CreatedAt, t.Id })
                .ToListAsync(cancellationToken))
            .OrderByDescending(t => t.IsCanonical)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new UnitTerm
            {
                Term = t.Term,
                NormalizedTerm = t.NormalizedTerm,
                IsCanonical = t.IsCanonical,
                IsReserved = t.IsReserved,
            })
            .ToList();

        return new UnitCatalogRecord(
            unitId, unit.CanonicalName, unit.NormalizedCanonicalName, terms, unit.IsReserved, unit.ConcurrencyStamp);
    }

    public async Task<LocationCatalogRecord?> FindLocationAsync(
        InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        await db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == inventoryId.Value && l.Id == locationId.Value && l.RetiredAt == null)
            .Select(l => new LocationCatalogRecord(new LocationId(l.Id), l.Name, l.NormalizedName, l.ConcurrencyStamp))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> ReadActiveUnitTermsAsync(
        InventoryId inventoryId, UnitId? excluding, CancellationToken cancellationToken)
    {
        var terms = db.UnitTerms
            .AsNoTracking()
            .Where(t => t.InventoryId == inventoryId.Value && t.RetiredAt == null);

        if (excluding is { } unitId)
        {
            terms = terms.Where(t => !(t.UnitId == unitId.Value && t.IsCanonical));
        }

        var rows = await terms.Select(t => t.NormalizedTerm).ToListAsync(cancellationToken);

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> ReadActiveLocationNamesAsync(
        InventoryId inventoryId, LocationId? excluding, CancellationToken cancellationToken)
    {
        var locations = db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == inventoryId.Value && l.RetiredAt == null);

        if (excluding is { } locationId)
        {
            locations = locations.Where(l => l.Id != locationId.Value);
        }

        var rows = await locations.Select(l => l.NormalizedName).ToListAsync(cancellationToken);

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<int> CountStockReferencesAsync(
        InventoryId inventoryId, ReferenceKind kind, Guid referenceId, CancellationToken cancellationToken)
    {
        var entries = db.StockEntries.AsNoTracking().Where(e => e.InventoryId == inventoryId.Value);

        entries = kind == ReferenceKind.Unit
            ? entries.Where(e => e.UnitId == referenceId)
            : entries.Where(e => e.LocationId == referenceId);

        return await entries.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var normalized = NameNormalization.Normalize(reference);

        var candidates = kind == ReferenceKind.Unit
            ? db.UnitTerms
                .AsNoTracking()
                .Where(t => t.InventoryId == inventoryId.Value && t.RetiredAt == null)
                .OrderBy(t => t.NormalizedTerm)
                .ThenBy(t => t.Id)
                .Select(t => new { Display = t.Term, Normalized = t.NormalizedTerm })
            : db.Locations
                .AsNoTracking()
                .Where(l => l.InventoryId == inventoryId.Value && l.RetiredAt == null)
                .OrderBy(l => l.NormalizedName)
                .ThenBy(l => l.Id)
                .Select(l => new { Display = l.Name, Normalized = l.NormalizedName });

        if (normalized.Length > 0)
        {
            var prefixed = await candidates
                .Where(row => row.Normalized.StartsWith(normalized))
                .Take(IReferenceCatalogStore.MaxSuggestions)
                .Select(row => row.Display)
                .ToListAsync(cancellationToken);

            if (prefixed.Count > 0)
            {
                return prefixed;
            }
        }

        // Nothing shares a prefix, so the honest answer is "here is what this Inventory actually
        // has" - bounded, in the same one order, and never a nearest-match guess.
        return await candidates
            .Take(IReferenceCatalogStore.MaxSuggestions)
            .Select(row => row.Display)
            .ToListAsync(cancellationToken);
    }
}
