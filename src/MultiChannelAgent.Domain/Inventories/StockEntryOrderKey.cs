namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The deterministic ordering key one Stock Entry row occupies in every List and Find result:
/// normalized name, then the Unit's normalized canonical name, then the Location's normalized name
/// (unlocated Stock uses the empty key, so it always sorts first), then the Stock Entry identity as a
/// final stabilizer. Every component is a normalized string compared ordinally, so the order is
/// exactly reproducible - by this domain, by a paging cursor, and by a database whose ordering
/// columns use a binary collation - rather than depending on a database collation's own locale rules.
///
/// The first three components are already unique within an Inventory (Equivalent Stock uniqueness,
/// plus unique Unit terms and Location names), so <see cref="IdOrderKey"/> never actually decides an
/// order for valid data; it exists so the key stays total even for data that could not be persisted.
/// </summary>
public readonly record struct StockEntryOrderKey(string NormalizedName, string UnitOrderKey, string LocationOrderKey, string IdOrderKey)
    : IComparable<StockEntryOrderKey>
{
    /// <summary>The ordering key for unlocated Stock: it sorts before every named Location.</summary>
    public const string UnlocatedOrderKey = "";

    public static StockEntryOrderKey From(StockEntrySummary row) => new(
        row.NormalizedName,
        NameNormalization.Normalize(row.UnitCanonicalName),
        row.LocationName is null ? UnlocatedOrderKey : NameNormalization.Normalize(row.LocationName),
        row.Id.Value.ToString());

    public int CompareTo(StockEntryOrderKey other)
    {
        var byName = string.CompareOrdinal(NormalizedName, other.NormalizedName);
        if (byName != 0)
        {
            return byName;
        }

        var byUnit = string.CompareOrdinal(UnitOrderKey, other.UnitOrderKey);
        if (byUnit != 0)
        {
            return byUnit;
        }

        var byLocation = string.CompareOrdinal(LocationOrderKey, other.LocationOrderKey);
        if (byLocation != 0)
        {
            return byLocation;
        }

        return string.CompareOrdinal(IdOrderKey, other.IdOrderKey);
    }
}
