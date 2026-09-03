using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockStore"/>. Every filter, the deterministic display order, keyset
/// cursor resumption, and the page/candidate cap are expressed in the query itself, so the database
/// returns only the rows the caller asked for - an Inventory with a hundred thousand Stock Entries
/// costs the same one bounded query as an empty one. Ordering uses the normalized order-key columns
/// (<see cref="StockEntryOrderKey"/>), which carry a binary collation on SQL Server, so the database's
/// order is exactly the domain's own ordinal order rather than a locale-dependent approximation of it.
/// </summary>
public sealed class SqlStockStore(MultiChannelAgentDbContext db) : IStockStore
{
    public async Task<IReadOnlyList<StockEntrySummary>> ListPageAsync(StockListQuery query, CancellationToken cancellationToken)
    {
        var rows = ScopedRows(query.InventoryId);

        if (!query.IncludeZero)
        {
            rows = rows.Where(r => r.StockEntry.Quantity > 0m);
        }

        if (query.UnitId is { } unitId)
        {
            rows = rows.Where(r => r.StockEntry.UnitId == unitId.Value);
        }

        if (query.UnlocatedOnly)
        {
            rows = rows.Where(r => r.StockEntry.LocationId == null);
        }
        else if (query.LocationId is { } locationId)
        {
            rows = rows.Where(r => r.StockEntry.LocationId == locationId.Value);
        }

        if (query.NameFilter is { } nameFilter)
        {
            var normalizedFilter = NameNormalization.Normalize(nameFilter);
            rows = rows.Where(r => r.StockEntry.NormalizedName.Contains(normalizedFilter));
        }

        if (query.Cursor is { } cursor)
        {
            // Keyset resumption strictly after the cursor's order key. The name/Unit/Location triple
            // is already unique within an Inventory, so this comparison alone never skips or repeats
            // a row.
            var name = cursor.OrderKey.NormalizedName;
            var unit = cursor.OrderKey.UnitOrderKey;
            var location = cursor.OrderKey.LocationOrderKey;

            rows = rows.Where(r =>
                string.Compare(r.StockEntry.NormalizedName, name) > 0
                || (r.StockEntry.NormalizedName == name
                    && (string.Compare(r.Unit.NormalizedCanonicalName, unit) > 0
                        || (r.Unit.NormalizedCanonicalName == unit
                            && string.Compare(r.Location == null ? "" : r.Location.NormalizedName, location) > 0))));
        }

        var page = await OrderDeterministically(rows)
            .Take(query.PageSize + 1)
            .ToListAsync(cancellationToken);

        return page.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<StockEntrySummary>> FindMatchesAsync(
        StockFindQuery query, int maxCandidatesPlusOne, CancellationToken cancellationToken)
    {
        var rows = ScopedRows(query.InventoryId);

        if (query.StockEntryId is { } stockEntryId)
        {
            rows = rows.Where(r => r.StockEntry.Id == stockEntryId.Value);
        }
        else
        {
            var normalizedNameReference = query.NormalizedNameReference;
            rows = rows.Where(r => r.StockEntry.NormalizedName == normalizedNameReference);

            if (query.UnitId is { } unitId)
            {
                rows = rows.Where(r => r.StockEntry.UnitId == unitId.Value);
            }

            if (query.UnlocatedOnly)
            {
                rows = rows.Where(r => r.StockEntry.LocationId == null);
            }
            else if (query.LocationId is { } locationId)
            {
                rows = rows.Where(r => r.StockEntry.LocationId == locationId.Value);
            }
        }

        // The candidate cap is applied by the database too: a reference matching thousands of rows
        // still only ever materializes the few needed to decide "one match", "these candidates", or
        // "too many - narrow it down".
        var matches = await OrderDeterministically(rows)
            .Take(maxCandidatesPlusOne)
            .ToListAsync(cancellationToken);

        return matches.Select(ToSummary).ToList();
    }

    /// <summary>
    /// One Stock Entry joined to the Unit it references and, when it is placed somewhere, its
    /// Location. Filtering, ordering, and paging all run against this joined shape, so every one of
    /// them stays inside the single query the database executes.
    /// </summary>
    private sealed class JoinedRow
    {
        public required StockEntryEntity StockEntry { get; init; }

        public required UnitEntity Unit { get; init; }

        public LocationEntity? Location { get; init; }
    }

    private IQueryable<JoinedRow> ScopedRows(InventoryId inventoryId) =>
        from stockEntry in db.StockEntries.AsNoTracking()
        where stockEntry.InventoryId == inventoryId.Value
        join unit in db.Units.AsNoTracking() on stockEntry.UnitId equals unit.Id
        join location in db.Locations.AsNoTracking() on stockEntry.LocationId equals location.Id into locationJoin
        from location in locationJoin.DefaultIfEmpty()
        select new JoinedRow { StockEntry = stockEntry, Unit = unit, Location = location };

    // The trailing id only stabilizes an order the three normalized keys have already decided (they
    // are unique within an Inventory), so its provider-native comparison can never disagree
    // observably with the domain's own ordering.
    private static IQueryable<JoinedRow> OrderDeterministically(IQueryable<JoinedRow> rows) =>
        rows
            .OrderBy(r => r.StockEntry.NormalizedName)
            .ThenBy(r => r.Unit.NormalizedCanonicalName)
            .ThenBy(r => r.Location == null ? "" : r.Location.NormalizedName)
            .ThenBy(r => r.StockEntry.Id);

    private static StockEntrySummary ToSummary(JoinedRow row) => new(
        new StockEntryId(row.StockEntry.Id),
        row.StockEntry.Name,
        row.StockEntry.NormalizedName,
        new UnitId(row.StockEntry.UnitId),
        row.Unit.CanonicalName,
        row.StockEntry.LocationId is { } locationId ? new LocationId(locationId) : null,
        row.Location?.Name,
        row.StockEntry.Note,
        Quantity.Create(row.StockEntry.Quantity));
}
