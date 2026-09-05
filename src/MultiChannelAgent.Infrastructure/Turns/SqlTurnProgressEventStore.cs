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
        catch
        {
            // SaveChangesAsync does not undo the Added state when a raw provider fault, interceptor
            // failure, or cancellation escapes outside DbUpdateException. This scoped DbContext serves
            // the rest of the coordinator batch, so abandon the courtesy insert before propagating the
            // original fault or a later terminal save could commit it.
            db.ChangeTracker.Clear();
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

        // The bounded set is selected first so one pass can never turn into an unbounded delete:
        // ordering and bounding inside ExecuteDelete is not translatable on every provider this model
        // runs on.
        var expired = await db.TurnProgressEvents
            .AsNoTracking()
            .Where(e => e.ExpiresAtTicks <= nowTicks)
            .OrderBy(e => e.ExpiresAtTicks)
            .ThenBy(e => e.TurnId)
            .ThenBy(e => e.Sequence)
            .Take(maxCount)
            .Select(e => new { e.TurnId, e.Sequence })
            .ToListAsync(cancellationToken);

        // Deleted by the identities that were actually selected, one set-based statement per distinct
        // sequence in the batch. The obvious single statement - "any selected TurnId AND any selected
        // Sequence" - would delete the CROSS PRODUCT of those two sets, which is both wrong and
        // unbounded in exactly the way maxCount exists to prevent. Grouping keeps every statement
        // exact, and the number of statements is bounded by how many distinct progress identities one
        // batch can contain, which is a small constant.
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
