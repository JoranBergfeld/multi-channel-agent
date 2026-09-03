using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed durable store for one Participant's Active Inventory selection per ChannelConversation.
/// </summary>
public sealed class SqlActiveInventorySelectionStore(MultiChannelAgentDbContext db) : IActiveInventorySelectionStore
{
    public async Task<ActiveInventorySelection?> FindAsync(ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken)
    {
        var entity = await db.ActiveInventorySelections
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.ParticipantId == participantId.Value && e.ChannelConversationId == channelConversationId,
                cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task UpsertAsync(ActiveInventorySelection selection, CancellationToken cancellationToken)
    {
        var existing = await db.ActiveInventorySelections.FirstOrDefaultAsync(
            e => e.ParticipantId == selection.ParticipantId.Value && e.ChannelConversationId == selection.ChannelConversationId,
            cancellationToken);

        if (existing is null)
        {
            db.ActiveInventorySelections.Add(new ActiveInventorySelectionEntity
            {
                ParticipantId = selection.ParticipantId.Value,
                ChannelConversationId = selection.ChannelConversationId,
                InventoryId = selection.InventoryId.Value,
                LastActivityAt = selection.LastActivityAt,
            });
        }
        else
        {
            existing.InventoryId = selection.InventoryId.Value;
            existing.LastActivityAt = selection.LastActivityAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken)
    {
        var existing = await db.ActiveInventorySelections.FirstOrDefaultAsync(
            e => e.ParticipantId == participantId.Value && e.ChannelConversationId == channelConversationId,
            cancellationToken);

        if (existing is not null)
        {
            db.ActiveInventorySelections.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static ActiveInventorySelection ToDomain(ActiveInventorySelectionEntity entity) => new(
        new ParticipantId(entity.ParticipantId),
        entity.ChannelConversationId,
        new InventoryId(entity.InventoryId),
        entity.LastActivityAt);
}
