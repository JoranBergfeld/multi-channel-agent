using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

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
}
