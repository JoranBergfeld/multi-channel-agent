using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryRecoveryStore"/> for Application-layer unit tests. Orphan
/// status is derived from <see cref="InMemoryParticipantStore"/>'s persisted
/// <see cref="Participant.IsActive"/> flag, revalidated afresh against the directory double at both
/// listing and recovery time - mirroring the real store's "never trust a stale cache" rule - while
/// real optimistic-concurrency-under-contention behavior is proven at the SQL/Infrastructure layer.
/// </summary>
public sealed class InMemoryInventoryRecoveryStore(
    InMemoryInventoryStore inventoryStore,
    InMemoryParticipantStore participantStore,
    ITenantMemberDirectory directory)
    : IInventoryRecoveryStore
{
    public IReadOnlyList<AuditFact> RecordedFacts => _recordedFacts;

    private readonly List<AuditFact> _recordedFacts = [];

    public async Task<OrphanedInventoriesPage> ListOrphanedAsync(int maxResults, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var summaries = new List<OrphanedInventorySummary>();

        foreach (var owner in inventoryStore.Memberships.Where(m => m.Role == MembershipRole.Owner))
        {
            var isActive = await RevalidateAsync(owner.ParticipantId, cancellationToken);
            if (InventoryGovernancePolicy.IsOrphaned(isActive))
            {
                var inventory = inventoryStore.Inventories.Single(i => i.Id == owner.InventoryId);
                var ownerDisplayName = participantStore.Participants.GetValueOrDefault(owner.ParticipantId)?.DisplayName ?? "Unknown";
                summaries.Add(new OrphanedInventorySummary(inventory.Id.ToString(), inventory.Id.ShortId, inventory.Name, ownerDisplayName));
            }
        }

        var bounded = summaries.Take(maxResults).ToList();
        return new OrphanedInventoriesPage(summaries.Count, bounded);
    }

    public async Task<RecoveryResult> RecoverAsync(
        InventoryId inventoryId, TenantMemberIdentifier targetIdentifier, string actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var currentOwner = inventoryStore.Memberships.FirstOrDefault(m => m.InventoryId == inventoryId && m.Role == MembershipRole.Owner);
        if (currentOwner is null)
        {
            return new RecoveryResult(RecoveryOutcome.NotEligible, null);
        }

        var ownerIsActive = await RevalidateAsync(currentOwner.ParticipantId, cancellationToken);
        if (!InventoryGovernancePolicy.IsOrphaned(ownerIsActive))
        {
            return new RecoveryResult(RecoveryOutcome.NotEligible, null);
        }

        var resolvedTarget = await directory.ResolveAsync(targetIdentifier, cancellationToken);
        if (resolvedTarget is null)
        {
            return new RecoveryResult(RecoveryOutcome.TargetNotResolved, null);
        }

        inventoryStore.SetRole(inventoryId, currentOwner.ParticipantId, MembershipRole.Editor, now);
        inventoryStore.SetRole(inventoryId, resolvedTarget.ParticipantId, MembershipRole.Owner, now);

        _recordedFacts.Add(AuditFact.Create(
            AuditEventType.OrphanOwnershipRecovered,
            AuditActorKind.RecoveryAdministrator,
            actorId,
            inventoryId,
            resolvedTarget.ParticipantId,
            "Recovered",
            now));

        return new RecoveryResult(RecoveryOutcome.Recovered, resolvedTarget.DisplayName);
    }

    private async Task<bool> RevalidateAsync(ParticipantId ownerId, CancellationToken cancellationToken)
    {
        var identifier = TenantMemberIdentifier.Parse(ownerId.ToString())!;
        var resolution = await directory.ResolveAsync(identifier, cancellationToken);

        var isActive = resolution is not null;
        if (participantStore.Participants.TryGetValue(ownerId, out var participant) && participant.IsActive != isActive)
        {
            participantStore.SetActive(ownerId, isActive);
        }

        return isActive;
    }
}
