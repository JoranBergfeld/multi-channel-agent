namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Participant's Active Inventory selection within one ChannelConversation.
/// Purely a conversational convenience: it never grants access by itself, expires after 30 inactive
/// days, and must be cleared on access loss - every read of it must recheck Membership.
/// </summary>
public sealed class ActiveInventorySelectionEntity
{
    public Guid ParticipantId { get; set; }

    public required string ChannelConversationId { get; set; }

    public Guid InventoryId { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }
}
