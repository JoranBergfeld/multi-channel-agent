namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The deterministic display order both catalog lists share: normalized name, then identity, both
/// compared ordinally so the database's order is the domain's order rather than a locale-dependent
/// approximation of it.
///
/// The normalized name alone is already unique among the active references of one Inventory - the
/// filtered unique indexes guarantee it - so <see cref="IdOrderKey"/> never actually decides an
/// order for valid data; it exists so the key stays total, exactly as
/// <see cref="StockEntryOrderKey.IdOrderKey"/> does.
/// </summary>
public sealed record ReferenceOrderKey(string NormalizedName, string IdOrderKey)
{
    public static readonly IComparer<ReferenceOrderKey> Comparer = Comparer<ReferenceOrderKey>.Create((left, right) =>
    {
        var byName = string.CompareOrdinal(left.NormalizedName, right.NormalizedName);

        return byName != 0 ? byName : string.CompareOrdinal(left.IdOrderKey, right.IdOrderKey);
    });
}

/// <summary>
/// One bounded, validated catalog read. Callers only ever supply an InventoryId already scoped by
/// trusted context; this type owns the bounds, and refuses a cursor issued for the other kind of
/// reference so a page marker can only ever continue the question that produced it.
/// </summary>
public sealed record ReferenceListQuery
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public required InventoryId InventoryId { get; init; }

    public required ReferenceKind Kind { get; init; }

    public required int PageSize { get; init; }

    public ReferenceListCursor? Cursor { get; init; }

    public static ReferenceListQuery Create(InventoryId inventoryId, ReferenceKind kind, int? pageSize, string? cursor)
    {
        var boundedPageSize = pageSize ?? DefaultPageSize;
        if (boundedPageSize < 1 || boundedPageSize > MaxPageSize)
        {
            throw new ArgumentException($"Page size must be between 1 and {MaxPageSize}.", nameof(pageSize));
        }

        if (!ReferenceListCursor.TryDecode(cursor, out var decoded))
        {
            throw new ArgumentException("Cursor is not a valid reference list cursor.", nameof(cursor));
        }

        if (decoded is not null && !decoded.Matches(kind))
        {
            throw new ArgumentException("Cursor was issued for a different reference list.", nameof(cursor));
        }

        return new ReferenceListQuery
        {
            InventoryId = inventoryId,
            Kind = kind,
            PageSize = boundedPageSize,
            Cursor = decoded,
        };
    }
}
