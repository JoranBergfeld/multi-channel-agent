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
}
