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

/// <summary>
/// What the signed-in web channel is, as declared to the channel-neutral core: its name, and what it
/// can actually do with an answer. It renders text and shows progress while a Turn is still being
/// processed; it carries no inbound attachments and no voice yet, so neither is declared and neither
/// will be offered.
/// </summary>
public static class WebChannel
{
    public const string Name = "web";

    public const ChannelCapabilities Capabilities = ChannelCapabilities.Text | ChannelCapabilities.RichText | ChannelCapabilities.ProgressEvents;
}

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
            TurnOutcomeReader outcomeReader,
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
                new SubmitTurnRequest(
                    request.NativeMessageId!,
                    participantId,
                    channelConversationId,
                    WebChannel.Name,
                    // Typed evidence of how this Turn's Participant was authenticated - the signed-in
                    // Entra session behind the cookie, never anything the request body claimed.
                    ChannelPrincipal.EntraUser(participantId.Value.ToString(), user.FindFirst("tid")?.Value),
                    WebChannel.Capabilities,
                    request.ContentText!,
                    request.Locale,
                    request.TraceId),
                timeProvider.GetUtcNow(),
                cancellationToken);

            if (result.WasAlreadyAccepted)
            {
                // At-least-once redelivery of a Turn that has already been answered: hand back the
                // recorded terminal Outcome itself rather than an acknowledgement, so a redelivering
                // adapter (or a reconnecting browser) never has to poll for a result the application
                // already holds - and never triggers any reprocessing to obtain it. A duplicate of a
                // Turn still being processed has no recorded result yet and stays an acknowledgement.
                var recorded = await outcomeReader.GetAsync(result.TurnId, participantId, cancellationToken);
                if (recorded is not null)
                {
                    return Results.Ok(recorded);
                }
            }

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
