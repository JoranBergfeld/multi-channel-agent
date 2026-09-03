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
/// </summary>
public interface IInventoryReferenceStore
{
    Task<UnitId?> ResolveUnitAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken);

    Task<LocationId?> ResolveLocationAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken);
}
