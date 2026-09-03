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

    /// <summary>
    /// The canonical name normalized the same way every other name in this domain is. It is the
    /// Unit component of a Stock Entry's deterministic order key, so List and Find can order in SQL
    /// exactly as the domain does.
    /// </summary>
    public required string NormalizedCanonicalName { get; set; }

    public bool IsReserved { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
