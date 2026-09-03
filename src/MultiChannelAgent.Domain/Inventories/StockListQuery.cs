namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A bounded, validated List query: defaults to On-hand Stock only, an explicit request is required
/// to include zero-quantity Stock Entries, and the page size is always clamped to a safe maximum so
/// no caller can force an unbounded scan. <see cref="Cursor"/>, when supplied, must decode as a valid
/// <see cref="StockListCursor"/> - a malformed cursor is rejected here rather than silently ignored or
/// left for a store to fail on later.
/// </summary>
public sealed record StockListQuery
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public required InventoryId InventoryId { get; init; }

    public required bool IncludeZero { get; init; }

    public UnitId? UnitId { get; init; }

    public LocationId? LocationId { get; init; }

    /// <summary>
    /// Restricts the page to Stock kept nowhere in particular. Unlocated Stock is the absence of a
    /// Location, so it can only be asked for explicitly - never named like a place.
    /// </summary>
    public bool UnlocatedOnly { get; init; }

    public string? NameFilter { get; init; }

    public required int PageSize { get; init; }

    public StockListCursor? Cursor { get; init; }

    /// <summary>
    /// The identity of this request's shape. Cursors are issued against it, and only accepted back
    /// from a request whose shape is identical.
    /// </summary>
    public required StockListQueryShape Shape { get; init; }

    public static StockListQuery Create(
        InventoryId inventoryId,
        bool includeZero,
        UnitId? unitId,
        LocationId? locationId,
        bool unlocatedOnly,
        string? nameFilter,
        int? pageSize,
        string? cursor)
    {
        if (unlocatedOnly && locationId is not null)
        {
            throw new ArgumentException("A Location filter and an unlocated-only filter are mutually exclusive.", nameof(unlocatedOnly));
        }

        var boundedPageSize = pageSize ?? DefaultPageSize;
        if (boundedPageSize < 1 || boundedPageSize > MaxPageSize)
        {
            throw new ArgumentException($"Page size must be between 1 and {MaxPageSize}.", nameof(pageSize));
        }

        if (!StockListCursor.TryDecode(cursor, out var decodedCursor))
        {
            throw new ArgumentException("Cursor is not a valid Stock list cursor.", nameof(cursor));
        }

        var normalizedNameFilter = NormalizeOptional(nameFilter) is { } filter ? NameNormalization.Normalize(filter) : null;
        var shape = StockListQueryShape.Compute(
            inventoryId, includeZero, unitId, locationId, unlocatedOnly, normalizedNameFilter, boundedPageSize);

        // A cursor answers "continue exactly this question". Resuming one against a different
        // question - another Inventory, filter, on-hand setting, page size, or an older meaning of
        // any of them - would return a page that answers neither, so it is refused outright rather
        // than quietly reinterpreted.
        if (decodedCursor is not null && !decodedCursor.Matches(shape))
        {
            throw new ArgumentException("Cursor was issued for a different Stock list request.", nameof(cursor));
        }

        return new StockListQuery
        {
            InventoryId = inventoryId,
            IncludeZero = includeZero,
            UnitId = unitId,
            LocationId = locationId,
            UnlocatedOnly = unlocatedOnly,
            NameFilter = NormalizeOptional(nameFilter),
            PageSize = boundedPageSize,
            Cursor = decodedCursor,
            Shape = shape,
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
