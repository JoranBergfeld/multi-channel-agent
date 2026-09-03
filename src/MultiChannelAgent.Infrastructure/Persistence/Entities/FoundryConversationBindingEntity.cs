namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row binding one Participant's ChannelConversation to its currently active Foundry
/// conversation generation.
/// </summary>
public sealed class FoundryConversationBindingEntity
{
    public Guid ParticipantId { get; set; }

    public required string ChannelConversationId { get; set; }

    public Guid FoundryConversationId { get; set; }

    public int Generation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
