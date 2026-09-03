using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>Durable store for the terminal semantic <see cref="Outcome"/> of each Turn.</summary>
public interface IOutcomeStore
{
    Task<Outcome?> FindAsync(TurnId turnId, CancellationToken cancellationToken);

    Task SaveAsync(Outcome outcome, CancellationToken cancellationToken);

    /// <summary>
    /// Discards the retained payload of every Outcome whose payload expiry has passed, up to
    /// <paramref name="maxCount"/> of them, and reports how many were discarded. The Outcomes
    /// themselves - their category, code, and summary - are never removed.
    /// </summary>
    Task<int> DiscardExpiredPayloadsAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken);
}
