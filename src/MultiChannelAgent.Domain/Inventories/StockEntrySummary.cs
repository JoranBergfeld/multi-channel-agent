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
/// The stable deterministic display order every List and Find result is returned in: normalized name,
/// then Unit canonical name, then Location name (unlocated stock sorts first), then Stock Entry id as
/// the final tie-breaker so ordering never depends on database row order and opaque list cursors
/// (which encode this same tuple) remain valid across pages.
/// </summary>
public static class StockEntryOrdering
{
    public static readonly IComparer<StockEntrySummary> ByDisplayOrder = Comparer<StockEntrySummary>.Create((a, b) =>
    {
        var byName = string.CompareOrdinal(a.NormalizedName, b.NormalizedName);
        if (byName != 0)
        {
            return byName;
        }

        var byUnit = string.CompareOrdinal(a.UnitCanonicalName, b.UnitCanonicalName);
        if (byUnit != 0)
        {
            return byUnit;
        }

        var byLocation = string.CompareOrdinal(a.LocationName ?? string.Empty, b.LocationName ?? string.Empty);
        if (byLocation != 0)
        {
            return byLocation;
        }

        return a.Id.Value.CompareTo(b.Id.Value);
    });
}
