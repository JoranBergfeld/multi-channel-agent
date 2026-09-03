namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A validated Find query: resolves either by opaque Stock Entry id (checked first by the store) or
/// by a normalized name reference with optional exact Unit/Location narrowing - never both at once.
/// Matching itself (the SQL lookup) and interpreting the resulting candidate set
/// (<see cref="StockFindOutcome"/>) both live outside this type; this is only the validated request
/// shape.
/// </summary>
public sealed record StockFindQuery
{
    public required InventoryId InventoryId { get; init; }

    public StockEntryId? StockEntryId { get; init; }

    public string? NormalizedNameReference { get; init; }

    public UnitId? UnitId { get; init; }

    public LocationId? LocationId { get; init; }

    /// <summary>Narrows to Stock kept nowhere in particular; mutually exclusive with <see cref="LocationId"/>.</summary>
    public bool UnlocatedOnly { get; init; }

    public static StockFindQuery ById(InventoryId inventoryId, StockEntryId stockEntryId) => new()
    {
        InventoryId = inventoryId,
        StockEntryId = stockEntryId,
    };

    public static StockFindQuery ByName(
        InventoryId inventoryId, string? nameReference, UnitId? unitId, LocationId? locationId, bool unlocatedOnly = false)
    {
        if (string.IsNullOrWhiteSpace(nameReference))
        {
            throw new ArgumentException("Value must not be blank.", nameof(nameReference));
        }

        if (unlocatedOnly && locationId is not null)
        {
            throw new ArgumentException("A Location narrowing and an unlocated-only narrowing are mutually exclusive.", nameof(unlocatedOnly));
        }

        return new StockFindQuery
        {
            InventoryId = inventoryId,
            NormalizedNameReference = NameNormalization.Normalize(nameReference),
            UnitId = unitId,
            LocationId = locationId,
            UnlocatedOnly = unlocatedOnly,
        };
    }
}
