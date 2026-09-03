using System.Security.Claims;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted at the Inventory creation endpoint.</summary>
public sealed record CreateInventoryHttpRequest(string? Name, string? ClientRequestId);

/// <summary>The wire shape accepted at the Inventory selection endpoint.</summary>
public sealed record SelectInventoryHttpResponse(string InventoryId);

/// <summary>Maps Inventory creation, listing, and selection HTTP endpoints behind the active-tenant-member policy.</summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/inventories")
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        group.MapGet("/", async (
            ClaimsPrincipal user,
            InventoryListingService listingService,
            CancellationToken cancellationToken) =>
        {
            var views = await listingService.ListAuthorizedAsync(user.GetParticipantId(), cancellationToken);
            return Results.Ok(views);
        });

        group.MapPost("/", async (
            CreateInventoryHttpRequest request,
            ClaimsPrincipal user,
            InventoryCreationService creationService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["name is required and must not be blank."];
            }

            if (string.IsNullOrWhiteSpace(request.ClientRequestId))
            {
                errors["clientRequestId"] = ["clientRequestId is required and must not be blank."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var view = await creationService.CreateAsync(
                user.GetParticipantId(),
                user.GetDisplayName(),
                request.Name!,
                request.ClientRequestId!,
                timeProvider.GetUtcNow(),
                cancellationToken);

            return Results.Ok(view);
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/{inventoryId:guid}/select", async (
            Guid inventoryId,
            HttpContext httpContext,
            ClaimsPrincipal user,
            InventorySelectionService selectionService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var webConversationId = WebConversationCookie.EnsureId(httpContext);

            var result = await selectionService.SelectAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                webConversationId,
                timeProvider.GetUtcNow(),
                cancellationToken);

            // Whether the Inventory does not exist or simply is not authorized for this Participant,
            // the response must be identical: a plain 404, never a distinct signal that would let a
            // caller infer an unauthorized Inventory's existence.
            return result.Outcome == InventorySelectionOutcome.Selected
                ? Results.Ok(new SelectInventoryHttpResponse(result.InventoryId!.Value.ToString()))
                : Results.NotFound();
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}
