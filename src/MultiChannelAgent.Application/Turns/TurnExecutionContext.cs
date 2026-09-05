using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// The trusted, application-assembled context every tool dispatch is executed under. Assembled only
/// after a Turn has been durably accepted and claimed for processing, from identities the Turn itself
/// already carries plus a current authorization recheck - never from anything the model proposes.
/// <see cref="ActiveInventoryId"/> is null when the Participant has no (or no longer authorized)
/// Active Inventory selection for this ChannelConversation.
/// <see cref="AcceptedInSupersededConversation"/> says the Participant had already started a new
/// conversation by the time this context was assembled; it is a fact about that moment, never a
/// substitute for re-reading the binding once the Turn has done its work.
/// </summary>
public sealed record TurnExecutionContext(
    TurnId TurnId,
    ParticipantId ParticipantId,
    ChannelConversationId ChannelConversationId,
    FoundryConversationId FoundryConversationId,
    int FoundryConversationGeneration,
    InventoryId? ActiveInventoryId,
    string? TraceId,
    DirectConfirmationEvidence Confirmation = DirectConfirmationEvidence.None,
    bool WasInterrupted = false,
    bool AcceptedInSupersededConversation = false);

/// <summary>
/// Assembles the trusted <see cref="TurnExecutionContext"/> for one claimed Turn: reads back the
/// Foundry conversation generation the Turn was accepted under, compares it with the generation that
/// ChannelConversation currently holds, and rechecks its Active Inventory selection through
/// <see cref="InventorySelectionService"/> - the same seam the web BFF uses - so access lost since
/// the selection was made is never trusted. This is the sole seam that ever supplies
/// Participant/Inventory identity to tool dispatch; a scripted or real model's own proposed
/// arguments are never trusted for either.
/// </summary>
public sealed class TurnExecutionContextFactory(
    IInboxStore inboxStore,
    IFoundryConversationBindingStore bindingStore,
    InventorySelectionService selectionService)
{
    public async Task<TurnExecutionContext> CreateAsync(InboundTurn turn, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The conversation this Turn belongs to was decided when it was accepted, so a reset since
        // then leaves this Turn exactly where it was rather than dragging it into a fresh history.
        // The fallback covers only Turns accepted before that was captured; those predate any reset
        // by definition, so the current binding is the right one for them.
        var captured = await inboxStore.FindCapturedBindingAsync(turn.TurnId, cancellationToken);
        var current = await bindingStore.GetOrCreateAsync(
            turn.ParticipantId, turn.ChannelConversationId, now, cancellationToken);
        var accepted = captured ?? new CapturedConversationBinding(current.FoundryConversationId, current.Generation);

        var activeInventoryId = await selectionService.GetActiveInventoryIdAsync(
            turn.ParticipantId, turn.ChannelConversationId.Value, now, cancellationToken);

        return new TurnExecutionContext(
            turn.TurnId,
            turn.ParticipantId,
            turn.ChannelConversationId,

            // The captured conversation, deliberately: this Turn continues the history it was accepted
            // into, whatever the ChannelConversation has moved on to since.
            accepted.FoundryConversationId,
            accepted.Generation,
            activeInventoryId,
            turn.TraceId,

            // Derived from the Turn's own direct content, here, before the model is asked anything -
            // so no proposal the model makes can ever be the reason a mutation was approved.
            DirectConfirmationEvidenceReader.Read(turn),
            turn.WasInterrupted,

            // True as of this instant only. A reset can still land while this Turn is being processed,
            // which is why nothing that must hold at the END of the Turn may rely on this alone.
            accepted.Generation < current.Generation);
    }
}
