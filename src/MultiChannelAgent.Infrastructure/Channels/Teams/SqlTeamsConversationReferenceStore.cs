using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Channels.Teams;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Channels.Teams;

/// <summary>SQL-backed durable store for serialized Teams conversation references, upserted by chat id.</summary>
public sealed class SqlTeamsConversationReferenceStore(MultiChannelAgentDbContext db, TimeProvider timeProvider)
    : ITeamsConversationReferenceStore
{
    public async Task SaveAsync(string channelConversationId, string serializedReference, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await db.TeamsConversationReferences
            .FirstOrDefaultAsync(x => x.ChannelConversationId == channelConversationId, cancellationToken);

        if (existing is null)
        {
            db.TeamsConversationReferences.Add(new TeamsConversationReferenceEntity
            {
                ChannelConversationId = channelConversationId,
                SerializedReference = serializedReference,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.SerializedReference = serializedReference;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> FindAsync(string channelConversationId, CancellationToken cancellationToken) =>
        (await db.TeamsConversationReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChannelConversationId == channelConversationId, cancellationToken))?.SerializedReference;
}
