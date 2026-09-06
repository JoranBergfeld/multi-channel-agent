namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable serialized conversation reference needed to proactively continue one Teams personal
/// chat after the inbound activity request has already completed.
/// </summary>
public sealed class TeamsConversationReferenceEntity
{
    public required string ChannelConversationId { get; set; }

    public required string SerializedReference { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
