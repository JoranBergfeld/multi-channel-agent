namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One term (canonical name or alias) in a Unit's shared collision-free namespace. Every Inventory's
/// reserved `each` Unit contributes five rows here: the canonical `each` term plus the four fixed
/// alias terms `piece`, `pieces`, `pc`, and `pcs`. The unique index on (InventoryId, NormalizedTerm)
/// enforces that a term identifies at most one active Unit within an Inventory, whether canonical or
/// alias.
/// </summary>
public sealed class UnitTermEntity
{
    public Guid Id { get; set; }

    public Guid InventoryId { get; set; }

    public Guid UnitId { get; set; }

    public required string Term { get; set; }

    public required string NormalizedTerm { get; set; }

    public bool IsCanonical { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
