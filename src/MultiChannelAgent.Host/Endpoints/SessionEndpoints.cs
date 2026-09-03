using Microsoft.AspNetCore.Antiforgery;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>Session bootstrap response: the Application-owned bootstrap view plus the CSRF token the client must echo back for mutating requests.</summary>
public sealed record BootstrapHttpResponse(BootstrapView Bootstrap, string CsrfToken);

/// <summary>Maps the authenticated session bootstrap endpoint.</summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/session/bootstrap", async (
            HttpContext httpContext,
            InventoryBootstrapService bootstrapService,
            IAntiforgery antiforgery,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var participantId = httpContext.User.GetParticipantId();
            var displayName = httpContext.User.GetDisplayName();
            var webConversationId = WebConversationCookie.EnsureId(httpContext);

            var bootstrap = await bootstrapService.BootstrapAsync(
                participantId,
                displayName,
                webConversationId,
                timeProvider.GetUtcNow(),
                cancellationToken);

            var tokens = antiforgery.GetAndStoreTokens(httpContext);

            return Results.Ok(new BootstrapHttpResponse(bootstrap, tokens.RequestToken!));
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        return endpoints;
    }
}
