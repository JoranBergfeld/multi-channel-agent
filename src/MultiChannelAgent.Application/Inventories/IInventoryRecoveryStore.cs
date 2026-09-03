using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// A bounded, disambiguation-only summary of one orphaned Inventory - never stock, never the full
/// membership roster, just enough for a Recovery Administrator to pick the right one.
/// </summary>
public sealed record OrphanedInventorySummary(string InventoryId, string ShortId, string Name, string LastKnownOwnerDisplayName);

public sealed record OrphanedInventoriesPage(int TotalCount, IReadOnlyList<OrphanedInventorySummary> Items);

public enum RecoveryOutcome
{
    Recovered,

    /// <summary>
    /// Non-disclosing: covers both a healthy Inventory (its Owner is still active) and a nonexistent
    /// Inventory id - the two must be indistinguishable to the caller.
    /// </summary>
    NotEligible,

    /// <summary>The target identifier did not resolve to an exact, active, non-guest tenant member.</summary>
    TargetNotResolved,

    /// <summary>Another recovery or transfer already committed for this Inventory between this request's read and its write.</summary>
    ConcurrentModification,
}

public sealed record RecoveryResult(RecoveryOutcome Outcome, string? NewOwnerDisplayName);

/// <summary>
/// The atomic seam for orphan recovery: identifies Inventories whose sole Owner Participant is no
/// longer active/resolvable per the tenant directory, and lets a Recovery Administrator transfer
/// ownership to a resolvable active Participant - re-verifying orphaned status against the directory
/// at commit time (never trusting a stale cache) and guarded by optimistic concurrency so a race
/// between two recovery attempts can never both succeed. The Recovery Administrator is never added as
/// a member, and neither this store nor its caller ever exposes stock or a membership roster.
/// </summary>
public interface IInventoryRecoveryStore
{
    Task<OrphanedInventoriesPage> ListOrphanedAsync(int maxResults, DateTimeOffset now, CancellationToken cancellationToken);

    Task<RecoveryResult> RecoverAsync(
        InventoryId inventoryId,
        TenantMemberIdentifier targetIdentifier,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
