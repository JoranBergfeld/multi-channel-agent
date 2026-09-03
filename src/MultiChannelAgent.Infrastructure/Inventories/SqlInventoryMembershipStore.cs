using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed atomic Owner-driven membership administration: every grant, role change, or removal
/// commits together with its semantic audit fact - and, for removal, with clearing every Active
/// Inventory selection the removed Participant held for this Inventory - in one
/// <see cref="MultiChannelAgentDbContext.SaveChangesAsync(CancellationToken)"/> transaction.
/// </summary>
public sealed class SqlInventoryMembershipStore(MultiChannelAgentDbContext db) : IInventoryMembershipStore
{
    public async Task<MembershipGrantResult> GrantOrChangeRoleAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        string targetDisplayName,
        MembershipRole role,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.Memberships.FirstOrDefaultAsync(
            m => m.InventoryId == inventoryId.Value && m.ParticipantId == targetParticipantId.Value, cancellationToken);

        // Defense in depth: the Application layer already rejects granting the requested role to the
        // current Owner, but re-checking here against the freshest read guards the same TOCTOU window
        // ownership transfer guards with concurrency - the target could have just become Owner via a
        // concurrent transfer.
        if (existing?.Role == MembershipRole.Owner)
        {
            return new MembershipGrantResult(MembershipGrantOutcome.TargetIsOwner);
        }

        MembershipGrantOutcome outcome;
        if (existing is null)
        {
            db.Memberships.Add(new MembershipEntity
            {
                InventoryId = inventoryId.Value,
                ParticipantId = targetParticipantId.Value,
                Role = role,
                CreatedAt = now,
            });
            outcome = MembershipGrantOutcome.Granted;
        }
        else
        {
            existing.Role = role;
            existing.ConcurrencyStamp = Guid.NewGuid();
            outcome = MembershipGrantOutcome.RoleChanged;
        }

        var fact = AuditFact.Create(
            outcome == MembershipGrantOutcome.Granted ? AuditEventType.MembershipGranted : AuditEventType.RoleChanged,
            AuditActorKind.Participant,
            requesterId.ToString(),
            inventoryId,
            targetParticipantId,
            $"{outcome}:{role}",
            now);
        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(fact));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent ownership transfer or recovery (or another grant/change request) already
            // committed against this same Membership row between our read above and this save.
            db.ChangeTracker.Clear();
            return new MembershipGrantResult(MembershipGrantOutcome.ConcurrentModification);
        }

        return new MembershipGrantResult(outcome);
    }

    public async Task<MembershipRemovalResult> RemoveAsync(
        InventoryId inventoryId, ParticipantId requesterId, ParticipantId targetParticipantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.Memberships.FirstOrDefaultAsync(
            m => m.InventoryId == inventoryId.Value && m.ParticipantId == targetParticipantId.Value, cancellationToken);

        if (existing is null)
        {
            return new MembershipRemovalResult(MembershipRemovalOutcome.NotAMember);
        }

        if (existing.Role == MembershipRole.Owner)
        {
            return new MembershipRemovalResult(MembershipRemovalOutcome.TargetIsOwner);
        }

        db.Memberships.Remove(existing);

        // Access loss must clear every Active Inventory selection this Participant held for this
        // Inventory, across every ChannelConversation - not just the one the removing Owner happens
        // to be using.
        var staleSelections = await db.ActiveInventorySelections
            .Where(s => s.ParticipantId == targetParticipantId.Value && s.InventoryId == inventoryId.Value)
            .ToListAsync(cancellationToken);
        db.ActiveInventorySelections.RemoveRange(staleSelections);

        var fact = AuditFact.Create(
            AuditEventType.MembershipRemoved, AuditActorKind.Participant, requesterId.ToString(), inventoryId, targetParticipantId, "Removed", now);
        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(fact));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent ownership transfer or recovery already committed against this same
            // Membership row (or removed it outright) between our read above and this save - the
            // audit fact and stale-selection removal staged above are discarded together with it,
            // since nothing here has been persisted yet.
            db.ChangeTracker.Clear();
            return new MembershipRemovalResult(MembershipRemovalOutcome.ConcurrentModification);
        }

        return new MembershipRemovalResult(MembershipRemovalOutcome.Removed);
    }

    public async Task<IReadOnlyList<MemberView>> ListMembersAsync(InventoryId inventoryId, CancellationToken cancellationToken)
    {
        var rows = await (
            from membership in db.Memberships.AsNoTracking()
            where membership.InventoryId == inventoryId.Value
            join participant in db.Participants.AsNoTracking() on membership.ParticipantId equals participant.Id
            select new { participant.Id, participant.DisplayName, membership.Role }
        ).ToListAsync(cancellationToken);

        return rows.Select(r => new MemberView(r.Id.ToString(), r.DisplayName, r.Role.ToString())).ToList();
    }
}
