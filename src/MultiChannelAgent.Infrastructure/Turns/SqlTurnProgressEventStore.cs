using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

public sealed class SqlTurnProgressEventStore(MultiChannelAgentDbContext db) : ITurnProgressEventStore
{
    public async Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken)
    {
        var entity = new TurnProgressEventEntity
        {
            TurnId = progressEvent.TurnId.Value,
            Sequence = progressEvent.Sequence,
            Kind = progressEvent.Kind.ToString(),
            OccurredAt = progressEvent.OccurredAt,
            ExpiresAtTicks = progressEvent.ExpiresAt.UtcTicks,
        };
        db.TurnProgressEvents.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(entity).State = EntityState.Detached;
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            var exists = await db.TurnProgressEvents
                .AsNoTracking()
                .AnyAsync(
                    e => e.TurnId == progressEvent.TurnId.Value && e.Sequence == progressEvent.Sequence,
                    cancellationToken);

            if (exists)
            {
                return false;
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(
        TurnId turnId,
        CancellationToken cancellationToken)
    {
        var entities = await db.TurnProgressEvents
            .AsNoTracking()
            .Where(e => e.TurnId == turnId.Value)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        return entities.Select(e => new TurnProgressEvent
        {
            TurnId = new TurnId(e.TurnId),
            Sequence = e.Sequence,
            Kind = Enum.Parse<TurnEventKind>(e.Kind),
            OccurredAt = e.OccurredAt,
            ExpiresAt = new DateTimeOffset(e.ExpiresAtTicks, TimeSpan.Zero),
        }).ToList();
    }

    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var nowTicks = now.UtcTicks;
        var expired = await db.TurnProgressEvents
            .AsNoTracking()
            .Where(e => e.ExpiresAtTicks <= nowTicks)
            .OrderBy(e => e.ExpiresAtTicks)
            .ThenBy(e => e.TurnId)
            .ThenBy(e => e.Sequence)
            .Take(maxCount)
            .Select(e => new { e.TurnId, e.Sequence })
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var sequenceGroup in expired.GroupBy(e => e.Sequence))
        {
            var turnIds = sequenceGroup.Select(e => e.TurnId).ToList();
            deleted += await db.TurnProgressEvents
                .Where(e =>
                    e.ExpiresAtTicks <= nowTicks
                    && e.Sequence == sequenceGroup.Key
                    && turnIds.Contains(e.TurnId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }
}
