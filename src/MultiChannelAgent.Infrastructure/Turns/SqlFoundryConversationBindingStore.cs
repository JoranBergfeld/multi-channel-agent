using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL Server-backed <see cref="IFoundryConversationBindingStore"/>. Idempotency is enforced by the
/// (ParticipantId, ChannelConversationId) primary key: <see cref="GetOrCreateAsync"/> resolves a
/// concurrent duplicate-insert race the same way <see cref="SqlInboxStore.AcceptAsync"/> does - the
/// loser converges on the winner's binding instead of leaking a raw <see cref="DbUpdateException"/>.
/// </summary>
public sealed class SqlFoundryConversationBindingStore(MultiChannelAgentDbContext db) : IFoundryConversationBindingStore
{
    public async Task<FoundryConversationBinding> GetOrCreateAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(participantId, channelConversationId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var binding = FoundryConversationBinding.CreateFirstGeneration(participantId, channelConversationId, now);

        db.FoundryConversationBindings.Add(new FoundryConversationBindingEntity
        {
            ParticipantId = binding.ParticipantId.Value,
            ChannelConversationId = binding.ChannelConversationId.Value,
            FoundryConversationId = binding.FoundryConversationId.Value,
            Generation = binding.Generation,
            CreatedAt = binding.CreatedAt,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent Turns for the same (Participant, ChannelConversation) can both observe
            // absence and both reach this insert; the primary key then lets exactly one commit. Clear
            // this failed attempt and re-read: if a row is there now, this was that race and we
            // converge on the winner; otherwise this was a real, unrelated failure and must propagate.
            db.ChangeTracker.Clear();

            var winner = await FindAsync(participantId, channelConversationId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        return binding;
    }

    private async Task<FoundryConversationBinding?> FindAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        var entity = await db.FoundryConversationBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.ParticipantId == participantId.Value && e.ChannelConversationId == channelConversationId.Value,
                cancellationToken);

        return entity is null
            ? null
            : new FoundryConversationBinding
            {
                ParticipantId = new ParticipantId(entity.ParticipantId),
                ChannelConversationId = new ChannelConversationId(entity.ChannelConversationId),
                FoundryConversationId = new FoundryConversationId(entity.FoundryConversationId),
                Generation = entity.Generation,
                CreatedAt = entity.CreatedAt,
            };
    }
}
