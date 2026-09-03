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

    public LocationId? LocationId { get; init; }

    public string? NameFilter { get; init; }

    public required int PageSize { get; init; }

    public StockListCursor? Cursor { get; init; }

    public static StockListQuery Create(
        InventoryId inventoryId,
        bool includeZero,
        LocationId? locationId,
        string? nameFilter,
        int? pageSize,
        string? cursor)
    {
        var boundedPageSize = pageSize ?? DefaultPageSize;
        if (boundedPageSize < 1 || boundedPageSize > MaxPageSize)
        {
            throw new ArgumentException($"Page size must be between 1 and {MaxPageSize}.", nameof(pageSize));
        }

        if (!StockListCursor.TryDecode(cursor, out var decodedCursor))
        {
            throw new ArgumentException("Cursor is not a valid Stock list cursor.", nameof(cursor));
        }

        return new StockListQuery
        {
            InventoryId = inventoryId,
            IncludeZero = includeZero,
            LocationId = locationId,
            NameFilter = NormalizeOptional(nameFilter),
            PageSize = boundedPageSize,
            Cursor = decodedCursor,
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
