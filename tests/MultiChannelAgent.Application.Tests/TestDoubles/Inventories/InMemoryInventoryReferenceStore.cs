using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryReferenceStore"/> for Application-layer unit tests: resolves
/// exactly like the SQL store - by opaque identifier, or by exact normalized name (a Unit also by any
/// of its active terms) - and never guesses.
/// </summary>
public sealed class InMemoryInventoryReferenceStore : IInventoryReferenceStore
{
    private readonly Dictionary<(InventoryId, string), UnitId> _unitTerms = [];
    private readonly Dictionary<(InventoryId, string), LocationId> _locationNames = [];
    private readonly HashSet<(InventoryId, UnitId)> _units = [];
    private readonly HashSet<(InventoryId, LocationId)> _locations = [];

    public void AddUnit(InventoryId inventoryId, UnitId unitId, params string[] terms)
    {
        _units.Add((inventoryId, unitId));
        foreach (var term in terms)
        {
            _unitTerms[(inventoryId, NameNormalization.Normalize(term))] = unitId;
        }
    }

    public void AddLocation(InventoryId inventoryId, LocationId locationId, string name)
    {
        _locations.Add((inventoryId, locationId));
        _locationNames[(inventoryId, NameNormalization.Normalize(name))] = locationId;
    }

    public Task<UnitId?> ResolveUnitAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var id))
        {
            var unitId = new UnitId(id);
            return Task.FromResult<UnitId?>(_units.Contains((inventoryId, unitId)) ? unitId : null);
        }

        return Task.FromResult<UnitId?>(
            _unitTerms.TryGetValue((inventoryId, NameNormalization.Normalize(reference)), out var resolved) ? resolved : null);
    }

    public Task<LocationId?> ResolveLocationAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var id))
        {
            var locationId = new LocationId(id);
            return Task.FromResult<LocationId?>(_locations.Contains((inventoryId, locationId)) ? locationId : null);
        }

        return Task.FromResult<LocationId?>(
            _locationNames.TryGetValue((inventoryId, NameNormalization.Normalize(reference)), out var resolved) ? resolved : null);
    }
}
