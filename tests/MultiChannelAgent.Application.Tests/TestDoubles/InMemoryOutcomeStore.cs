using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>Minimal in-memory <see cref="IOutcomeStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryOutcomeStore : IOutcomeStore
{
    private readonly Dictionary<Guid, Outcome> _outcomes = [];

    public Task<Outcome?> FindAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        _outcomes.TryGetValue(turnId.Value, out var outcome);
        return Task.FromResult(outcome);
    }

    public Task SaveAsync(Outcome outcome, CancellationToken cancellationToken)
    {
        _outcomes[outcome.TurnId.Value] = outcome;
        return Task.CompletedTask;
    }

    public Task<int> DiscardExpiredPayloadsAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken)
    {
        var expired = _outcomes.Values
            .Where(outcome => outcome.PayloadExpiresAt is { } expiresAt && expiresAt < now)
            .OrderBy(outcome => outcome.PayloadExpiresAt)
            .Take(maxCount)
            .ToList();

        foreach (var outcome in expired)
        {
            _outcomes[outcome.TurnId.Value] = outcome.WithoutRetainedPayload();
        }

        return Task.FromResult(expired.Count);
    }
}
