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
/// Acceptance is also where a Turn's Foundry conversation generation is decided and stamped on it, so
/// a conversation reset can never retroactively move work that was already accepted.
/// </summary>
public sealed class TurnAcceptanceService(IInboxStore inboxStore, IFoundryConversationBindingStore bindingStore)
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

        // Every channel's text-only submission is the same shape: one content part, authored directly
        // by the authenticated Participant in this Turn.
        var turn = InboundTurn.Create(InboundTurnDraft.DirectText(
            request.NativeMessageId,
            request.ParticipantId,
            request.ChannelConversationId,
            request.Channel,
            request.Principal,
            request.Capabilities,
            request.ContentText,
            request.Locale,
            receivedAt,
            request.TraceId,
            request.WasInterrupted));

        // The conversation this Turn belongs to is decided here, at acceptance, and stamped on the
        // Turn itself. Resolving it later - when the Turn is finally claimed - would let a "New
        // conversation" in between silently move this already-accepted work into the fresh history,
        // which is exactly what a reset must not do.
        var binding = await bindingStore.GetOrCreateAsync(
            request.ParticipantId, key.ChannelConversationId, receivedAt, cancellationToken);

        var accepted = await inboxStore.AcceptAsync(turn, binding, cancellationToken);

        return new TurnAcceptanceResult(accepted.Turn.TurnId, accepted.WasAlreadyAccepted);
    }
}
