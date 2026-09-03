using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Participant's role in one Inventory - the sole source of Inventory access
/// authorization. A filtered unique index on (InventoryId) where Role = 'Owner' enforces that an
/// Inventory never has more than one Owner at the database layer.
/// </summary>
public sealed class MembershipEntity
{
    public Guid InventoryId { get; set; }

    public Guid ParticipantId { get; set; }

    public MembershipRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
