using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>One active Unit as administration sees it: its identity, its display order key, its full ordered term set, whether it is reserved, and its current version.</summary>
public sealed record UnitCatalogRecord(
    UnitId Id,
    string CanonicalName,
    string NormalizedCanonicalName,
    IReadOnlyList<UnitTerm> Terms,
    bool IsReserved,
    Guid ConcurrencyStamp)
{
    /// <summary>The Unit's aliases, in the order they were added - its canonical name is not one of them.</summary>
    public IReadOnlyList<string> Aliases => [.. Terms.Where(term => !term.IsCanonical).Select(term => term.Term)];
}

/// <summary>One active Location as administration sees it.</summary>
public sealed record LocationCatalogRecord(LocationId Id, string Name, string NormalizedName, Guid ConcurrencyStamp);

/// <summary>
/// Authorized, active-only catalog reads for Unit and Location administration, scoped to one
/// Inventory at a time. Everything here is only ever reached after
/// <see cref="InventoryAuthorizationService"/> has authorized the caller for that Inventory, so this
/// store never itself decides access - and it never returns a retired reference, because a retired
/// reference is exactly as unknown as one that never existed.
/// </summary>
public interface IReferenceCatalogStore
{
    /// <summary>The bound on how many suggestions an unknown reference may offer. Bounded so an answer is reviewable, never a catalog dump.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>
    /// Up to <c>query.PageSize + 1</c> active Units in <see cref="ReferenceOrderKey"/> order,
    /// keyset-paginated strictly after <see cref="ReferenceListQuery.Cursor"/> when present, so the
    /// caller can detect whether more remain without a separate count query.
    /// </summary>
    Task<IReadOnlyList<UnitCatalogRecord>> ListUnitsAsync(ReferenceListQuery query, CancellationToken cancellationToken);

    /// <summary>Up to <c>query.PageSize + 1</c> active Locations. See <see cref="ListUnitsAsync"/>.</summary>
    Task<IReadOnlyList<LocationCatalogRecord>> ListLocationsAsync(ReferenceListQuery query, CancellationToken cancellationToken);

    /// <summary>One active Unit with everything administration needs to plan against it, or null when there is no such active Unit here.</summary>
    Task<UnitCatalogRecord?> FindUnitAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken);

    /// <summary>One active Location, or null when there is no such active Location here.</summary>
    Task<LocationCatalogRecord?> FindLocationAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken);

    /// <summary>
    /// Every normalized term that currently identifies an active Unit in this Inventory. When
    /// <paramref name="excluding"/> names a Unit, that Unit's <em>canonical</em> term is left out -
    /// which is exactly the set a rename must not collide with, since renaming onto its own
    /// canonical form is a display-only change while renaming onto its own alias would be a merge.
    /// </summary>
    Task<IReadOnlySet<string>> ReadActiveUnitTermsAsync(
        InventoryId inventoryId, UnitId? excluding, CancellationToken cancellationToken);

    /// <summary>
    /// Every normalized name that currently identifies an active Location in this Inventory, minus
    /// <paramref name="excluding"/>'s own.
    /// </summary>
    Task<IReadOnlySet<string>> ReadActiveLocationNamesAsync(
        InventoryId inventoryId, LocationId? excluding, CancellationToken cancellationToken);

    /// <summary>
    /// How many Stock Entries in this Inventory reference this Unit or Location. Zero is what makes a
    /// reference retirable; anything else is what blocks it - administration never rewrites stock.
    /// </summary>
    Task<int> CountStockReferencesAsync(
        InventoryId inventoryId, ReferenceKind kind, Guid referenceId, CancellationToken cancellationToken);

    /// <summary>
    /// At most <see cref="MaxSuggestions"/> display names for an unresolved reference: active terms
    /// (or Location names) whose normalized form <em>starts with</em> the normalized reference, in
    /// the one deterministic display order; and when none does, the first
    /// <see cref="MaxSuggestions"/> in that same order.
    ///
    /// Exact-prefix and order only. No edit distance, no phonetics, no ranking - fuzzy matching is
    /// out of scope, and the same input against the same Inventory always yields the same list.
    /// </summary>
    Task<IReadOnlyList<string>> SuggestAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken);
}
