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

    /// <summary>
    /// Optimistic concurrency guard, regenerated on every role change: a plain compared column - not
    /// a provider-specific rowversion type - so the same concurrency check works identically against
    /// SQLite (fast tests) and SQL Server (production). Ownership transfer and orphan recovery both
    /// read this row, do their business/directory checks, then write conditioned on the value they
    /// read; a concurrent writer changes it first, so the loser's save fails with
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> instead of both
    /// silently succeeding.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}
