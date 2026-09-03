using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryMembershipStore"/> for Application-layer unit tests,
/// sharing the backing <see cref="InMemoryInventoryStore"/> so authorization checks (via
/// <see cref="InventoryAuthorizationService"/>) and membership mutations observe the same state.
/// </summary>
public sealed class InMemoryInventoryMembershipStore(InMemoryInventoryStore inventoryStore) : IInventoryMembershipStore
{
    public IReadOnlyList<AuditFact> RecordedFacts => _recordedFacts;

    private readonly List<AuditFact> _recordedFacts = [];

    public Task<MembershipGrantResult> GrantOrChangeRoleAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        string targetDisplayName,
        MembershipRole role,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targetCurrentRole = inventoryStore.Memberships
            .Where(m => m.InventoryId == inventoryId && m.ParticipantId == targetParticipantId)
            .Select(m => (MembershipRole?)m.Role)
            .FirstOrDefault();

        if (targetCurrentRole == MembershipRole.Owner)
        {
            return Task.FromResult(new MembershipGrantResult(MembershipGrantOutcome.TargetIsOwner));
        }

        var outcome = targetCurrentRole is null ? MembershipGrantOutcome.Granted : MembershipGrantOutcome.RoleChanged;
        inventoryStore.SetRole(inventoryId, targetParticipantId, role, now);

        _recordedFacts.Add(AuditFact.Create(
            outcome == MembershipGrantOutcome.Granted ? AuditEventType.MembershipGranted : AuditEventType.RoleChanged,
            AuditActorKind.Participant,
            requesterId.ToString(),
            inventoryId,
            targetParticipantId,
            $"{outcome}:{role}",
            now));

        return Task.FromResult(new MembershipGrantResult(outcome));
    }

    public Task<MembershipRemovalResult> RemoveAsync(
        InventoryId inventoryId, ParticipantId requesterId, ParticipantId targetParticipantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var targetCurrentRole = inventoryStore.Memberships
            .Where(m => m.InventoryId == inventoryId && m.ParticipantId == targetParticipantId)
            .Select(m => (MembershipRole?)m.Role)
            .FirstOrDefault();

        if (targetCurrentRole is null)
        {
            return Task.FromResult(new MembershipRemovalResult(MembershipRemovalOutcome.NotAMember));
        }

        if (targetCurrentRole == MembershipRole.Owner)
        {
            return Task.FromResult(new MembershipRemovalResult(MembershipRemovalOutcome.TargetIsOwner));
        }

        inventoryStore.RevokeMembership(inventoryId, targetParticipantId);

        _recordedFacts.Add(AuditFact.Create(
            AuditEventType.MembershipRemoved, AuditActorKind.Participant, requesterId.ToString(), inventoryId, targetParticipantId, "Removed", now));

        return Task.FromResult(new MembershipRemovalResult(MembershipRemovalOutcome.Removed));
    }

    public Task<IReadOnlyList<MemberView>> ListMembersAsync(InventoryId inventoryId, CancellationToken cancellationToken)
    {
        var members = inventoryStore.Memberships
            .Where(m => m.InventoryId == inventoryId)
            .Select(m => new MemberView(m.ParticipantId.ToString(), "Test Participant", m.Role.ToString()))
            .ToList();

        return Task.FromResult<IReadOnlyList<MemberView>>(members);
    }
}
