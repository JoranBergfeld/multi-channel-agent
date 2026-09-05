using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// What one conversation reset did: the fresh binding it established, and whether it had a pending
/// confirmation to settle.
/// </summary>
public sealed record ConversationRotationResult(FoundryConversationBinding Binding, bool ClearedPendingConfirmation);

/// <summary>
/// Starts a fresh Foundry conversation generation for one Participant's ChannelConversation and
/// settles whatever confirmation was waiting in it - as a single durable operation, because a reset
/// that rotated history without clearing the pending confirmation would leave a "confirm" the
/// Participant can still say pointing at work they have just walked away from.
///
/// What it must NOT touch is as much of the contract as what it must: Membership, the Active
/// Inventory selection, every other conversation, and the Initial Import proposals keyed by
/// (Participant, Inventory) all survive a reset untouched. Starting a new conversation is not
/// signing out.
/// </summary>
public interface IConversationRotationStore
{
    Task<ConversationRotationResult> RotateAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
