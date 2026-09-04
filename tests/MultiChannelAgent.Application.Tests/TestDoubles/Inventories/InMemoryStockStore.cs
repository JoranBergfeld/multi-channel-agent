using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IStockStore"/> for Application-layer unit tests: applies the same
/// ordering, on-hand default, keyset pagination, and id-first/name-then Find resolution the real SQL
/// store must, over a plain in-memory list instead of a database.
/// </summary>
public sealed class InMemoryStockStore : IStockStore
{
    private readonly List<(InventoryId InventoryId, StockEntrySummary Row)> _rows = [];

    // The optimistic-concurrency version of each row, regenerated on every write exactly as the real
    // column is, so a proposal pinned to a stamp is invalidated by any write - even one that restored
    // the same Quantity.
    private readonly Dictionary<StockEntryId, Guid> _stamps = [];

    public void Add(InventoryId inventoryId, StockEntrySummary row)
    {
        _rows.Add((inventoryId, row));
        _stamps[row.Id] = Guid.NewGuid();
    }

    /// <summary>The current version of one row, or <see cref="Guid.Empty"/> when it is not there.</summary>
    public Guid StampOf(StockEntryId id) => _stamps.TryGetValue(id, out var stamp) ? stamp : Guid.Empty;

    /// <summary>The row with this identity in this Inventory, or null when it is not (or no longer) there.</summary>
    public StockEntrySummary? Find(InventoryId inventoryId, StockEntryId id) =>
        _rows.FirstOrDefault(r => r.InventoryId == inventoryId && r.Row.Id == id).Row;

    /// <summary>Replaces one row's Quantity, returning the row as it now stands, or null when it is not there.</summary>
    public StockEntrySummary? SetQuantity(InventoryId inventoryId, StockEntryId id, Quantity quantity)
    {
        var index = _rows.FindIndex(r => r.InventoryId == inventoryId && r.Row.Id == id);
        if (index < 0)
        {
            return null;
        }

        var updated = _rows[index].Row with { Quantity = quantity };
        _rows[index] = (inventoryId, updated);
        _stamps[id] = Guid.NewGuid();
        return updated;
    }

    /// <summary>Creates a row for one exact Equivalent Stock key, returning it.</summary>
    public StockEntrySummary CreateRow(
        InventoryId inventoryId,
        string name,
        UnitId unitId,
        string unitCanonicalName,
        LocationId? locationId,
        string? locationName,
        string? note,
        Quantity quantity)
    {
        var row = new StockEntrySummary(
            new StockEntryId(Guid.NewGuid()),
            name,
            NameNormalization.Normalize(name),
            unitId,
            unitCanonicalName,
            locationId,
            locationName,
            note,
            quantity);

        _rows.Add((inventoryId, row));
        _stamps[row.Id] = Guid.NewGuid();
        return row;
    }

    public Task<IReadOnlyList<StockEntryVersion>> ReadVersionsAsync(
        InventoryId inventoryId, IReadOnlyList<StockEntryId> stockEntryIds, CancellationToken cancellationToken)
    {
        var wanted = stockEntryIds.ToHashSet();

        IReadOnlyList<StockEntryVersion> versions = _rows
            .Where(r => r.InventoryId == inventoryId && wanted.Contains(r.Row.Id))
            .Select(r => new StockEntryVersion(r.Row.Id, StampOf(r.Row.Id), r.Row.Quantity))
            .ToList();

        return Task.FromResult(versions);
    }

    public Task<IReadOnlyList<StockEntrySummary>> ListPageAsync(StockListQuery query, CancellationToken cancellationToken)
    {
        var candidates = _rows
            .Where(r => r.InventoryId == query.InventoryId)
            .Select(r => r.Row)
            .Where(r => query.IncludeZero || r.Quantity.IsOnHand)
            .Where(r => query.UnitId is null || r.UnitId == query.UnitId)
            .Where(r => !query.UnlocatedOnly || r.LocationId is null)
            .Where(r => query.UnlocatedOnly || query.LocationId is null || r.LocationId == query.LocationId)
            .Where(r => query.NameFilter is null || r.NormalizedName.Contains(NameNormalization.Normalize(query.NameFilter), StringComparison.Ordinal))
            .OrderBy(r => r, StockEntryOrdering.ByDisplayOrder)
            .ToList();

        if (query.Cursor is { } cursor)
        {
            candidates = candidates
                .Where(r => StockEntryOrderKey.From(r).CompareTo(cursor.OrderKey) > 0)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<StockEntrySummary>>(candidates.Take(query.PageSize + 1).ToList());
    }

    public Task<IReadOnlyList<StockEntrySummary>> FindMatchesAsync(StockFindQuery query, int maxCandidatesPlusOne, CancellationToken cancellationToken)
    {
        var ordered = Matches(query).OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).Take(maxCandidatesPlusOne).ToList();
        return Task.FromResult<IReadOnlyList<StockEntrySummary>>(ordered);
    }

    public Task<StockMatchFacets> SummarizeMatchFacetsAsync(StockFindQuery query, int maxFacetValues, CancellationToken cancellationToken)
    {
        var matches = Matches(query).ToList();

        return Task.FromResult(new StockMatchFacets(
            matches.Select(r => r.UnitCanonicalName).Distinct().OrderBy(name => name, StringComparer.Ordinal).Take(maxFacetValues).ToList(),
            matches.Where(r => r.LocationName is not null).Select(r => r.LocationName!).Distinct().OrderBy(name => name, StringComparer.Ordinal).Take(maxFacetValues).ToList(),
            matches.Any(r => r.LocationId is null)));
    }

    private IEnumerable<StockEntrySummary> Matches(StockFindQuery query)
    {
        var scoped = _rows.Where(r => r.InventoryId == query.InventoryId).Select(r => r.Row);

        return query.StockEntryId is { } id
            ? scoped.Where(r => r.Id == id)
            : scoped
                .Where(r => r.NormalizedName == query.NormalizedNameReference)
                .Where(r => query.UnitId is null || r.UnitId == query.UnitId)
                .Where(r => !query.UnlocatedOnly || r.LocationId is null)
                .Where(r => query.UnlocatedOnly || query.LocationId is null || r.LocationId == query.LocationId);
    }
}
