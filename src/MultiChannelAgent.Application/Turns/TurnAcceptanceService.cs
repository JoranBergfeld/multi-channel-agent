using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>Result of submitting a Turn: whether it was newly accepted or already durably recorded.</summary>
public sealed record TurnAcceptanceResult(TurnId TurnId, bool WasAlreadyAccepted);

/// <summary>
/// Durably accepts normalized synthetic Turns. Ingress is at-least-once: submitting the same
/// <see cref="SubmitTurnRequest.NativeMessageId"/> again, within the same Participant and
/// ChannelConversation scope, returns the originally recorded Turn identity instead of creating a
/// duplicate, so retries never rerun processing once accepted. The same native id issued in a
/// different scope is a different message and is accepted on its own.
/// </summary>
public sealed class TurnAcceptanceService(IInboxStore inboxStore)
{
    public async Task<TurnAcceptanceResult> AcceptAsync(
        SubmitTurnRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var key = new NativeMessageKey(
            request.ParticipantId, new ChannelConversationId(request.ChannelConversationId), request.NativeMessageId);

        var existing = await inboxStore.FindByNativeMessageIdAsync(key, cancellationToken);
        if (existing is not null)
        {
            return new TurnAcceptanceResult(existing.TurnId, WasAlreadyAccepted: true);
        }

        var turn = InboundTurn.Create(
            request.NativeMessageId,
            request.ParticipantId,
            request.ChannelConversationId,
            request.ContentText,
            request.Locale,
            receivedAt,
            request.TraceId);

        var accepted = await inboxStore.AcceptAsync(turn, cancellationToken);

        return new TurnAcceptanceResult(accepted.Turn.TurnId, accepted.WasAlreadyAccepted);
    }
}
