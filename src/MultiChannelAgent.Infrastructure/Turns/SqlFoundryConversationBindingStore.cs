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
///
/// It reads the same row two ways, and the difference is the point.
/// <see cref="GetOrCreateAsync"/> is the cheap current answer every ordinary caller wants;
/// <see cref="ReadCurrentForSupersessionAsync"/> is a locking read for the one caller that has to be
/// ordered against a reset committing right now, and it never writes.
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

    /// <summary>
    /// The supersession read: a locking read of this pair's binding, in its own short transaction, so
    /// it and a rotation of the same row are strictly ordered rather than passing through each other.
    /// See <see cref="FoundryConversationBindingSupersessionRead"/> for why an ordinary read cannot
    /// answer this question on a database with <c>READ_COMMITTED_SNAPSHOT</c> on.
    ///
    /// The transaction is opened here only when the caller is not already in one - a caller inside a
    /// transaction already holds its locks to its own commit, which is strictly stronger. It is
    /// abandoned rather than left dangling on any fault or cancellation, because this
    /// <see cref="MultiChannelAgentDbContext"/> is scoped to a whole batch of Turns and a rolled-back
    /// phantom would be resolved against by the next unrelated read in that same scope.
    /// </summary>
    public async Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await LockingReadAsync(participantId, channelConversationId, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var binding = await LockingReadAsync(participantId, channelConversationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return binding;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    private async Task<FoundryConversationBinding?> LockingReadAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        // No tracking: this is a read that decides something, never the start of a write, and a
        // tracked copy would linger in a scope shared by a whole batch of Turns.
        var rows = await db.FoundryConversationBindings
            .FromSql(FoundryConversationBindingSupersessionRead.Statement(
                db.Database, participantId.Value, channelConversationId.Value))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : Project(rows[0]);
    }

    private async Task<FoundryConversationBinding?> FindAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        var entity = await db.FoundryConversationBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.ParticipantId == participantId.Value && e.ChannelConversationId == channelConversationId.Value,
                cancellationToken);

        return entity is null ? null : Project(entity);
    }

    private static FoundryConversationBinding Project(FoundryConversationBindingEntity entity) => new()
    {
        ParticipantId = new ParticipantId(entity.ParticipantId),
        ChannelConversationId = new ChannelConversationId(entity.ChannelConversationId),
        FoundryConversationId = new FoundryConversationId(entity.FoundryConversationId),
        Generation = entity.Generation,
        CreatedAt = entity.CreatedAt,
    };
}
