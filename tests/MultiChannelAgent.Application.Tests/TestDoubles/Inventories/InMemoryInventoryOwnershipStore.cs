using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryOwnershipStore"/> for Application-layer unit tests.
/// Optimistic-concurrency behavior under real contention is proven separately against a real
/// relational engine (see the SQL-backed concurrency scenario); this double only needs to prove the
/// atomic promote/demote outcome and the requester-still-owner defense-in-depth check.
/// </summary>
public sealed class InMemoryInventoryOwnershipStore(InMemoryInventoryStore inventoryStore) : IInventoryOwnershipStore
{
    public IReadOnlyList<AuditFact> RecordedFacts => _recordedFacts;

    private readonly List<AuditFact> _recordedFacts = [];

    public Task<TransferResult> TransferAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        string targetDisplayName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var currentOwner = inventoryStore.Memberships.FirstOrDefault(m => m.InventoryId == inventoryId && m.Role == MembershipRole.Owner);
        if (currentOwner is null || currentOwner.ParticipantId != requesterId)
        {
            return Task.FromResult(new TransferResult(TransferOutcome.RequesterNotOwner));
        }

        inventoryStore.SetRole(inventoryId, requesterId, MembershipRole.Editor, now);
        inventoryStore.SetRole(inventoryId, targetParticipantId, MembershipRole.Owner, now);

        _recordedFacts.Add(AuditFact.Create(
            AuditEventType.OwnershipTransferred, AuditActorKind.Participant, requesterId.ToString(), inventoryId, targetParticipantId, "Transferred", now));

        return Task.FromResult(new TransferResult(TransferOutcome.Transferred));
    }
}
