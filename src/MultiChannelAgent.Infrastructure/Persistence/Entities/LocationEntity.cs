namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Inventory-owned Location. Its name is unique case-insensitively within an
/// Inventory - <see cref="NormalizedName"/> is what the unique index enforces that against.
/// </summary>
public sealed class LocationEntity
{
    public Guid Id { get; set; }

    public Guid InventoryId { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    /// <summary>Regenerated on every administrative write; what an <c>ExpectedReferenceVersion</c> pins. See <c>UnitEntity.ConcurrencyStamp</c>.</summary>
    public Guid ConcurrencyStamp { get; set; }

    /// <summary>When this Location was withdrawn from matching and assignment, or null while it is active.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
