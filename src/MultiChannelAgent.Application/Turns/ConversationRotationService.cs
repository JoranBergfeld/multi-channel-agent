using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// What a channel is told a conversation reset did. <see cref="ClearedPendingConfirmation"/> is
/// there so a Participant who was mid-confirmation is told their proposal stopped being confirmable,
/// rather than discovering it by saying "confirm" into a conversation that no longer has one.
/// </summary>
public sealed record ConversationRotationView(
    string FoundryConversationId, int Generation, bool ClearedPendingConfirmation);

/// <summary>
/// Starts a fresh conversation for one Participant's ChannelConversation. This is the only entry
/// point channels use: the identities involved are always trusted context - the authenticated
/// Participant and their own channel conversation - never anything a request body claimed.
/// </summary>
public sealed class ConversationRotationService(IConversationRotationStore rotationStore)
{
    public async Task<ConversationRotationView> RotateAsync(
        ParticipantId participantId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await rotationStore.RotateAsync(
            participantId, new ChannelConversationId(channelConversationId), now, cancellationToken);

        return new ConversationRotationView(
            result.Binding.FoundryConversationId.ToString(),
            result.Binding.Generation,
            result.ClearedPendingConfirmation);
    }
}
