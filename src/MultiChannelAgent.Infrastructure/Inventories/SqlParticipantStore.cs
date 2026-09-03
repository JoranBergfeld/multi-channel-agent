using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed durable store for canonical Participants. Idempotent by <see cref="Participant.Id"/>
/// (the Entra object ID): an existing row's display name refreshes rather than a duplicate row being
/// created.
/// </summary>
public sealed class SqlParticipantStore(MultiChannelAgentDbContext db, TimeProvider timeProvider) : IParticipantStore
{
    public async Task UpsertAsync(Participant participant, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await db.Participants.FirstOrDefaultAsync(p => p.Id == participant.Id.Value, cancellationToken);

        if (existing is null)
        {
            db.Participants.Add(new ParticipantEntity
            {
                Id = participant.Id.Value,
                DisplayName = participant.DisplayName,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.DisplayName = participant.DisplayName;
            existing.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Two concurrent first-sign-ins for the same Participant can both observe absence and
            // both reach the insert above; the primary key then lets exactly one commit. Converge on
            // the winner (whose display name is refreshed by whichever call runs next) rather than
            // leaking a raw duplicate-key failure to the caller.
            db.ChangeTracker.Clear();
            var winner = await db.Participants.FirstOrDefaultAsync(p => p.Id == participant.Id.Value, cancellationToken);
            if (winner is null)
            {
                throw;
            }
        }
    }
}
