using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for an authorized Stock read (List today; Find extends it with Ambiguous).</summary>
public enum StockAccessOutcomeKind
{
    Completed,
    Forbidden,
    NotFound,

    /// <summary>A named Unit or Location does not exist in this Inventory - it is never created implicitly.</summary>
    ReferenceNotFound,
    Invalid,
}

/// <summary>One Stock Entry row as exposed at the application boundary. Quantity is exact invariant decimal text - never a floating-point number - so no precision is ever lost in transit.</summary>
public sealed record StockRowView(string Id, string Name, string Unit, string? Location, string? Note, string Quantity);

/// <summary>One authorized page of List results, plus the opaque cursor to resume from when <see cref="HasMore"/> is true.</summary>
public sealed record StockListView(IReadOnlyList<StockRowView> Rows, string? NextCursor, bool HasMore);

/// <summary>Which named reference of a Stock read could not be resolved, so a caller can say which one is at fault.</summary>
public enum StockReferenceKind
{
    Unit,
    Location,
}

/// <summary>
/// The semantic result of a List request: never SQL details, versions, or diagnostics - only a
/// typed <see cref="StockAccessOutcomeKind"/>, a machine <see cref="Code"/>, and (only when
/// <see cref="StockAccessOutcomeKind.Completed"/>) the resulting page.
/// <see cref="UnresolvedReference"/> is present exactly when
/// <see cref="StockAccessOutcomeKind.ReferenceNotFound"/> is, so an answer can name the reference
/// that did not resolve instead of leaving a caller to guess between them.
/// </summary>
public sealed record StockListResult(
    StockAccessOutcomeKind Kind, StockListView? View, string Code, StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// One List request's bounds, as named by the caller. <see cref="UnitReference"/> and
/// <see cref="LocationReference"/> are exact references (an opaque identifier, or an exact name -
/// for a Unit also an active alias) that this service resolves against the Inventory; they are never
/// pattern-matched. <see cref="UnlocatedOnly"/> asks for Stock kept nowhere in particular, which can
/// only be requested explicitly because it is the absence of a Location rather than a place.
/// </summary>
public sealed record StockListRequest
{
    public bool IncludeZero { get; init; }

    public string? UnitReference { get; init; }

    public string? LocationReference { get; init; }

    public bool UnlocatedOnly { get; init; }

    public string? NameFilter { get; init; }

    public int? PageSize { get; init; }

    public string? Cursor { get; init; }
}

/// <summary>
/// Lists Stock Entries for one Inventory: defaults to On-hand Stock, bounded and filtered by
/// <see cref="StockListRequest"/>, in the stable deterministic display order every List and Find
/// result shares. Authorization always flows through <see cref="InventoryAuthorizationService"/> so an
/// unauthorized Inventory is indistinguishable from one that does not exist - callers of this service
/// only ever supply an InventoryId already scoped by trusted context, never one taken from an
/// untrusted model-proposed argument.
/// </summary>
public sealed class StockListingService(
    IStockStore stockStore,
    IInventoryReferenceStore referenceStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<StockListResult> ListAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        StockListRequest request,
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

        UnitId? unitId = null;
        if (!string.IsNullOrWhiteSpace(request.UnitReference))
        {
            unitId = await referenceStore.ResolveUnitAsync(inventoryId, request.UnitReference, cancellationToken);
            if (unitId is null)
            {
                return new StockListResult(StockAccessOutcomeKind.ReferenceNotFound, null, "reference_not_found", StockReferenceKind.Unit);
            }
        }

        LocationId? locationId = null;
        if (!string.IsNullOrWhiteSpace(request.LocationReference))
        {
            locationId = await referenceStore.ResolveLocationAsync(inventoryId, request.LocationReference, cancellationToken);
            if (locationId is null)
            {
                return new StockListResult(StockAccessOutcomeKind.ReferenceNotFound, null, "reference_not_found", StockReferenceKind.Location);
            }
        }

        StockListQuery query;
        try
        {
            query = StockListQuery.Create(
                inventoryId,
                request.IncludeZero,
                unitId,
                locationId,
                request.UnlocatedOnly,
                request.NameFilter,
                request.PageSize,
                request.Cursor);
        }
        catch (ArgumentException invalid)
        {
            // Which bound was violated is exactly what a caller needs in order to correct the
            // request, so it is reported rather than flattened into one opaque "invalid".
            return new StockListResult(StockAccessOutcomeKind.Invalid, null, InvalidQueryCode(invalid.ParamName));
        }

        var page = await stockStore.ListPageAsync(query, cancellationToken);
        var hasMore = page.Count > query.PageSize;
        var rows = page.Take(query.PageSize).ToList();

        // The cursor is issued against this exact request's shape, so resuming it can only ever
        // continue this same question.
        var nextCursor = hasMore ? StockListCursor.FromRow(rows[^1], query.Shape).Encode() : null;

        return new StockListResult(
            StockAccessOutcomeKind.Completed,
            new StockListView(rows.Select(ToRowView).ToList(), nextCursor, hasMore),
            "completed");
    }

    /// <summary>The machine code naming the bound a rejected request violated.</summary>
    internal static string InvalidQueryCode(string? parameterName) => parameterName switch
    {
        "pageSize" => "invalid_page_size",
        "cursor" => "invalid_cursor",
        "unlocatedOnly" => "invalid_location_filter",
        _ => "invalid_query",
    };

    /// <summary>
    /// The one place a Stock row is shaped for the application boundary - List and Find both use it -
    /// so an amount is always rendered through <see cref="Quantity.ToInvariantText"/> and can never
    /// reach a caller carrying whatever scale a particular database stored it at.
    /// </summary>
    internal static StockRowView ToRowView(StockEntrySummary row) => new(
        row.Id.ToString(),
        row.Name,
        row.UnitCanonicalName,
        row.LocationName,
        row.Note,
        row.Quantity.ToInvariantText());
}
