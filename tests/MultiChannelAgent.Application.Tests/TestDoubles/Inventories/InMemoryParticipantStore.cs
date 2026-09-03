using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>Minimal in-memory <see cref="IParticipantStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryParticipantStore : IParticipantStore
{
    private readonly Dictionary<ParticipantId, Participant> _participants = [];

    public IReadOnlyDictionary<ParticipantId, Participant> Participants => _participants;

    public Task UpsertAsync(Participant participant, CancellationToken cancellationToken)
    {
        _participants[participant.Id] = participant;
        return Task.CompletedTask;
    }
}
