namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The Inventory a Participant is currently working in for one ChannelConversation. Purely a
/// conversational convenience: it is keyed by (Participant, ChannelConversation), expires after 30
/// inactive days, must be cleared on access loss, and never itself grants authorization - every use
/// must recheck Membership.
/// </summary>
public sealed record ActiveInventorySelection(
    ParticipantId ParticipantId,
    string ChannelConversationId,
    InventoryId InventoryId,
    DateTimeOffset LastActivityAt)
{
    private static readonly TimeSpan InactivityWindow = TimeSpan.FromDays(30);

    public bool IsExpired(DateTimeOffset now) => now - LastActivityAt > InactivityWindow;
}
