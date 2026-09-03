using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>Durable store for the terminal semantic <see cref="Outcome"/> of each Turn.</summary>
public interface IOutcomeStore
{
    Task<Outcome?> FindAsync(TurnId turnId, CancellationToken cancellationToken);

    Task SaveAsync(Outcome outcome, CancellationToken cancellationToken);
}
