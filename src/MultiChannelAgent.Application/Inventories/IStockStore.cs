using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// SQL-backed authorized read access to Stock Entries, scoped to one Inventory at a time. All
/// filtering, ordering, and matching happen here against already-validated, trusted parameters - this
/// store is only ever reached after <see cref="InventoryAuthorizationService"/> has authorized the
/// caller for the given <see cref="StockListQuery.InventoryId"/>/<see cref="StockFindQuery.InventoryId"/>,
/// so it never itself decides access.
/// </summary>
public interface IStockStore
{
    /// <summary>
    /// Returns up to <c>query.PageSize + 1</c> rows in <see cref="StockEntryOrdering.ByDisplayOrder"/>,
    /// keyset-paginated strictly after <see cref="StockListQuery.Cursor"/> when present, so the
    /// caller can detect whether more rows remain without a separate count query.
    /// </summary>
    Task<IReadOnlyList<StockEntrySummary>> ListPageAsync(StockListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves matches for <paramref name="query"/>: by opaque Stock Entry id when present, else by
    /// normalized name with optional exact Unit/Location narrowing. Returns up to
    /// <paramref name="maxCandidatesPlusOne"/> matches in <see cref="StockEntryOrdering.ByDisplayOrder"/>
    /// so the caller can detect "more than the cap matched" without a separate count query.
    /// </summary>
    Task<IReadOnlyList<StockEntrySummary>> FindMatchesAsync(StockFindQuery query, int maxCandidatesPlusOne, CancellationToken cancellationToken);
}
