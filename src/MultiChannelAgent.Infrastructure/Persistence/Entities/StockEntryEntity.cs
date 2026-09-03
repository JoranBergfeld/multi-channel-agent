namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Stock Entry. <see cref="LocationUniquenessKey"/> mirrors
/// <see cref="LocationId"/> but is never null (<see cref="Guid.Empty"/> stands in for "unlocated") so
/// the Equivalent Stock unique index below behaves correctly - SQL Server treats every NULL in a
/// unique index as distinct from every other NULL, which would otherwise let multiple unlocated rows
/// for the same normalized name and Unit slip past it.
/// </summary>
public sealed class StockEntryEntity
{
    public Guid Id { get; set; }

    public Guid InventoryId { get; set; }

    public Guid UnitId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid LocationUniquenessKey { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public string? Note { get; set; }

    public decimal Quantity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
