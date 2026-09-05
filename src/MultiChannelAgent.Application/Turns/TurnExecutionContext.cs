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
    bool WasInterrupted = false);

/// <summary>
/// Assembles the trusted <see cref="TurnExecutionContext"/> for one claimed Turn: reads back the
/// Foundry conversation generation the Turn was accepted under, and rechecks its Active Inventory
/// selection through <see cref="InventorySelectionService"/> - the same seam the web BFF uses - so
/// access lost since the selection was made is never trusted. This is the sole seam that ever
/// supplies Participant/Inventory identity to tool dispatch; a scripted or real model's own proposed
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
        var captured = await inboxStore.FindCapturedBindingAsync(turn.TurnId, cancellationToken)
            ?? await CurrentBindingAsync(turn, now, cancellationToken);

        var activeInventoryId = await selectionService.GetActiveInventoryIdAsync(
            turn.ParticipantId, turn.ChannelConversationId.Value, now, cancellationToken);

        return new TurnExecutionContext(
            turn.TurnId,
            turn.ParticipantId,
            turn.ChannelConversationId,
            captured.FoundryConversationId,
            captured.Generation,
            activeInventoryId,
            turn.TraceId,

            // Derived from the Turn's own direct content, here, before the model is asked anything -
            // so no proposal the model makes can ever be the reason a mutation was approved.
            DirectConfirmationEvidenceReader.Read(turn),
            turn.WasInterrupted);
    }

    private async Task<CapturedConversationBinding> CurrentBindingAsync(
        InboundTurn turn, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var binding = await bindingStore.GetOrCreateAsync(
            turn.ParticipantId, turn.ChannelConversationId, now, cancellationToken);

        return new CapturedConversationBinding(binding.FoundryConversationId, binding.Generation);
    }
}
