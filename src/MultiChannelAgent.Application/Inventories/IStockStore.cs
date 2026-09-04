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

    /// <summary>
    /// Summarizes what actually distinguishes the whole match set for <paramref name="query"/> - the
    /// Units and Locations its matches occupy, bounded to <paramref name="maxFacetValues"/> values
    /// each - so an ambiguous answer can offer narrowing a Participant can really act on rather than
    /// guessing from the few candidates it happened to show. Computed by the database, never by
    /// loading the matches.
    /// </summary>
    Task<StockMatchFacets> SummarizeMatchFacetsAsync(StockFindQuery query, int maxFacetValues, CancellationToken cancellationToken);

    /// <summary>
    /// The current optimistic-concurrency version of each named Stock Entry within
    /// <paramref name="inventoryId"/>. Entries that do not exist, or that belong to another
    /// Inventory, are simply absent - a version can never be read across an Inventory boundary.
    ///
    /// This exists separately from the display projection on purpose: a concurrency stamp is a
    /// persistence concern, and every List row and Find candidate would otherwise carry one into
    /// views and payloads that must never expose it.
    /// </summary>
    Task<IReadOnlyList<StockEntryVersion>> ReadVersionsAsync(
        InventoryId inventoryId, IReadOnlyList<StockEntryId> stockEntryIds, CancellationToken cancellationToken);
}

/// <summary>
/// One Stock Entry's current version and Quantity. The stamp - not the Quantity - is what a proposal
/// pins: an unrelated write that happened to restore the same amount still changes the stamp, and
/// must still invalidate a proposal decided before it.
/// </summary>
public sealed record StockEntryVersion(StockEntryId StockEntryId, Guid ConcurrencyStamp, Quantity Quantity);

/// <summary>
/// What the matches of one Find differ by: the Unit canonical names and Location names they occupy
/// (each bounded and in display order), and whether any of them is kept nowhere in particular.
/// </summary>
public sealed record StockMatchFacets(
    IReadOnlyList<string> UnitCanonicalNames,
    IReadOnlyList<string> LocationNames,
    bool HasUnlocatedMatches);
