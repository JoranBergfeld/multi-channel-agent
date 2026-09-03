using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed atomic ownership transfer: promotes the target to Owner and demotes the previous Owner
/// to Editor in one transaction, guarded by <see cref="MembershipEntity.ConcurrencyStamp"/> optimistic
/// concurrency on the Owner Membership row read at the start of this method - so two concurrent
/// transfer attempts for the same Inventory can never both commit, and an Inventory can never
/// intentionally become ownerless.
/// </summary>
public sealed class SqlInventoryOwnershipStore(MultiChannelAgentDbContext db) : IInventoryOwnershipStore
{
    public async Task<TransferResult> TransferAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        string targetDisplayName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ownerRow = await db.Memberships.FirstOrDefaultAsync(
            m => m.InventoryId == inventoryId.Value && m.Role == MembershipRole.Owner, cancellationToken);

        if (ownerRow is null || ownerRow.ParticipantId != requesterId.Value)
        {
            return new TransferResult(TransferOutcome.RequesterNotOwner);
        }

        var targetRow = await db.Memberships.FirstOrDefaultAsync(
            m => m.InventoryId == inventoryId.Value && m.ParticipantId == targetParticipantId.Value, cancellationToken);

        ownerRow.Role = MembershipRole.Editor;
        ownerRow.ConcurrencyStamp = Guid.NewGuid();

        if (targetRow is null)
        {
            db.Memberships.Add(new MembershipEntity
            {
                InventoryId = inventoryId.Value,
                ParticipantId = targetParticipantId.Value,
                Role = MembershipRole.Owner,
                CreatedAt = now,
            });
        }
        else
        {
            targetRow.Role = MembershipRole.Owner;
            targetRow.ConcurrencyStamp = Guid.NewGuid();
        }

        var fact = AuditFact.Create(
            AuditEventType.OwnershipTransferred, AuditActorKind.Participant, requesterId.ToString(), inventoryId, targetParticipantId, "Transferred", now);
        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(fact));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another transfer (or recovery) committed for this Owner row between our read above and
            // this save - never both silently succeed; the loser reports the conflict instead.
            db.ChangeTracker.Clear();
            return new TransferResult(TransferOutcome.ConcurrentModification);
        }
        catch (DbUpdateException) when (targetRow is null)
        {
            // A grant, transfer, or recovery can concurrently create the target Membership after
            // this request observed it missing. Confirm that exact row now exists before translating
            // the insert failure; every unrelated database error still propagates.
            if (await MembershipInsertConflictDetector.ExistsAfterFailedInsertAsync(
                    db, inventoryId, targetParticipantId, cancellationToken))
            {
                return new TransferResult(TransferOutcome.ConcurrentModification);
            }

            throw;
        }

        return new TransferResult(TransferOutcome.Transferred);
    }
}
