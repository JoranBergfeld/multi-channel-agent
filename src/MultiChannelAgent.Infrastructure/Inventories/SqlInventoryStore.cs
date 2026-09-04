using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed sole authority for Inventory creation, role lookup, and authorized listing. Creation
/// commits the Inventory row, its Owner Membership, and its reserved `each` Unit (with fixed aliases)
/// in one <see cref="MultiChannelAgentDbContext.SaveChangesAsync(CancellationToken)"/> call, which EF
/// Core wraps in a single database transaction - so the whole set either commits or none of it does.
/// Idempotency is additionally enforced by a unique index on (CreatedByParticipantId,
/// ClientRequestId), which <see cref="CreateAsync"/> resolves atomically against concurrent duplicate
/// creation attempts exactly like <c>SqlInboxStore</c> resolves duplicate Turn delivery.
/// </summary>
public sealed class SqlInventoryStore(MultiChannelAgentDbContext db) : IInventoryStore
{
    public async Task<Inventory?> FindByClientRequestIdAsync(ParticipantId createdBy, string clientRequestId, CancellationToken cancellationToken)
    {
        var entity = await db.Inventories
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.CreatedByParticipantId == createdBy.Value && i.ClientRequestId == clientRequestId, cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<InventoryCreationResult> CreateAsync(Inventory inventory, Unit reservedEachUnit, CancellationToken cancellationToken)
    {
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventory.Id.Value,
            Name = inventory.Name,
            NormalizedName = NameNormalization.Normalize(inventory.Name),
            CreatedByParticipantId = inventory.CreatedByParticipantId.Value,
            ClientRequestId = inventory.ClientRequestId,
            CreatedAt = inventory.CreatedAt,
        });

        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = inventory.Id.Value,
            ParticipantId = inventory.CreatedByParticipantId.Value,
            Role = MembershipRole.Owner,
            CreatedAt = inventory.CreatedAt,
        });

        db.Units.Add(new UnitEntity
        {
            Id = reservedEachUnit.Id.Value,
            InventoryId = reservedEachUnit.InventoryId.Value,
            CanonicalName = reservedEachUnit.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(reservedEachUnit.CanonicalName),
            IsReserved = reservedEachUnit.IsReserved,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = reservedEachUnit.CreatedAt,
            RetiredAt = null,
        });

        // The Unit's own term set, canonical first: exactly the five terms every Inventory starts
        // with, each marked reserved so none of them can ever be removed or reassigned.
        foreach (var term in reservedEachUnit.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = reservedEachUnit.InventoryId.Value,
                UnitId = reservedEachUnit.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = true,
                CreatedAt = reservedEachUnit.CreatedAt,
                RetiredAt = null,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent creation attempts for the same (creator, ClientRequestId) can both
            // observe absence via FindByClientRequestIdAsync and both reach this insert; the unique
            // index then lets exactly one commit. Converge on the winner rather than leaking a raw
            // duplicate-key failure - but only when a row genuinely exists now, so unrelated failures
            // still propagate untouched.
            db.ChangeTracker.Clear();

            var winner = await FindByClientRequestIdAsync(inventory.CreatedByParticipantId, inventory.ClientRequestId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return new InventoryCreationResult(winner, WasAlreadyCreated: true);
        }

        return new InventoryCreationResult(inventory, WasAlreadyCreated: false);
    }

    public async Task<MembershipRole?> FindRoleAsync(InventoryId inventoryId, ParticipantId participantId, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.InventoryId == inventoryId.Value && m.ParticipantId == participantId.Value, cancellationToken);

        return membership?.Role;
    }

    public async Task<IReadOnlyList<AuthorizedInventoryRecord>> ListAuthorizedAsync(ParticipantId participantId, CancellationToken cancellationToken)
    {
        var rows = await (
            from membership in db.Memberships.AsNoTracking()
            where membership.ParticipantId == participantId.Value
            join inventory in db.Inventories.AsNoTracking() on membership.InventoryId equals inventory.Id
            join ownerMembership in db.Memberships.AsNoTracking() on inventory.Id equals ownerMembership.InventoryId
            where ownerMembership.Role == MembershipRole.Owner
            join owner in db.Participants.AsNoTracking() on ownerMembership.ParticipantId equals owner.Id
            select new
            {
                InventoryId = inventory.Id,
                inventory.Name,
                OwnerParticipantId = owner.Id,
                OwnerDisplayName = owner.DisplayName,
                membership.Role,
            }).ToListAsync(cancellationToken);

        return rows
            .Select(r => new AuthorizedInventoryRecord(
                new InventoryId(r.InventoryId),
                r.Name,
                new ParticipantId(r.OwnerParticipantId),
                r.OwnerDisplayName,
                r.Role))
            .ToList();
    }

    private static Inventory ToDomain(InventoryEntity entity) => new()
    {
        Id = new InventoryId(entity.Id),
        Name = entity.Name,
        CreatedByParticipantId = new ParticipantId(entity.CreatedByParticipantId),
        ClientRequestId = entity.ClientRequestId,
        CreatedAt = entity.CreatedAt,
    };
}
