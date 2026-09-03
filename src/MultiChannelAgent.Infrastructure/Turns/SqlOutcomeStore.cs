using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>SQL Server-backed read/write access to the terminal <see cref="Outcome"/> recorded for a Turn.</summary>
public sealed class SqlOutcomeStore(MultiChannelAgentDbContext db) : IOutcomeStore
{
    public async Task<Outcome?> FindAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var entity = await db.Outcomes.AsNoTracking().FirstOrDefaultAsync(e => e.TurnId == turnId.Value, cancellationToken);
        return entity is null ? null : OutcomeEntityMapping.ToDomain(entity);
    }

    public async Task SaveAsync(Outcome outcome, CancellationToken cancellationToken)
    {
        db.Outcomes.Add(OutcomeEntityMapping.ToEntity(outcome));

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DiscardExpiredPayloadsAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken)
    {
        var cutoffTicks = now.UtcTicks;

        // The bounded set is selected first so one pass can never turn into an unbounded update, and
        // the discard itself runs as a single set-based statement rather than by loading Outcomes.
        var expiredTurnIds = await db.Outcomes
            .AsNoTracking()
            .Where(e => e.PayloadExpiresAtTicks != null && e.PayloadExpiresAtTicks < cutoffTicks)
            .OrderBy(e => e.PayloadExpiresAtTicks)
            .Take(maxCount)
            .Select(e => e.TurnId)
            .ToListAsync(cancellationToken);

        if (expiredTurnIds.Count == 0)
        {
            return 0;
        }

        return await db.Outcomes
            .Where(e => expiredTurnIds.Contains(e.TurnId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Payload, (string?)null)
                    .SetProperty(e => e.PayloadExpiresAtTicks, (long?)null),
                cancellationToken);
    }
}
