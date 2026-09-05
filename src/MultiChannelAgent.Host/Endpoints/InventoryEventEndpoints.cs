using System.Security.Claims;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the Participant-level Inventory invalidation stream. It is deliberately not under
/// <c>/api/inventories</c>: it is scoped to the Participant, not to one Inventory, and which
/// Inventories it reports is exactly what it re-derives on every pass.
/// </summary>
public static class InventoryEventEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // There is no ownership check to make and therefore no non-disclosing 404 to return: the
        // stream reports exactly the Inventories this Participant is authorized for and nothing else,
        // re-derived on every pass, so an unauthorized Inventory is not something it can be asked
        // about in the first place.
        endpoints.MapGet(
                "/api/inventory-events",
                (ClaimsPrincipal user) => (IResult)new InventoryEventStreamResult(user.GetParticipantId()))
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        return endpoints;
    }
}
