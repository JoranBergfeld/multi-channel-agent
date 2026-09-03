using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// Strongly typed Foundry conversation identity - distinct from <see cref="ChannelConversationId"/>
/// (the native channel's own conversation identifier) and from <see cref="TurnId"/> (one Turn within
/// it). Never itself grants authorization or access to Inventory data.
/// </summary>
public readonly record struct FoundryConversationId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// The durable binding from one Participant's ChannelConversation to its currently active Foundry
/// conversation generation. One web browser-profile conversation maps to exactly one active Foundry
/// conversation generation at a time; <see cref="Generation"/> exists so a later ticket can start a
/// fresh Foundry conversation for the same ChannelConversation (for example after an explicit reset)
/// without losing this binding's history.
/// </summary>
public sealed record FoundryConversationBinding
{
    public required ParticipantId ParticipantId { get; init; }

    public required ChannelConversationId ChannelConversationId { get; init; }

    public required FoundryConversationId FoundryConversationId { get; init; }

    public required int Generation { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static FoundryConversationBinding CreateFirstGeneration(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset createdAt) =>
        new()
        {
            ParticipantId = participantId,
            ChannelConversationId = channelConversationId,
            FoundryConversationId = new FoundryConversationId(Guid.NewGuid()),
            Generation = 1,
            CreatedAt = createdAt,
        };
}
