namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Inventory-owned Unit. Every Inventory starts with exactly one reserved
/// Unit (`each`); its stable <see cref="Id"/> is preserved for a later Unit-administration ticket to
/// rename without rewriting Stock Entry references.
/// </summary>
public sealed class UnitEntity
{
    public Guid Id { get; set; }

    public Guid InventoryId { get; set; }

    public required string CanonicalName { get; set; }

    public bool IsReserved { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
