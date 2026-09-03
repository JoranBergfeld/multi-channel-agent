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
}
