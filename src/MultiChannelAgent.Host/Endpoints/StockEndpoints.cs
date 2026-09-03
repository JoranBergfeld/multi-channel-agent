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
/// </summary>
public static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventories/{inventoryId:guid}/stock", async (
            Guid inventoryId,
            string? includeZero,
            string? locationId,
            string? unlocated,
            string? nameFilter,
            string? cursor,
            ClaimsPrincipal user,
            StockListingService listingService,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var webConversationId = WebConversationCookie.EnsureId(httpContext);

            var result = await listingService.ListAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                new StockListRequest
                {
                    IncludeZero = string.Equals(includeZero, "true", StringComparison.OrdinalIgnoreCase),
                    LocationReference = locationId,
                    UnlocatedOnly = string.Equals(unlocated, "true", StringComparison.OrdinalIgnoreCase),
                    NameFilter = nameFilter,
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
                StockAccessOutcomeKind.ReferenceNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["locationId"] = ["That Location does not exist in this Inventory."] }),
                StockAccessOutcomeKind.Invalid => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["cursor"] = ["cursor is not a valid Stock list cursor, or pageSize/locationId is malformed."] }),
                _ => throw new InvalidOperationException($"Unhandled {nameof(StockAccessOutcomeKind)}: {result.Kind}."),
            };
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        return endpoints;
    }
}
