using System.Security.Claims;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the signed-in web channel's conversation lifecycle endpoint.
///
/// The request body is deliberately empty: the Participant and the ChannelConversation being reset
/// are always trusted context - the authenticated principal and this browser profile's own web
/// conversation cookie - so there is nothing a caller could send that would not be a way to reset
/// someone else's conversation.
/// </summary>
public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/conversation/new", async (
                HttpContext httpContext,
                ClaimsPrincipal user,
                ConversationRotationService rotationService,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var view = await rotationService.RotateAsync(
                    user.GetParticipantId(),
                    WebConversationCookie.EnsureId(httpContext),
                    timeProvider.GetUtcNow(),
                    cancellationToken);

                return Results.Ok(view);
            })
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}
