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
    private readonly Dictionary<(InventoryId, UnitId), string> _unitCanonicalNames = [];
    private readonly Dictionary<(InventoryId, LocationId), string> _locationDisplayNames = [];
    private readonly HashSet<(InventoryId, UnitId)> _retiredUnits = [];
    private readonly HashSet<(InventoryId, LocationId)> _retiredLocations = [];

    /// <summary>How many times a Unit reference was resolved, so a caller's caching claim can be proven rather than trusted.</summary>
    public int UnitResolutionCount { get; private set; }

    /// <summary>How many times a Location reference was resolved. See <see cref="UnitResolutionCount"/>.</summary>
    public int LocationResolutionCount { get; private set; }

    /// <summary>Withdraws a Unit from resolution exactly as retiring it does in SQL: it becomes as unknown as one that never existed.</summary>
    public void RetireUnit(InventoryId inventoryId, UnitId unitId) => _retiredUnits.Add((inventoryId, unitId));

    /// <summary>Withdraws a Location from resolution. See <see cref="RetireUnit"/>.</summary>
    public void RetireLocation(InventoryId inventoryId, LocationId locationId) => _retiredLocations.Add((inventoryId, locationId));

    public void AddUnit(InventoryId inventoryId, UnitId unitId, params string[] terms)
    {
        _units.Add((inventoryId, unitId));
        if (terms.Length > 0)
        {
            // The first term is the canonical name, exactly as a Unit's own canonical name leads its terms.
            _unitCanonicalNames[(inventoryId, unitId)] = terms[0];
        }

        foreach (var term in terms)
        {
            _unitTerms[(inventoryId, NameNormalization.Normalize(term))] = unitId;
        }
    }

    public void AddLocation(InventoryId inventoryId, LocationId locationId, string name)
    {
        _locations.Add((inventoryId, locationId));
        _locationNames[(inventoryId, NameNormalization.Normalize(name))] = locationId;
        _locationDisplayNames[(inventoryId, locationId)] = name;
    }

    public Task<string?> FindUnitCanonicalNameAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken) =>
        Task.FromResult(!_retiredUnits.Contains((inventoryId, unitId))
            && _unitCanonicalNames.TryGetValue((inventoryId, unitId), out var name) ? name : null);

    public Task<string?> FindLocationNameAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        Task.FromResult(!_retiredLocations.Contains((inventoryId, locationId))
            && _locationDisplayNames.TryGetValue((inventoryId, locationId), out var name) ? name : null);

    public Task<UnitId?> ResolveUnitAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        UnitResolutionCount++;

        if (Guid.TryParse(reference, out var id))
        {
            var unitId = new UnitId(id);
            return Task.FromResult<UnitId?>(
                _units.Contains((inventoryId, unitId)) && !_retiredUnits.Contains((inventoryId, unitId)) ? unitId : null);
        }

        return Task.FromResult<UnitId?>(
            _unitTerms.TryGetValue((inventoryId, NameNormalization.Normalize(reference)), out var resolved)
                && !_retiredUnits.Contains((inventoryId, resolved))
                    ? resolved
                    : null);
    }

    public Task<LocationId?> ResolveLocationAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        LocationResolutionCount++;

        if (Guid.TryParse(reference, out var id))
        {
            var locationId = new LocationId(id);
            return Task.FromResult<LocationId?>(
                _locations.Contains((inventoryId, locationId)) && !_retiredLocations.Contains((inventoryId, locationId))
                    ? locationId
                    : null);
        }

        return Task.FromResult<LocationId?>(
            _locationNames.TryGetValue((inventoryId, NameNormalization.Normalize(reference)), out var resolved)
                && !_retiredLocations.Contains((inventoryId, resolved))
                    ? resolved
                    : null);
    }
}
