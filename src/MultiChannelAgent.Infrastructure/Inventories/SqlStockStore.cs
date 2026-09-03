using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockStore"/>. Loads every Stock Entry for the given Inventory
/// (already scoped by a WHERE, and joined against Units/Locations for their display names) once per
/// call, then applies filtering, the shared deterministic display order
/// (<see cref="StockEntryOrdering.ByDisplayOrder"/>), keyset cursor resumption, and the page/candidate
/// cap in memory - mirroring <see cref="Application.Inventories.InventoryListingService"/>'s own
/// fetch-then-order-in-memory approach, so ordering always matches
/// <see cref="StockEntryOrdering.ByDisplayOrder"/>'s ordinal semantics exactly rather than depending on
/// the database column collation's own (non-ordinal) string comparison rules.
/// </summary>
public sealed class SqlStockStore(MultiChannelAgentDbContext db) : IStockStore
{
    public async Task<IReadOnlyList<StockEntrySummary>> ListPageAsync(StockListQuery query, CancellationToken cancellationToken)
    {
        var rows = await LoadInventoryRowsAsync(query.InventoryId, cancellationToken);

        var filtered = rows
            .Where(r => query.IncludeZero || r.Quantity.IsOnHand)
            .Where(r => query.LocationId is null || r.LocationId == query.LocationId)
            .Where(r => query.NameFilter is null
                || r.NormalizedName.Contains(NameNormalization.Normalize(query.NameFilter), StringComparison.Ordinal))
            .OrderBy(r => r, StockEntryOrdering.ByDisplayOrder)
            .ToList();

        if (query.Cursor is { } cursor)
        {
            var cursorRow = CursorAsComparableRow(cursor);
            filtered = filtered.Where(r => StockEntryOrdering.ByDisplayOrder.Compare(r, cursorRow) > 0).ToList();
        }

        return filtered.Take(query.PageSize + 1).ToList();
    }

    public async Task<IReadOnlyList<StockEntrySummary>> FindMatchesAsync(
        StockFindQuery query, int maxCandidatesPlusOne, CancellationToken cancellationToken)
    {
        var rows = await LoadInventoryRowsAsync(query.InventoryId, cancellationToken);

        IEnumerable<StockEntrySummary> matches = query.StockEntryId is { } id
            ? rows.Where(r => r.Id == id)
            : rows
                .Where(r => r.NormalizedName == query.NormalizedNameReference)
                .Where(r => query.UnitId is null || r.UnitId == query.UnitId)
                .Where(r => query.LocationId is null || r.LocationId == query.LocationId);

        return matches.OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).Take(maxCandidatesPlusOne).ToList();
    }

    private async Task<List<StockEntrySummary>> LoadInventoryRowsAsync(InventoryId inventoryId, CancellationToken cancellationToken)
    {
        var joined = await (
            from stockEntry in db.StockEntries.AsNoTracking()
            where stockEntry.InventoryId == inventoryId.Value
            join unit in db.Units.AsNoTracking() on stockEntry.UnitId equals unit.Id
            join location in db.Locations.AsNoTracking() on stockEntry.LocationId equals location.Id into locationJoin
            from location in locationJoin.DefaultIfEmpty()
            select new
            {
                StockEntry = stockEntry,
                UnitCanonicalName = unit.CanonicalName,
                LocationName = location != null ? location.Name : null,
            }).ToListAsync(cancellationToken);

        return joined.Select(x => new StockEntrySummary(
            new StockEntryId(x.StockEntry.Id),
            x.StockEntry.Name,
            x.StockEntry.NormalizedName,
            new UnitId(x.StockEntry.UnitId),
            x.UnitCanonicalName,
            x.StockEntry.LocationId is { } locationId ? new LocationId(locationId) : null,
            x.LocationName,
            x.StockEntry.Note,
            Quantity.Create(x.StockEntry.Quantity))).ToList();
    }

    private static StockEntrySummary CursorAsComparableRow(StockListCursor cursor) => new(
        cursor.StockEntryId,
        Name: string.Empty,
        cursor.NormalizedName,
        UnitId: default,
        cursor.UnitCanonicalName,
        LocationId: null,
        cursor.LocationName,
        Note: null,
        Quantity: default);
}
