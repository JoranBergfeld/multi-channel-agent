using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Durable store for the (Participant, ChannelConversation) -> active Foundry conversation generation
/// binding. One web browser-profile conversation maps to one active Foundry conversation generation
/// at a time.
/// </summary>
public interface IFoundryConversationBindingStore
{
    /// <summary>
    /// Returns the existing binding for (<paramref name="participantId"/>, <paramref name="channelConversationId"/>)
    /// when one already exists; otherwise atomically creates and returns a fresh first-generation
    /// binding. Concurrent callers racing this for the same pair must converge on one binding, never
    /// create two.
    /// </summary>
    Task<FoundryConversationBinding> GetOrCreateAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the binding this pair currently holds for the one question
    /// <see cref="Inventories.ConfirmationProposalLifecycle.SettleSupersededConversationAsync"/> asks:
    /// has this conversation moved past the generation a Turn was accepted under? That question is
    /// decided against a reset that may be committing right now, so this read carries a stronger
    /// contract than <see cref="GetOrCreateAsync"/> and exists separately rather than changing it.
    ///
    /// <b>It must serialize against an in-flight rotation of this pair's binding.</b> A caller that
    /// returns while a rotation holds the row uncommitted has answered from a generation that is
    /// already being replaced; an implementation must instead wait for that writer to finish and
    /// answer from what it left behind. Ordinary reads have no such obligation, and must not grow one
    /// - every other caller wants the cheapest current answer, not a queue behind a reset.
    ///
    /// <b>It must never create a binding.</b> Returning null means this pair has none at all, which
    /// means no rotation has ever run for it and nothing can have been superseded. A caller treats
    /// that exactly as "still current" - it is the same answer a first-generation binding would give,
    /// without writing a row that only a read asked for.
    /// </summary>
    Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken);
}
