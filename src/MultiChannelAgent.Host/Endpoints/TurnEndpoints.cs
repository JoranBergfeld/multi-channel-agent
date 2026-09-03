using System.Security.Claims;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// The wire shape accepted at the synthetic Turn submission endpoint. Deliberately carries no
/// Participant or conversation identity: those are always trusted application context derived from
/// the authenticated principal and the web conversation cookie, never accepted from the client body.
/// </summary>
public sealed record SubmitTurnHttpRequest(
    string? NativeMessageId,
    string? ContentText,
    string? Locale,
    string? TraceId);

/// <summary>Maps the application boundary's Turn acceptance and Outcome retrieval HTTP endpoints.</summary>
public static class TurnEndpoints
{
    public static IEndpointRouteBuilder MapTurnEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/turns")
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        group.MapPost("/", async (
            SubmitTurnHttpRequest request,
            HttpContext httpContext,
            ClaimsPrincipal user,
            TurnAcceptanceService acceptanceService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var validationErrors = ValidateSubmitTurnRequest(request);
            if (validationErrors.Count > 0)
            {
                return Results.ValidationProblem(validationErrors);
            }

            var participantId = user.GetParticipantId();
            var channelConversationId = WebConversationCookie.EnsureId(httpContext);

            var result = await acceptanceService.AcceptAsync(
                new SubmitTurnRequest(request.NativeMessageId!, participantId, channelConversationId, request.ContentText!, request.Locale, request.TraceId),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return Results.Accepted(
                $"/api/turns/{result.TurnId.Value}/outcome",
                new { turnId = result.TurnId.Value, alreadyAccepted = result.WasAlreadyAccepted });
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapGet("/{turnId:guid}/outcome", async (
            Guid turnId,
            ClaimsPrincipal user,
            TurnOutcomeReader outcomeReader,
            CancellationToken cancellationToken) =>
        {
            // Whether the Turn does not exist or simply belongs to a different Participant, the
            // response must be identical: a plain 404, never a distinct signal that would let a
            // caller infer another Participant's Turn exists.
            var view = await outcomeReader.GetAsync(new TurnId(turnId), user.GetParticipantId(), cancellationToken);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });

        return endpoints;
    }

    // Missing/null/blank required fields must never reach InboundTurn.Create: this is the endpoint's
    // one authoritative place to reject malformed client input with a controlled, RFC 7807-shaped 400
    // instead of letting an ArgumentException (or, before the domain guard, an NRE) escape as a 500.
    private static Dictionary<string, string[]> ValidateSubmitTurnRequest(SubmitTurnHttpRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.NativeMessageId))
        {
            errors["nativeMessageId"] = ["nativeMessageId is required and must not be blank."];
        }

        if (string.IsNullOrWhiteSpace(request.ContentText))
        {
            errors["contentText"] = ["contentText is required and must not be blank."];
        }

        return errors;
    }
}
