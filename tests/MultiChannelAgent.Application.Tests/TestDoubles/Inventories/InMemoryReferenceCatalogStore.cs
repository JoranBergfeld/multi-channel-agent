using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IReferenceCatalogStore"/> for Application-layer unit tests. It
/// answers exactly like the SQL store - active-only, ordered by normalized name then identity, and
/// with the same bounded prefix-then-fallback suggestions - and never guesses.
/// </summary>
public sealed class InMemoryReferenceCatalogStore : IReferenceCatalogStore
{
    private sealed record UnitRow(InventoryId InventoryId, UnitCatalogRecord Record, bool Retired);

    private sealed record LocationRow(InventoryId InventoryId, LocationCatalogRecord Record, bool Retired);

    private readonly List<UnitRow> _units = [];
    private readonly List<LocationRow> _locations = [];
    private readonly Dictionary<(ReferenceKind, Guid), int> _stockReferences = [];

    public UnitId AddUnit(
        InventoryId inventoryId, string canonicalName, string[] aliases, bool isReserved = false, bool retired = false)
    {
        var unitId = new UnitId(Guid.NewGuid());
        var terms = new List<UnitTerm> { UnitTerm.Create(canonicalName, isCanonical: true, isReserved) };
        terms.AddRange(aliases.Select(alias => UnitTerm.Create(alias, isCanonical: false, isReserved)));

        _units.Add(new UnitRow(
            inventoryId,
            new UnitCatalogRecord(
                unitId, canonicalName, NameNormalization.Normalize(canonicalName), terms, isReserved, Guid.NewGuid()),
            retired));

        return unitId;
    }

    public LocationId AddLocation(InventoryId inventoryId, string name, bool retired = false)
    {
        var locationId = new LocationId(Guid.NewGuid());

        _locations.Add(new LocationRow(
            inventoryId,
            new LocationCatalogRecord(locationId, name, NameNormalization.Normalize(name), Guid.NewGuid()),
            retired));

        return locationId;
    }

    public void SetStockReferences(ReferenceKind kind, Guid referenceId, int count) =>
        _stockReferences[(kind, referenceId)] = count;

    public Task<IReadOnlyList<UnitCatalogRecord>> ListUnitsAsync(ReferenceListQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<UnitCatalogRecord> page = Ordered(
                _units
                    .Where(row => row.InventoryId == query.InventoryId && !row.Retired)
                    .Select(row => (Key: Key(row.Record.NormalizedCanonicalName, row.Record.Id.Value), row.Record)),
                query)
            .ToList();

        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<LocationCatalogRecord>> ListLocationsAsync(ReferenceListQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<LocationCatalogRecord> page = Ordered(
                _locations
                    .Where(row => row.InventoryId == query.InventoryId && !row.Retired)
                    .Select(row => (Key: Key(row.Record.NormalizedName, row.Record.Id.Value), row.Record)),
                query)
            .ToList();

        return Task.FromResult(page);
    }

    public Task<UnitCatalogRecord?> FindUnitAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken) =>
        Task.FromResult(_units
            .FirstOrDefault(row => row.InventoryId == inventoryId && row.Record.Id == unitId && !row.Retired)?.Record);

    public Task<LocationCatalogRecord?> FindLocationAsync(
        InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        Task.FromResult(_locations
            .FirstOrDefault(row => row.InventoryId == inventoryId && row.Record.Id == locationId && !row.Retired)?.Record);

    public Task<IReadOnlySet<string>> ReadActiveUnitTermsAsync(
        InventoryId inventoryId, UnitId? excluding, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> terms = _units
            .Where(row => row.InventoryId == inventoryId && !row.Retired)
            .SelectMany(row => row.Record.Terms
                .Where(term => !(excluding == row.Record.Id && term.IsCanonical))
                .Select(term => term.NormalizedTerm))
            .ToHashSet(StringComparer.Ordinal);

        return Task.FromResult(terms);
    }

    public Task<IReadOnlySet<string>> ReadActiveLocationNamesAsync(
        InventoryId inventoryId, LocationId? excluding, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> names = _locations
            .Where(row => row.InventoryId == inventoryId && !row.Retired && excluding != row.Record.Id)
            .Select(row => row.Record.NormalizedName)
            .ToHashSet(StringComparer.Ordinal);

        return Task.FromResult(names);
    }

    public Task<int> CountStockReferencesAsync(
        InventoryId inventoryId, ReferenceKind kind, Guid referenceId, CancellationToken cancellationToken) =>
        Task.FromResult(_stockReferences.GetValueOrDefault((kind, referenceId)));

    public Task<IReadOnlyList<string>> SuggestAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken)
    {
        var normalized = NameNormalization.Normalize(reference);

        var candidates = kind == ReferenceKind.Unit
            ? _units
                .Where(row => row.InventoryId == inventoryId && !row.Retired)
                .SelectMany(row => row.Record.Terms.Select(term => (term.NormalizedTerm, Display: term.Term, row.Record.Id.Value)))
                .OrderBy(row => row.NormalizedTerm, StringComparer.Ordinal)
                .ThenBy(row => row.Value)
                .Select(row => (row.NormalizedTerm, row.Display))
                .ToList()
            : _locations
                .Where(row => row.InventoryId == inventoryId && !row.Retired)
                .OrderBy(row => row.Record.NormalizedName, StringComparer.Ordinal)
                .ThenBy(row => row.Record.Id.Value)
                .Select(row => (NormalizedTerm: row.Record.NormalizedName, Display: row.Record.Name))
                .ToList();

        var prefixed = candidates
            .Where(row => normalized.Length > 0 && row.NormalizedTerm.StartsWith(normalized, StringComparison.Ordinal))
            .Take(IReferenceCatalogStore.MaxSuggestions)
            .Select(row => row.Display)
            .ToList();

        IReadOnlyList<string> suggestions = prefixed.Count > 0
            ? prefixed
            : candidates.Take(IReferenceCatalogStore.MaxSuggestions).Select(row => row.Display).ToList();

        return Task.FromResult(suggestions);
    }

    private static ReferenceOrderKey Key(string normalizedName, Guid id) => new(normalizedName, id.ToString("D"));

    private static IEnumerable<TRecord> Ordered<TRecord>(
        IEnumerable<(ReferenceOrderKey Key, TRecord Record)> rows, ReferenceListQuery query)
    {
        var ordered = rows.OrderBy(row => row.Key, ReferenceOrderKey.Comparer);

        var after = query.Cursor is { OrderKey: var cursorKey }
            ? ordered.Where(row => ReferenceOrderKey.Comparer.Compare(row.Key, cursorKey) > 0)
            : ordered.AsEnumerable();

        return after.Take(query.PageSize + 1).Select(row => row.Record);
    }
}
