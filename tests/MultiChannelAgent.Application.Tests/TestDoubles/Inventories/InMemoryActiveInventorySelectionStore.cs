using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>Minimal in-memory <see cref="IActiveInventorySelectionStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryActiveInventorySelectionStore : IActiveInventorySelectionStore
{
    private readonly Dictionary<(ParticipantId, string), ActiveInventorySelection> _selections = [];

    public IReadOnlyDictionary<(ParticipantId, string), ActiveInventorySelection> Selections => _selections;

    public Task<ActiveInventorySelection?> FindAsync(ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken)
    {
        _selections.TryGetValue((participantId, channelConversationId), out var selection);
        return Task.FromResult(selection);
    }

    public Task UpsertAsync(ActiveInventorySelection selection, CancellationToken cancellationToken)
    {
        _selections[(selection.ParticipantId, selection.ChannelConversationId)] = selection;
        return Task.CompletedTask;
    }

    public Task ClearAsync(ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken)
    {
        _selections.Remove((participantId, channelConversationId));
        return Task.CompletedTask;
    }
}
