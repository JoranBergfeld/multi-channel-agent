using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for an authorized catalog read.</summary>
public enum ReferenceListResultKind
{
    Completed,
    Forbidden,
    NotFound,
    Invalid,
}

/// <summary>One active Unit as exposed at the application boundary: its stable identity, its canonical name, and its active aliases in order.</summary>
public sealed record UnitView(string Id, string Name, IReadOnlyList<string> Aliases);

/// <summary>One active Location. Flat and alias-free by design; unlocated stock is the absence of a reference and never appears here.</summary>
public sealed record LocationView(string Id, string Name);

/// <summary>One authorized page of active Units, plus the opaque cursor to resume from when <see cref="HasMore"/> is true.</summary>
public sealed record UnitListView(IReadOnlyList<UnitView> Units, string? NextCursor, bool HasMore);

/// <summary>One authorized page of active Locations. See <see cref="UnitListView"/>.</summary>
public sealed record LocationListView(IReadOnlyList<LocationView> Locations, string? NextCursor, bool HasMore);

/// <summary>The semantic result of a Unit list. Never SQL detail, versions, reserved flags, or unauthorized existence.</summary>
public sealed record UnitListResult(ReferenceListResultKind Kind, string Code, UnitListView? View = null);

/// <summary>The semantic result of a Location list.</summary>
public sealed record LocationListResult(ReferenceListResultKind Kind, string Code, LocationListView? View = null);

/// <summary>
/// Lists the active Units and Locations of one Inventory: bounded, in the stable deterministic
/// display order both catalog reads share, retired references excluded. Viewer is enough - listing
/// reference data mutates nothing - and authorization always flows through
/// <see cref="InventoryAuthorizationService"/> so an unauthorized Inventory is indistinguishable
/// from one that does not exist.
///
/// This is the one service behind both the conversational <c>list_units</c>/<c>list_locations</c>
/// tools and the web workspace projections, so the conversation and the workspace can never disagree
/// about what exists.
/// </summary>
public sealed class ReferenceListingService(
    IReferenceCatalogStore catalogStore, InventoryAuthorizationService authorizationService)
{
    public async Task<UnitListResult> ListUnitsAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        int? pageSize,
        string? cursor,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return new UnitListResult(refusal.Kind, refusal.Code);
        }

        ReferenceListQuery query;
        try
        {
            query = ReferenceListQuery.Create(inventoryId, ReferenceKind.Unit, pageSize, cursor);
        }
        catch (ArgumentException invalid)
        {
            return new UnitListResult(ReferenceListResultKind.Invalid, InvalidQueryCode(invalid.ParamName));
        }

        var page = await catalogStore.ListUnitsAsync(query, cancellationToken);
        var hasMore = page.Count > query.PageSize;
        var rows = page.Take(query.PageSize).ToList();

        var nextCursor = hasMore
            ? new ReferenceListCursor(
                ReferenceKind.Unit,
                new ReferenceOrderKey(rows[^1].NormalizedCanonicalName, rows[^1].Id.Value.ToString("D"))).Encode()
            : null;

        return new UnitListResult(
            ReferenceListResultKind.Completed,
            "completed",
            new UnitListView(
                rows.Select(row => new UnitView(row.Id.ToString(), row.CanonicalName, row.Aliases)).ToList(),
                nextCursor,
                hasMore));
    }

    public async Task<LocationListResult> ListLocationsAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        int? pageSize,
        string? cursor,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return new LocationListResult(refusal.Kind, refusal.Code);
        }

        ReferenceListQuery query;
        try
        {
            query = ReferenceListQuery.Create(inventoryId, ReferenceKind.Location, pageSize, cursor);
        }
        catch (ArgumentException invalid)
        {
            return new LocationListResult(ReferenceListResultKind.Invalid, InvalidQueryCode(invalid.ParamName));
        }

        var page = await catalogStore.ListLocationsAsync(query, cancellationToken);
        var hasMore = page.Count > query.PageSize;
        var rows = page.Take(query.PageSize).ToList();

        var nextCursor = hasMore
            ? new ReferenceListCursor(
                ReferenceKind.Location,
                new ReferenceOrderKey(rows[^1].NormalizedName, rows[^1].Id.Value.ToString("D"))).Encode()
            : null;

        return new LocationListResult(
            ReferenceListResultKind.Completed,
            "completed",
            new LocationListView(
                rows.Select(row => new LocationView(row.Id.ToString(), row.Name)).ToList(),
                nextCursor,
                hasMore));
    }

    private async Task<(ReferenceListResultKind Kind, string Code)?> AuthorizeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Viewer, channelConversationId, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.NotFound => (ReferenceListResultKind.NotFound, "not_found"),
            InventoryAuthorizationOutcome.Forbidden => (ReferenceListResultKind.Forbidden, "forbidden"),
            _ => null,
        };
    }

    /// <summary>The machine code naming the bound a rejected request violated.</summary>
    internal static string InvalidQueryCode(string? parameterName) => parameterName switch
    {
        "pageSize" => "invalid_page_size",
        "cursor" => "invalid_cursor",
        _ => "invalid_query",
    };
}
