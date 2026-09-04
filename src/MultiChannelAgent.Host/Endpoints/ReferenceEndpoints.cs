using System.Globalization;
using System.Security.Claims;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the two authorized reference projections the Inventory workspace refetches after a terminal
/// Outcome arrives - the same authorized reads the conversational list_units and list_locations tool
/// calls use, resolved by the same service, so the workspace and the conversation can never disagree
/// about which Units and Locations exist.
///
/// Both are Viewer-authorized reads that expose only semantic facts: identities, names, and active
/// aliases. Never a version, never a reserved flag, never a retired row.
/// </summary>
public static class ReferenceEndpoints
{
    public static IEndpointRouteBuilder MapReferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventories/{inventoryId:guid}/units", async (
            Guid inventoryId,
            string? pageSize,
            string? cursor,
            ClaimsPrincipal user,
            ReferenceListingService listingService,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!TryParsePageSize(pageSize, out var parsedPageSize))
            {
                return InvalidPageSize();
            }

            var result = await listingService.ListUnitsAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                parsedPageSize,
                cursor,
                WebConversationCookie.EnsureId(httpContext),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Kind switch
            {
                ReferenceListResultKind.Completed => Results.Ok(result.View),

                // Whether the Inventory does not exist or simply is not authorized for this
                // Participant, the response must be identical: a plain 404, never a distinct signal.
                ReferenceListResultKind.NotFound or ReferenceListResultKind.Forbidden => Results.NotFound(),
                _ => InvalidRequest(result.Code),
            };
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        endpoints.MapGet("/api/inventories/{inventoryId:guid}/locations", async (
            Guid inventoryId,
            string? pageSize,
            string? cursor,
            ClaimsPrincipal user,
            ReferenceListingService listingService,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!TryParsePageSize(pageSize, out var parsedPageSize))
            {
                return InvalidPageSize();
            }

            var result = await listingService.ListLocationsAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                parsedPageSize,
                cursor,
                WebConversationCookie.EnsureId(httpContext),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Kind switch
            {
                ReferenceListResultKind.Completed => Results.Ok(result.View),
                ReferenceListResultKind.NotFound or ReferenceListResultKind.Forbidden => Results.NotFound(),
                _ => InvalidRequest(result.Code),
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
        "invalid_cursor" => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["cursor"] = ["cursor is not valid here, or was issued for a different list."] }),
        _ => InvalidPageSize(),
    };

    private static IResult InvalidPageSize() => Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["pageSize"] = [$"pageSize must be a whole number between 1 and {ReferenceListQuery.MaxPageSize}."],
        });
}
