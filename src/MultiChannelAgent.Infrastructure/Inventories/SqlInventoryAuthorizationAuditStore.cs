using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed <see cref="IInventoryAuthorizationAuditStore"/>: clears a stale Active Inventory
/// selection (when applicable) and appends the AccessDenied audit fact in one
/// <see cref="MultiChannelAgentDbContext.SaveChangesAsync(CancellationToken)"/> call, so a denial is
/// never recorded without also clearing the access it denies, and vice versa.
/// </summary>
public sealed class SqlInventoryAuthorizationAuditStore(MultiChannelAgentDbContext db) : IInventoryAuthorizationAuditStore
{
    public async Task RecordDenialAsync(
        AuditFact fact,
        ParticipantId? clearSelectionParticipantId,
        string? clearSelectionChannelConversationId,
        CancellationToken cancellationToken)
    {
        if (clearSelectionParticipantId is not null && clearSelectionChannelConversationId is not null)
        {
            var selection = await db.ActiveInventorySelections.FirstOrDefaultAsync(
                e => e.ParticipantId == clearSelectionParticipantId.Value.Value
                     && e.ChannelConversationId == clearSelectionChannelConversationId,
                cancellationToken);

            if (selection is not null)
            {
                db.ActiveInventorySelections.Remove(selection);
            }
        }

        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(fact));

        await db.SaveChangesAsync(cancellationToken);
    }
}
