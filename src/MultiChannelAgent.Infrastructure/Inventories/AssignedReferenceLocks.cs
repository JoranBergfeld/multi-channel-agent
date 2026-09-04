using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// The one protocol every Stock write shares for the Units and Locations it assigns.
///
/// A Stock write is decided long before it lands: the Application layer resolves a reference
/// active-only while planning, and a confirmed change set may have been reviewed minutes earlier. A
/// Retire committing in that window would leave the write holding a decision that is no longer true,
/// and #33's sixth acceptance criterion - a confirmed Retire never leaves Stock referencing what it
/// retired - would be false. Pinning Stock Entry versions does not help: retiring a reference does
/// not touch a single Stock Entry.
///
/// So every write that assigns a reference re-reads it here, inside its own transaction, and keeps
/// the read lock until it commits. That closes the window from both sides:
///
/// <list type="bullet">
/// <item>If the Retire committed first, the row reads back retired and the write refuses before
/// touching any Stock.</item>
/// <item>If this write got its lock first, <see cref="SqlReferenceAdministrationStore"/>'s guarded
/// update of that very row blocks until this transaction commits - and its Retire then finds the
/// Stock and answers <c>reference_in_use</c>.</item>
/// </list>
///
/// The lock order matches <see cref="SqlReferenceAdministrationStore"/> exactly - Units before
/// Locations, then the ordinal text of the identity - and reference locks are always taken
/// <em>before</em> any Stock row lock, so the two stores contend in one agreed order rather than
/// deadlocking halfway through. Where they still can deadlock (a Retire's Serializable range scan
/// over Stock crossing a write that already holds a reference), SQL Server picks a victim, the fault
/// propagates as the transient thing it is, and the Turn is simply retried.
/// </summary>
internal static class AssignedReferenceLocks
{
    /// <summary>
    /// The isolation a Stock write needs while it holds a reference open. Repeatable reads are exactly
    /// the guarantee being asked for - the reference this write was decided against is still there,
    /// and still active, when it commits - and nothing here needs the range locks serializable would
    /// additionally take. SQLite has one writer and gives this for free.
    /// </summary>
    public const System.Data.IsolationLevel Isolation = System.Data.IsolationLevel.RepeatableRead;

    /// <summary>
    /// Locks and re-verifies every reference this write assigns. Returns false when any of them is
    /// retired or gone, which the caller answers as its own typed stale outcome - never as a write.
    ///
    /// Each reference is read by its own statement, in the globally agreed order, so two writers over
    /// overlapping references contend in the same sequence. Reading is enough: under
    /// <see cref="Isolation"/> the shared lock is held to commit, and a Retire needs an exclusive lock
    /// on the very same row. The reference's own version is deliberately left alone - this write does
    /// not change the reference, and bumping it would spuriously conflict every pending rename.
    /// </summary>
    public static async Task<bool> TryHoldActiveAsync(
        MultiChannelAgentDbContext db,
        InventoryId inventoryId,
        IEnumerable<UnitId> unitIds,
        IEnumerable<LocationId> locationIds,
        CancellationToken cancellationToken)
    {
        foreach (var unitId in Ordered(unitIds.Select(id => id.Value)))
        {
            var active = await db.Units
                .AnyAsync(u => u.Id == unitId && u.InventoryId == inventoryId.Value && u.RetiredAt == null, cancellationToken);

            if (!active)
            {
                return false;
            }
        }

        foreach (var locationId in Ordered(locationIds.Select(id => id.Value)))
        {
            var active = await db.Locations
                .AnyAsync(l => l.Id == locationId && l.InventoryId == inventoryId.Value && l.RetiredAt == null, cancellationToken);

            if (!active)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<Guid> Ordered(IEnumerable<Guid> ids) =>
        ids.Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal);
}
