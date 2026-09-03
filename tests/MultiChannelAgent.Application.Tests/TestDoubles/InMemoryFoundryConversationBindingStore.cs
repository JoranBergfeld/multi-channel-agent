using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IFoundryConversationBindingStore"/> for Application-layer unit tests.
/// A single lock makes "look up, then insert" one indivisible step per (Participant,
/// ChannelConversation) pair, mirroring the real store's unique-index race resolution, so concurrent
/// callers converge on one binding exactly like production.
/// </summary>
public sealed class InMemoryFoundryConversationBindingStore : IFoundryConversationBindingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(ParticipantId, ChannelConversationId), FoundryConversationBinding> _bindings = [];

    public IReadOnlyCollection<FoundryConversationBinding> Bindings => _bindings.Values;

    public Task<FoundryConversationBinding> GetOrCreateAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (participantId, channelConversationId);
            if (_bindings.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing);
            }

            var created = FoundryConversationBinding.CreateFirstGeneration(participantId, channelConversationId, now);
            _bindings[key] = created;
            return Task.FromResult(created);
        }
    }
}
