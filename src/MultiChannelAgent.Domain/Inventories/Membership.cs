namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The three Inventory roles. Every Inventory begins with exactly one Owner; this ticket only creates,
/// lists, and selects Inventories, not membership administration.
/// </summary>
public enum MembershipRole
{
    Owner = 0,
    Editor = 1,
    Viewer = 2,
}

/// <summary>One Participant's role in one Inventory - the sole source of Inventory access authorization.</summary>
public sealed record Membership
{
    public required InventoryId InventoryId { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required MembershipRole Role { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static Membership CreateOwner(InventoryId inventoryId, ParticipantId participantId, DateTimeOffset createdAt) => new()
    {
        InventoryId = inventoryId,
        ParticipantId = participantId,
        Role = MembershipRole.Owner,
        CreatedAt = createdAt,
    };
}
