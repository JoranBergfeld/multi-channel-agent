using System.Security.Cryptography;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The identity of one List request's shape: the Inventory it reads and every filter and bound that
/// decides which rows, in which order, a page is drawn from. A paging cursor carries this shape, so
/// resuming with a cursor minted for a different question - a different Inventory, filter, on-hand
/// setting, or page size - is rejected instead of silently returning a page that answers neither
/// question. <see cref="Version"/> makes the same true across releases: a cursor minted before the
/// query shape's meaning changed can never be resumed against the new meaning.
/// </summary>
public readonly record struct StockListQueryShape(int Version, string Token)
{
    /// <summary>
    /// The current query-shape version. Increment it whenever the meaning of any component below
    /// changes (for example if "unlocated" or a filter's semantics change), so previously issued
    /// cursors stop being accepted rather than resuming against a different meaning.
    /// </summary>
    public const int CurrentVersion = 1;

    public static StockListQueryShape Compute(
        InventoryId inventoryId,
        bool includeZero,
        UnitId? unitId,
        LocationId? locationId,
        bool unlocatedOnly,
        string? normalizedNameFilter,
        int pageSize)
    {
        var components = string.Join(
            '|',
            inventoryId.Value.ToString(),
            includeZero ? "zero" : "onhand",
            unitId?.Value.ToString() ?? "-",
            locationId?.Value.ToString() ?? "-",
            unlocatedOnly ? "unlocated" : "anywhere",
            normalizedNameFilter ?? "-",
            pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(components));

        // Truncated purely to keep the cursor short: this is a shape check, never a security
        // boundary - the cursor is opaque, not secret, and carries only data the caller already saw.
        return new StockListQueryShape(CurrentVersion, Convert.ToHexString(digest)[..16].ToLowerInvariant());
    }
}
