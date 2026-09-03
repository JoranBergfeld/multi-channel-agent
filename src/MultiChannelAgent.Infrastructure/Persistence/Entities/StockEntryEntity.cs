namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Stock Entry. Absence of <see cref="LocationId"/> means unlocated, exactly
/// as the domain expresses it - there is no mirrored sentinel column a caller could forget to
/// maintain, because Equivalent Stock uniqueness is enforced by two filtered unique indexes over this
/// very column (see StockEntryEntityConfiguration).
/// </summary>
public sealed class StockEntryEntity
{
    public Guid Id { get; set; }

    public Guid InventoryId { get; set; }

    public Guid UnitId { get; set; }

    public Guid? LocationId { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public string? Note { get; set; }

    public decimal Quantity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency guard, regenerated on every Quantity change: a plain compared column -
    /// not a provider-specific rowversion type - so the same concurrency behavior holds both against
    /// SQLite (fast tests) and SQL Server (production). A mutation reads this row, decides,
    /// then writes conditioned on the value it read; a concurrent writer changes it first, so the
    /// loser's save fails with
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> instead of both
    /// silently succeeding and one amount being lost.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}
