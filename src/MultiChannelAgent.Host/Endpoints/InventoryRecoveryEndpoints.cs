using Microsoft.AspNetCore.Antiforgery;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted at the orphan recovery endpoint.</summary>
public sealed record RecoverOwnershipHttpRequest(string? TargetIdentifier);

/// <summary>The orphaned-inventories listing response: the bounded page plus the CSRF token the caller must echo back for the recovery mutation.</summary>
public sealed record OrphanedInventoriesHttpResponse(OrphanedInventoriesPage Page, string CsrfToken);

/// <summary>
/// Maps the Recovery Administrator-only, API-only endpoints: identify orphaned Inventories (bounded,
/// disambiguation-only facts - no stock, no membership roster) and transfer one's ownership. Requires
/// the trusted <see cref="AuthorizationPolicies.InventoryRecoveryAdministrator"/> app-role claim -
/// never the ordinary <see cref="AuthorizationPolicies.ActiveTenantMember"/> policy - so an ordinary
/// signed-in Participant can never reach these, and a Recovery Administrator is never required to
/// also be an ordinary Participant.
/// </summary>
public static class InventoryRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/recovery")
            .RequireAuthorization(AuthorizationPolicies.InventoryRecoveryAdministrator);

        group.MapGet("/orphaned-inventories", async (
            HttpContext httpContext,
            InventoryRecoveryService recoveryService,
            IAntiforgery antiforgery,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var page = await recoveryService.ListOrphanedAsync(timeProvider.GetUtcNow(), cancellationToken);

            // A Recovery Administrator has no session-bootstrap-equivalent read (they are never a
            // Participant), so this listing - the natural "read before you act" call before recovering
            // one - mints the CSRF token the recovery mutation below requires, exactly like session
            // bootstrap does for ordinary Participants.
            var tokens = antiforgery.GetAndStoreTokens(httpContext);

            return Results.Ok(new OrphanedInventoriesHttpResponse(page, tokens.RequestToken!));
        });

        group.MapPost("/inventories/{inventoryId:guid}/recover", async (
            Guid inventoryId,
            RecoverOwnershipHttpRequest request,
            HttpContext httpContext,
            InventoryRecoveryService recoveryService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await recoveryService.RecoverAsync(
                httpContext.User.GetRecoveryActorId(),
                new InventoryId(inventoryId),
                request.TargetIdentifier,
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Outcome switch
            {
                RecoveryRequestOutcome.Recovered => Results.Ok(new { newOwnerDisplayName = result.NewOwnerDisplayName }),
                // Non-disclosing: a healthy Inventory and a nonexistent Inventory id must be
                // indistinguishable to the caller - both a plain 404.
                RecoveryRequestOutcome.NotEligible => Results.NotFound(),
                RecoveryRequestOutcome.TargetNotResolved => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["targetIdentifier"] = ["targetIdentifier must resolve to an exact, active, non-guest tenant member."],
                }),
                RecoveryRequestOutcome.ConcurrentModification => Results.Conflict(
                    new { message = "This Inventory's ownership changed concurrently; please retry." }),
                _ => throw new InvalidOperationException($"Unhandled {nameof(RecoveryRequestOutcome)}: {result.Outcome}"),
            };
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}

