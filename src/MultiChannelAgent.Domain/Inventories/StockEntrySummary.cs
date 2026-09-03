namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A read/display projection of one Stock Entry, denormalized with its Unit's canonical name and
/// Location's name (when present) so display and the deterministic display order
/// (<see cref="StockEntryOrdering"/>) never need a further join. Used for both List rows and Find
/// candidates.
/// </summary>
public sealed record StockEntrySummary(
    StockEntryId Id,
    string Name,
    string NormalizedName,
    UnitId UnitId,
    string UnitCanonicalName,
    LocationId? LocationId,
    string? LocationName,
    string? Note,
    Quantity Quantity);

/// <summary>
/// The stable deterministic display order every List and Find result is returned in, defined by
/// <see cref="StockEntryOrderKey"/> so this domain, an opaque paging cursor, and the database itself
/// all agree on it exactly.
/// </summary>
public static class StockEntryOrdering
{
    public static readonly IComparer<StockEntrySummary> ByDisplayOrder =
        Comparer<StockEntrySummary>.Create((a, b) => StockEntryOrderKey.From(a).CompareTo(StockEntryOrderKey.From(b)));
}
