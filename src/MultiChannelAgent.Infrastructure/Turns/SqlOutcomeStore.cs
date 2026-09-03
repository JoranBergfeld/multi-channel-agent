using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

public sealed class SqlOutcomeStore(MultiChannelAgentDbContext db) : IOutcomeStore
{
    public async Task<Outcome?> FindAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var entity = await db.Outcomes.AsNoTracking().FirstOrDefaultAsync(e => e.TurnId == turnId.Value, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task SaveAsync(Outcome outcome, CancellationToken cancellationToken)
    {
        db.Outcomes.Add(new OutcomeEntity
        {
            TurnId = outcome.TurnId.Value,
            Status = outcome.Status == OutcomeStatus.Completed ? OutcomeEntityStatus.Completed : OutcomeEntityStatus.Failed,
            Code = outcome.Code,
            Summary = outcome.Summary,
            CreatedAt = outcome.CreatedAt,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Outcome ToDomain(OutcomeEntity entity) => new()
    {
        TurnId = new TurnId(entity.TurnId),
        Status = entity.Status == OutcomeEntityStatus.Completed ? OutcomeStatus.Completed : OutcomeStatus.Failed,
        Code = entity.Code,
        Summary = entity.Summary,
        CreatedAt = entity.CreatedAt,
    };
}
