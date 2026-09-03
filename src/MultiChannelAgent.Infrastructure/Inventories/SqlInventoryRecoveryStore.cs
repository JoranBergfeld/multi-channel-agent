using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed atomic orphan recovery. <see cref="ListOrphanedAsync"/> revalidates every current
/// Owner's active status against the tenant directory (self-healing the persisted
/// <see cref="ParticipantEntity.IsActive"/> flag) and returns a bounded, disambiguation-only summary
/// of the Inventories whose Owner is not active. <see cref="RecoverAsync"/> re-verifies orphaned
/// status against the directory again at commit time - never trusting the persisted flag alone - and
/// is guarded by optimistic concurrency on the Owner Membership row, so a race between two recovery
/// attempts (or a recovery racing an ordinary transfer) can never both commit. The calling Recovery
/// Administrator is never added as a member, and no stock or membership roster is ever touched.
/// </summary>
public sealed class SqlInventoryRecoveryStore(MultiChannelAgentDbContext db, ITenantMemberDirectory directory) : IInventoryRecoveryStore
{
    /// <summary>Caps how many directory resolutions run at once during <see cref="ListOrphanedAsync"/> - concurrent enough to avoid an O(n) sequential round trip per distinct Owner, bounded enough to never hammer Microsoft Graph.</summary>
    private const int MaxConcurrentDirectoryResolutions = 8;

    public async Task<OrphanedInventoriesPage> ListOrphanedAsync(int maxResults, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ownerRows = await (
            from membership in db.Memberships.AsNoTracking()
            where membership.Role == MembershipRole.Owner
            join inventory in db.Inventories.AsNoTracking() on membership.InventoryId equals inventory.Id
            join owner in db.Participants.AsNoTracking() on membership.ParticipantId equals owner.Id
            select new { InventoryId = inventory.Id, inventory.Name, OwnerId = owner.Id, OwnerDisplayName = owner.DisplayName }
        ).ToListAsync(cancellationToken);

        var distinctOwnerIds = ownerRows.Select(r => r.OwnerId).Distinct().ToList();
        var isActiveByOwnerId = await ResolveActiveStatusBoundedAsync(distinctOwnerIds, cancellationToken);

        var trackedOwners = await db.Participants.Where(p => distinctOwnerIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var changed = false;
        foreach (var owner in trackedOwners)
        {
            var isActive = isActiveByOwnerId[owner.Id];
            if (owner.IsActive != isActive)
            {
                owner.IsActive = isActive;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var orphaned = ownerRows
            .Where(r => InventoryGovernancePolicy.IsOrphaned(isActiveByOwnerId[r.OwnerId]))
            .Select(r => new OrphanedInventorySummary(r.InventoryId.ToString(), new InventoryId(r.InventoryId).ShortId, r.Name, r.OwnerDisplayName))
            .ToList();

        return new OrphanedInventoriesPage(orphaned.Count, orphaned.Take(maxResults).ToList());
    }

    /// <summary>
    /// Resolves every distinct Owner's active status concurrently, bounded by
    /// <see cref="MaxConcurrentDirectoryResolutions"/> - avoiding an O(n) sequential round trip per
    /// Owner without unbounded fan-out against Microsoft Graph. A directory failure for any single
    /// Owner (a typed <see cref="TenantDirectoryUnavailableException"/> - never silently treated as
    /// "not found") propagates out of the awaited <see cref="Task.WhenAll(Task[])"/> exactly as a
    /// sequential loop would: the whole listing call fails outright, and no Owner's persisted
    /// <see cref="ParticipantEntity.IsActive"/> flag is touched for this call, since that only happens
    /// afterward and only if this method returns successfully.
    /// </summary>
    private async Task<Dictionary<Guid, bool>> ResolveActiveStatusBoundedAsync(IReadOnlyList<Guid> ownerIds, CancellationToken cancellationToken)
    {
        using var concurrencyLimiter = new SemaphoreSlim(MaxConcurrentDirectoryResolutions);
        var isActiveByOwnerId = new ConcurrentDictionary<Guid, bool>();

        var resolutions = ownerIds.Select(async ownerId =>
        {
            await concurrencyLimiter.WaitAsync(cancellationToken);
            try
            {
                var identifier = TenantMemberIdentifier.Parse(ownerId.ToString())!;
                var resolution = await directory.ResolveAsync(identifier, cancellationToken);
                isActiveByOwnerId[ownerId] = resolution is not null;
            }
            finally
            {
                concurrencyLimiter.Release();
            }
        }).ToList();

        await Task.WhenAll(resolutions);

        return new Dictionary<Guid, bool>(isActiveByOwnerId);
    }

    public async Task<RecoveryResult> RecoverAsync(
        InventoryId inventoryId, TenantMemberIdentifier targetIdentifier, string actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ownerRow = await db.Memberships.FirstOrDefaultAsync(
            m => m.InventoryId == inventoryId.Value && m.Role == MembershipRole.Owner, cancellationToken);

        if (ownerRow is null)
        {
            // Non-disclosing: a nonexistent Inventory id must be indistinguishable from a healthy one.
            return new RecoveryResult(RecoveryOutcome.NotEligible, null);
        }

        var ownerParticipant = await db.Participants.FirstOrDefaultAsync(p => p.Id == ownerRow.ParticipantId, cancellationToken);
        var ownerIdentifier = TenantMemberIdentifier.Parse(ownerRow.ParticipantId.ToString())!;
        var ownerResolution = await directory.ResolveAsync(ownerIdentifier, cancellationToken);
        var ownerIsActive = ownerResolution is not null;

        if (ownerParticipant is not null && ownerParticipant.IsActive != ownerIsActive)
        {
            ownerParticipant.IsActive = ownerIsActive;
        }

        if (!InventoryGovernancePolicy.IsOrphaned(ownerIsActive))
        {
            // Self-heals the persisted flag (the Owner is active again) even though recovery does not
            // proceed - never leave a stale "orphaned" flag lying around once directly rechecked.
            await db.SaveChangesAsync(cancellationToken);
            return new RecoveryResult(RecoveryOutcome.NotEligible, null);
        }

        var resolvedTarget = await directory.ResolveAsync(targetIdentifier, cancellationToken);
        if (resolvedTarget is null)
        {
            return new RecoveryResult(RecoveryOutcome.TargetNotResolved, null);
        }

        var targetParticipant = await db.Participants.FirstOrDefaultAsync(p => p.Id == resolvedTarget.ParticipantId.Value, cancellationToken);
        if (targetParticipant is null)
        {
            db.Participants.Add(new ParticipantEntity
            {
                Id = resolvedTarget.ParticipantId.Value,
                DisplayName = resolvedTarget.DisplayName,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            targetParticipant.DisplayName = resolvedTarget.DisplayName;
            targetParticipant.IsActive = true;
            targetParticipant.UpdatedAt = now;
        }

        var targetMembership = await db.Memberships.FirstOrDefaultAsync(
            m => m.InventoryId == inventoryId.Value && m.ParticipantId == resolvedTarget.ParticipantId.Value, cancellationToken);

        ownerRow.Role = MembershipRole.Editor;
        ownerRow.ConcurrencyStamp = Guid.NewGuid();

        if (targetMembership is null)
        {
            db.Memberships.Add(new MembershipEntity
            {
                InventoryId = inventoryId.Value,
                ParticipantId = resolvedTarget.ParticipantId.Value,
                Role = MembershipRole.Owner,
                CreatedAt = now,
            });
        }
        else
        {
            targetMembership.Role = MembershipRole.Owner;
            targetMembership.ConcurrencyStamp = Guid.NewGuid();
        }

        var fact = AuditFact.Create(
            AuditEventType.OrphanOwnershipRecovered,
            AuditActorKind.RecoveryAdministrator,
            actorId,
            inventoryId,
            resolvedTarget.ParticipantId,
            "Recovered",
            now);
        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(fact));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another recovery attempt (or an ordinary transfer, in the vanishingly unlikely case the
            // Owner became resolvable and transferred away in the same instant) already committed for
            // this Owner row between our read above and this save.
            db.ChangeTracker.Clear();
            return new RecoveryResult(RecoveryOutcome.ConcurrentModification, null);
        }
        catch (DbUpdateException) when (targetMembership is null)
        {
            // A concurrent grant, transfer, or recovery may create this target Membership after the
            // read above. Translate only that exact insert race; participant, audit, and other
            // database failures remain visible to the caller.
            if (await MembershipInsertConflictDetector.ExistsAfterFailedInsertAsync(
                    db, inventoryId, resolvedTarget.ParticipantId, cancellationToken))
            {
                return new RecoveryResult(RecoveryOutcome.ConcurrentModification, null);
            }

            throw;
        }

        return new RecoveryResult(RecoveryOutcome.Recovered, resolvedTarget.DisplayName);
    }
}
