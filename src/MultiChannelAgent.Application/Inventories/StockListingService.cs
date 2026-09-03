using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for an authorized Stock read (List today; Find extends it with Ambiguous).</summary>
public enum StockAccessOutcomeKind
{
    Completed,
    Forbidden,
    NotFound,
    Invalid,
}

/// <summary>One Stock Entry row as exposed at the application boundary. Quantity is exact invariant decimal text - never a floating-point number - so no precision is ever lost in transit.</summary>
public sealed record StockRowView(string Id, string Name, string Unit, string? Location, string? Note, string Quantity);

/// <summary>One authorized page of List results, plus the opaque cursor to resume from when <see cref="HasMore"/> is true.</summary>
public sealed record StockListView(IReadOnlyList<StockRowView> Rows, string? NextCursor, bool HasMore);

/// <summary>
/// The semantic result of a List request: never SQL details, versions, or diagnostics - only a
/// typed <see cref="StockAccessOutcomeKind"/>, a machine <see cref="Code"/>, and (only when
/// <see cref="StockAccessOutcomeKind.Completed"/>) the resulting page.
/// </summary>
public sealed record StockListResult(StockAccessOutcomeKind Kind, StockListView? View, string Code);

/// <summary>
/// Lists Stock Entries for one Inventory: defaults to On-hand Stock, bounded/paginated via
/// <see cref="StockListQuery"/>, in the stable deterministic display order every List and Find result
/// shares. Authorization always flows through <see cref="InventoryAuthorizationService"/> so an
/// unauthorized Inventory is indistinguishable from one that does not exist - callers of this service
/// only ever supply an InventoryId already scoped by trusted context, never one taken from an
/// untrusted model-proposed argument.
/// </summary>
public sealed class StockListingService(IStockStore stockStore, InventoryAuthorizationService authorizationService)
{
    public async Task<StockListResult> ListAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        bool includeZero,
        string? locationId,
        string? nameFilter,
        int? pageSize,
        string? cursor,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Viewer, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new StockListResult(StockAccessOutcomeKind.NotFound, null, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new StockListResult(StockAccessOutcomeKind.Forbidden, null, "forbidden");
        }

        StockListQuery query;
        try
        {
            query = StockListQuery.Create(
                inventoryId, includeZero, unitId: null, ParseLocationId(locationId), unlocatedOnly: false, nameFilter, pageSize, cursor);
        }
        catch (ArgumentException)
        {
            return new StockListResult(StockAccessOutcomeKind.Invalid, null, "invalid_query");
        }

        var page = await stockStore.ListPageAsync(query, cancellationToken);
        var hasMore = page.Count > query.PageSize;
        var rows = page.Take(query.PageSize).ToList();
        var nextCursor = hasMore ? StockListCursor.FromRow(rows[^1]).Encode() : null;

        return new StockListResult(
            StockAccessOutcomeKind.Completed,
            new StockListView(rows.Select(ToRowView).ToList(), nextCursor, hasMore),
            "completed");
    }

    private static LocationId? ParseLocationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Guid.TryParse(value, out var guid))
        {
            throw new ArgumentException("locationId must be a GUID.", nameof(value));
        }

        return new LocationId(guid);
    }

    internal static StockRowView ToRowView(StockEntrySummary row) => new(
        row.Id.ToString(), row.Name, row.UnitCanonicalName, row.LocationName, row.Note, row.Quantity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
