using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum RecoveryRequestOutcome
{
    Recovered,
    NotEligible,
    TargetNotResolved,
    ConcurrentModification,
}

public sealed record RecoveryRequestResult(RecoveryRequestOutcome Outcome, string? NewOwnerDisplayName);

/// <summary>
/// The Recovery Administrator-facing seam: identify orphaned Inventories (bounded, disambiguation-only
/// facts) and transfer one's ownership to a resolvable active Participant. Never grants access to
/// stock or a membership roster, and never makes the calling administrator a member.
/// </summary>
public sealed class InventoryRecoveryService(IInventoryRecoveryStore store)
{
    private const int MaxOrphanedResults = 100;

    public Task<OrphanedInventoriesPage> ListOrphanedAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        store.ListOrphanedAsync(MaxOrphanedResults, now, cancellationToken);

    public async Task<RecoveryRequestResult> RecoverAsync(
        string actorId, InventoryId inventoryId, string? targetIdentifierRaw, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var identifier = TenantMemberIdentifier.Parse(targetIdentifierRaw);
        if (identifier is null)
        {
            return new RecoveryRequestResult(RecoveryRequestOutcome.TargetNotResolved, null);
        }

        var result = await store.RecoverAsync(inventoryId, identifier, actorId, now, cancellationToken);

        return new RecoveryRequestResult(
            result.Outcome switch
            {
                RecoveryOutcome.Recovered => RecoveryRequestOutcome.Recovered,
                RecoveryOutcome.NotEligible => RecoveryRequestOutcome.NotEligible,
                RecoveryOutcome.TargetNotResolved => RecoveryRequestOutcome.TargetNotResolved,
                RecoveryOutcome.ConcurrentModification => RecoveryRequestOutcome.ConcurrentModification,
                _ => throw new InvalidOperationException($"Unhandled {nameof(RecoveryOutcome)}: {result.Outcome}"),
            },
            result.NewOwnerDisplayName);
    }
}
