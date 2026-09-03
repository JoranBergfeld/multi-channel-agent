using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted at the synthetic Turn submission endpoint.</summary>
public sealed record SubmitTurnHttpRequest(
    string? NativeMessageId,
    string? ChannelConversationId,
    string? ContentText,
    string? Locale,
    string? TraceId);

/// <summary>Maps the application boundary's Turn acceptance and Outcome retrieval HTTP endpoints.</summary>
public static class TurnEndpoints
{
    public static IEndpointRouteBuilder MapTurnEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/turns", async (
            SubmitTurnHttpRequest request,
            TurnAcceptanceService acceptanceService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var validationErrors = ValidateSubmitTurnRequest(request);
            if (validationErrors.Count > 0)
            {
                return Results.ValidationProblem(validationErrors);
            }

            var result = await acceptanceService.AcceptAsync(
                new SubmitTurnRequest(request.NativeMessageId!, request.ChannelConversationId!, request.ContentText!, request.Locale, request.TraceId),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return Results.Accepted(
                $"/api/turns/{result.TurnId.Value}/outcome",
                new { turnId = result.TurnId.Value, alreadyAccepted = result.WasAlreadyAccepted });
        });

        endpoints.MapGet("/api/turns/{turnId:guid}/outcome", async (
            Guid turnId,
            TurnOutcomeReader outcomeReader,
            CancellationToken cancellationToken) =>
        {
            var view = await outcomeReader.GetAsync(new TurnId(turnId), cancellationToken);
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

        if (string.IsNullOrWhiteSpace(request.ChannelConversationId))
        {
            errors["channelConversationId"] = ["channelConversationId is required and must not be blank."];
        }

        if (string.IsNullOrWhiteSpace(request.ContentText))
        {
            errors["contentText"] = ["contentText is required and must not be blank."];
        }

        return errors;
    }
}
