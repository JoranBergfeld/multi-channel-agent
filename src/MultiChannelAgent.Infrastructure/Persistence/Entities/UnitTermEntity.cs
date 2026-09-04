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

    /// <summary>
    /// True for the five terms the reserved `each` Unit is born with. Per-term rather than derived
    /// from the Unit, so a fixed alias can never be removed while an alias a Participant later teaches
    /// `each` stays removable.
    /// </summary>
    public bool IsReserved { get; set; }

    /// <summary>
    /// When this term left the active namespace, or null while it is active. Set for every term of a
    /// Unit when that Unit is retired, and for one term when an alias is removed - the row remains
    /// either way, so the audit trail and prior meaning survive.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
