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
}
