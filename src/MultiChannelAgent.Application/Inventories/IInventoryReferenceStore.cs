using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>One active Unit a batched import lookup found for a normalized term, plus the canonical name a preview must show instead of the raw term.</summary>
public sealed record ResolvedUnitReference(UnitId Id, string CanonicalName);

/// <summary>One active Location a batched import lookup found for a normalized name, plus its display name. See <see cref="ResolvedUnitReference"/>.</summary>
public sealed record ResolvedLocationReference(LocationId Id, string Name);

/// <summary>
/// Resolves the Inventory-owned Unit and Location references a request names, exactly and
/// deterministically: an opaque identifier, or an exact name - for a Unit, any of its active terms
/// (its canonical name or an alias). Nothing is guessed, fuzzy-matched, or created: an unresolvable
/// reference is simply absent, and the caller answers <c>reference_not_found</c>.
///
/// Resolution is always scoped to one Inventory, so a reference can never reach across Inventory
/// boundaries, and this store is only ever reached after the caller has been authorized for that
/// Inventory.
///
/// Resolution is also <b>active-only</b>: a retired Unit, a retired term, and a retired Location all
/// resolve to nothing, exactly like one that never existed. That is what makes "retired Units and
/// Locations are excluded from matching" true for every caller at once - stock reads, stock
/// mutations, and later Import - rather than a rule each of them has to remember.
/// </summary>
public interface IInventoryReferenceStore
{
    Task<UnitId?> ResolveUnitAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken);

    Task<LocationId?> ResolveLocationAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken);

    /// <summary>
    /// The canonical name of an active Unit in this Inventory, or null when there is no such Unit
    /// here. A proposal reports this rather than the alias or the raw text a request happened to use,
    /// so what a Participant reviews is the name the Inventory actually holds.
    /// </summary>
    Task<string?> FindUnitCanonicalNameAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken);

    /// <summary>The name of an active Location in this Inventory, or null when there is no such Location here. See <see cref="FindUnitCanonicalNameAsync"/>.</summary>
    Task<string?> FindLocationNameAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves many Unit terms at once, for Initial Import: a 5,000-row file can name up to 5,000
    /// distinct terms, and one round trip per distinct term - even with per-term caching - is still up
    /// to 5,000 round trips. This is the bounded alternative: one call resolves every distinct term a
    /// file names.
    ///
    /// <paramref name="normalizedTerms"/> must already be <see cref="NameNormalization.Normalize"/>d
    /// and distinct - the caller (<see cref="ImportReferenceResolver"/>) owns folding a file's varied
    /// casing and whitespace to one key before ever reaching this store, exactly as the single-term
    /// <see cref="ResolveUnitAsync"/> path does internally.
    ///
    /// Scoped to one Inventory and active-only exactly like <see cref="ResolveUnitAsync"/>: an inactive
    /// Unit or an inactive term contributes nothing, whichever query answers. Nothing is ever created -
    /// a term absent from the result is exactly as unknown as one <see cref="ResolveUnitAsync"/> would
    /// have returned null for.
    ///
    /// Unlike <see cref="ResolveUnitAsync"/>, a term here is always a canonical name or an alias, never
    /// an opaque identifier: Initial Import accepts only the names a file can type, not an internal Id.
    ///
    /// The result is keyed by the exact normalized term supplied, so a caller maps every row's own
    /// normalized term back to what this returned in one dictionary lookup, without renormalizing or
    /// re-deriving which input produced which entry. An empty <paramref name="normalizedTerms"/> is
    /// answered with an empty map and never reaches the database.
    /// </summary>
    Task<IReadOnlyDictionary<string, ResolvedUnitReference>> ResolveUnitsAsync(
        InventoryId inventoryId, IReadOnlyCollection<string> normalizedTerms, CancellationToken cancellationToken);

    /// <summary>Resolves many Location names at once. See <see cref="ResolveUnitsAsync"/> for the scoping, active-only, no-creation, and keying rules, which all apply identically here.</summary>
    Task<IReadOnlyDictionary<string, ResolvedLocationReference>> ResolveLocationsAsync(
        InventoryId inventoryId, IReadOnlyCollection<string> normalizedNames, CancellationToken cancellationToken);
}
