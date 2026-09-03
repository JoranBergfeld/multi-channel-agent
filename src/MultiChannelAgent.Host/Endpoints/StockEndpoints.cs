using System.Globalization;
using System.Security.Claims;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the authorized Stock projection endpoint the Inventory workspace refetches after a terminal
/// read Outcome arrives through the conversation - the same authorized read the conversational
/// list_stock tool call uses, exposed directly so the workspace never has to round-trip through a
/// Turn to see current Stock.
///
/// It offers exactly the bounds the conversational read offers - on-hand default, name filter, exact
/// Unit and Location references, unlocated-only, page size, and cursor - resolved by the same service,
/// so the workspace and the conversation can never disagree about what a request means or about which
/// answers are even askable.
/// </summary>
public static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventories/{inventoryId:guid}/stock", async (
            Guid inventoryId,
            string? includeZero,
            string? unit,
            string? locationId,
            string? unlocated,
            string? nameFilter,
            string? pageSize,
            string? cursor,
            ClaimsPrincipal user,
            StockListingService listingService,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Parsed here rather than bound as an int? so a non-numeric value is answered with the
            // same RFC 7807 shape (naming pageSize) as an out-of-range one, instead of an opaque 400.
            if (!TryParsePageSize(pageSize, out var parsedPageSize))
            {
                return InvalidPageSize();
            }

            var webConversationId = WebConversationCookie.EnsureId(httpContext);

            var result = await listingService.ListAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                new StockListRequest
                {
                    IncludeZero = string.Equals(includeZero, "true", StringComparison.OrdinalIgnoreCase),
                    UnitReference = unit,
                    LocationReference = locationId,
                    UnlocatedOnly = string.Equals(unlocated, "true", StringComparison.OrdinalIgnoreCase),
                    NameFilter = nameFilter,
                    PageSize = parsedPageSize,
                    Cursor = cursor,
                },
                webConversationId,
                timeProvider.GetUtcNow(),
                cancellationToken);

            // Whether the Inventory does not exist or simply is not authorized for this Participant,
            // the response must be identical: a plain 404, never a distinct signal.
            return result.Kind switch
            {
                StockAccessOutcomeKind.Completed => Results.Ok(result.View),
                StockAccessOutcomeKind.NotFound => Results.NotFound(),
                StockAccessOutcomeKind.Forbidden => Results.NotFound(),

                // A named Unit or Location that does not exist is a problem with that parameter, and
                // saying which one is what lets the caller correct it. Existence is only ever
                // reported inside an Inventory the caller is already authorized for, so this
                // discloses nothing they could not already list.
                StockAccessOutcomeKind.ReferenceNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        [ReferenceParameterName(result.UnresolvedReference)] =
                            [$"That {ReferenceNoun(result.UnresolvedReference)} does not exist in this Inventory."],
                    }),
                StockAccessOutcomeKind.Invalid => InvalidRequest(result.Code),
                _ => throw new InvalidOperationException($"Unhandled {nameof(StockAccessOutcomeKind)}: {result.Kind}."),
            };
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        return endpoints;
    }

    /// <summary>A blank page size means "not asked for" (the bounded default applies); anything non-numeric is rejected.</summary>
    private static bool TryParsePageSize(string? pageSize, out int? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(pageSize))
        {
            return true;
        }

        if (!int.TryParse(pageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        parsed = value;
        return true;
    }

    private static IResult InvalidRequest(string code) => code switch
    {
        "invalid_page_size" => InvalidPageSize(),
        "invalid_cursor" => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["cursor"] = ["cursor is not a valid Stock list cursor, or was issued for a different request."] }),
        "invalid_location_filter" => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["unlocated"] = ["Ask for locationId or for unlocated stock, not both."] }),
        _ => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["query"] = ["That Stock list request could not be understood."] }),
    };

    private static IResult InvalidPageSize() => Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["pageSize"] = [$"pageSize must be a whole number between 1 and {StockListQuery.MaxPageSize}."],
        });

    private static string ReferenceParameterName(StockReferenceKind? reference) =>
        reference == StockReferenceKind.Unit ? "unit" : "locationId";

    private static string ReferenceNoun(StockReferenceKind? reference) =>
        reference == StockReferenceKind.Unit ? "Unit" : "Location";
}
