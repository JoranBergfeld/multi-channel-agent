using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

internal static class MembershipInsertConflictDetector
{
    public static async Task<bool> ExistsAfterFailedInsertAsync(
        MultiChannelAgentDbContext db,
        InventoryId inventoryId,
        ParticipantId participantId,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        return await db.Memberships.AsNoTracking().AnyAsync(
            membership =>
                membership.InventoryId == inventoryId.Value
                && membership.ParticipantId == participantId.Value,
            cancellationToken);
    }
}
