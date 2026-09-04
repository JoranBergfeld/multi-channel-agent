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

    /// <summary>
    /// Regenerated on every administrative write. It is what an <c>ExpectedReferenceVersion</c> pins,
    /// so a proposal decided against a Unit nobody holds any more can never land. It is deliberately
    /// not an EF concurrency token: every write to it goes through a guarded ExecuteUpdate rather than
    /// the change tracker.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; }

    /// <summary>When this Unit was withdrawn from matching and assignment, or null while it is active. The row - and the identity - always remain.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
