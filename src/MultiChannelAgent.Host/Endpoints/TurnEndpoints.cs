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
    string? TraceId,
    bool Interrupted = false);

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
                    request.TraceId,
                    request.Interrupted),
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

        group.MapGet("/{turnId:guid}/events", async (
            Guid turnId,
            HttpContext httpContext,
            ClaimsPrincipal user,
            TurnEventReader eventReader,
            CancellationToken cancellationToken) =>
        {
            var participantId = user.GetParticipantId();
            var resumePoint = ServerSentEvents.ReadResumePoint(httpContext.Request, TurnEventSequence.IsIssued);

            // The first page is read before any streaming header is written, so a Turn that does not
            // exist - or belongs to a different Participant - can still be answered with a plain 404,
            // identical in both cases exactly as the Outcome endpoint answers them.
            var firstPage = await eventReader.ReadAfterAsync(new TurnId(turnId), participantId, resumePoint, cancellationToken);

            return firstPage is null
                ? Results.NotFound()
                : new TurnEventStreamResult(new TurnId(turnId), participantId, resumePoint, firstPage);
        });

        return endpoints;
    }

    // Malformed client input must never reach InboundTurn.Create: this is the endpoint's one
    // authoritative place to reject it with a controlled, RFC 7807-shaped 400 instead of letting an
    // ArgumentException (or, further down, a database failure on an over-long column) escape as a
    // 500 no caller can act on. Every bound is the domain's own constant, so this can never drift
    // from what a Turn - or the row behind it - actually accepts.
    private static Dictionary<string, string[]> ValidateSubmitTurnRequest(SubmitTurnHttpRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.NativeMessageId))
        {
            errors["nativeMessageId"] = ["nativeMessageId is required and must not be blank."];
        }
        else if (request.NativeMessageId.Trim().Length > InboundTurn.MaxNativeMessageIdLength)
        {
            errors["nativeMessageId"] = [$"nativeMessageId must not exceed {InboundTurn.MaxNativeMessageIdLength} characters."];
        }

        if (string.IsNullOrWhiteSpace(request.ContentText))
        {
            errors["contentText"] = ["contentText is required and must not be blank."];
        }
        else if (request.ContentText.Trim().Length > TurnContentPart.MaxTextLength)
        {
            errors["contentText"] = [$"contentText must not exceed {TurnContentPart.MaxTextLength} characters."];
        }

        if (request.Locale is { } locale && locale.Trim().Length > InboundTurn.MaxLocaleLength)
        {
            errors["locale"] = [$"locale must not exceed {InboundTurn.MaxLocaleLength} characters."];
        }

        if (request.TraceId is { } traceId && traceId.Trim().Length > InboundTurn.MaxTraceIdLength)
        {
            errors["traceId"] = [$"traceId must not exceed {InboundTurn.MaxTraceIdLength} characters."];
        }

        return errors;
    }
}
