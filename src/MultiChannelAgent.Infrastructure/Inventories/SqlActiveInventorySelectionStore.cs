using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed durable store for one Participant's Active Inventory selection per ChannelConversation.
/// <see cref="UpsertAsync"/> resolves the concurrent-first-selection race on the primary key -
/// (ParticipantId, ChannelConversationId) - atomically against two simultaneous requests, exactly like
/// <c>SqlInboxStore</c> resolves duplicate Turn delivery: the loser converges on the winner's row
/// instead of leaking a raw <see cref="DbUpdateException"/> to callers.
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

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                // Two concurrent selections for the SAME (ParticipantId, ChannelConversationId) -
                // for example a bootstrap auto-selection racing an explicit multi-tab selection, or
                // two browser tabs selecting concurrently - can both observe absence via the read
                // above and both reach this insert; the primary key then lets exactly one of them
                // commit. Rather than parsing a provider-specific error code to confirm that
                // assumption, clear this failed attempt from the tracker and re-read by the same
                // key: if a row is there now, some other write genuinely committed it first, so this
                // IS that selection race. Selection never grants Membership by itself - the caller
                // already re-checked authorization before reaching UpsertAsync - so it is safe to let
                // last-writer-wins: this attempt updates the winner's row to its own values, so the
                // final row always reflects whichever authorized selection happened most recently,
                // exactly like the non-racing update path below. If no such row exists, this was a
                // real, unrelated failure (for example an invalid InventoryId violating the foreign
                // key) and must propagate untouched rather than be disguised as a race.
                db.ChangeTracker.Clear();

                existing = await db.ActiveInventorySelections.FirstOrDefaultAsync(
                    e => e.ParticipantId == selection.ParticipantId.Value && e.ChannelConversationId == selection.ChannelConversationId,
                    cancellationToken);

                if (existing is null)
                {
                    throw;
                }
            }
        }

        existing.InventoryId = selection.InventoryId.Value;
        existing.LastActivityAt = selection.LastActivityAt;
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
